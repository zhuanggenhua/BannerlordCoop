using Common;
using Common.Messaging;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Heroes.Messages.Collections;
using GameInterface.Services.TroopRosters.Messages;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.ViewModelCollection.GameMenu.Recruitment;

namespace GameInterface.Services.TroopRosters.Patches;

[HarmonyPatch(typeof(RecruitmentVM))]
public class RecruitmentVMPatches
{
    [HarmonyPatch("OnDone")]
    [HarmonyPrefix]
    public static bool OnDonePrefix(ref RecruitmentVM __instance, out (Hero, CharacterObject, int)[] __state)
    {
        __state = null;
        ContainerProvider.TryResolve<ICampaignAuthorityContext>(out var authority);
        var troopsInCart = __instance.TroopsInCart.Select(t => (t.Owner.OwnerHero, t.Character, t.Index)).ToArray();
        // The room host can be an observer, and a remote player can be the world writer.
        // An active writer uses the native operation once, with all mutation patches live.
        if (ShouldUseNativeRecruitment(authority, ModInformation.IsServer))
        {
            // Another player may have consumed a slot since this UI was opened. Vanilla OnDone
            // trusts its cart; fence stale or duplicate slots before it changes troops and gold.
            if (troopsInCart.Length == 0 ||
                (MobileParty.MainParty?.CurrentSettlement != null &&
                 troopsInCart.All(value => value.OwnerHero?.CurrentSettlement == MobileParty.MainParty.CurrentSettlement) &&
                 AreVolunteerSlotsCurrent(troopsInCart)))
            {
                __state = troopsInCart;
                return true;
            }
            __instance.Deactivate();
            return false;
        }

        AuthorityLease lease = default;
        authority?.TryGetCurrentLease(out lease);
        var message = new RecruitmentAttempted(MobileParty.MainParty, troopsInCart, lease);

        MessageBroker.Instance.Publish(__instance, message);

        __instance.Deactivate();

        return false;
    }

    [HarmonyPatch("OnDone")]
    [HarmonyPostfix]
    public static void OnDonePostfix((Hero, CharacterObject, int)[] __state)
    {
        if (__state == null) return;
        // Native OnDone accesses the volunteer through item.Owner.OwnerHero. Its IL shape does not
        // guarantee the generic Hero array transpiler will see a local Hero immediately before ldfld.
        // Publish the resulting absolute slot values explicitly; never replay the recruitment itself.
        PublishConsumedVolunteerSlots(__state);
    }

    internal static void PublishConsumedVolunteerSlots(IEnumerable<(Hero hero, CharacterObject troop, int index)> cart)
    {
        foreach (var (hero, troop, index) in cart)
        {
            if (hero?.VolunteerTypes == null || index < 0 || index >= hero.VolunteerTypes.Length ||
                hero.VolunteerTypes[index] != null) continue;
            MessageBroker.Instance.Publish(hero, new VolunteerTypesArrayUpdated(hero, null, index));
        }
    }

    internal static bool ShouldUseNativeRecruitment(ICampaignAuthorityContext authority, bool legacyIsServer)
    {
        if (authority?.IsAuthorityKnown != true) return legacyIsServer;
        return authority.TryGetLocalAuthorityLease(out var lease) && lease.IsValid && !lease.IsPaused;
    }

    internal static bool AreVolunteerSlotsCurrent(IEnumerable<(Hero hero, CharacterObject troop, int index)> cart)
    {
        var slots = new HashSet<(Hero, int)>();
        foreach (var (hero, troop, index) in cart)
        {
            if (hero?.VolunteerTypes == null || troop == null || index < 0 ||
                index >= hero.VolunteerTypes.Length || hero.VolunteerTypes[index] != troop ||
                !slots.Add((hero, index))) return false;
        }
        return true;
    }
}
