using Common;
using Common.Util;
using GameInterface.Services.ObjectManager;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.CampaignAuthority;

public interface ICampaignRosterCheckpointService
{
    bool TryCapture(out CampaignRosterCheckpoint checkpoint);
    bool TryApply(CampaignRosterCheckpoint checkpoint);
}

internal sealed class CampaignRosterCheckpointService : ICampaignRosterCheckpointService
{
    private readonly IObjectManager objectManager;
    public CampaignRosterCheckpointService(IObjectManager objectManager) { this.objectManager = objectManager; }

    public bool TryCapture(out CampaignRosterCheckpoint checkpoint)
    {
        checkpoint = null;
        return Campaign.Current?.MobileParties != null && TryCapture(GetWorldRosters(), out checkpoint);
    }

    public bool TryApply(CampaignRosterCheckpoint checkpoint) =>
        Campaign.Current?.MobileParties != null && TryApply(checkpoint, GetWorldRosters());

    private static IEnumerable<TroopRoster> GetWorldRosters()
    {
        foreach (var party in Campaign.Current.MobileParties)
        {
            yield return party.MemberRoster;
            yield return party.PrisonRoster;
        }
        foreach (var settlement in Settlement.All)
        {
            yield return settlement.Party.MemberRoster;
            yield return settlement.Party.PrisonRoster;
        }
    }

    internal bool TryCapture(IEnumerable<TroopRoster> rosters, out CampaignRosterCheckpoint checkpoint)
    {
        checkpoint = null;
        var states = new List<CampaignRosterState>();
        foreach (var roster in rosters.Distinct())
        {
            if (!IsValidRoster(roster) || !objectManager.TryGetHandleWithLogging(roster, out var handle)) return false;
            var elements = new List<CampaignRosterElementState>();
            for (int index = 0; index < roster._count; index++)
            {
                var element = roster.data[index];
                if (element.Character == null || !objectManager.TryGetHandleWithLogging(element.Character, out var character) ||
                    !ValidCounts(element.Number, element.WoundedNumber, element.Xp)) return false;
                elements.Add(new CampaignRosterElementState(character, element.Number, element.WoundedNumber, element.Xp));
            }
            states.Add(new CampaignRosterState(handle, elements));
        }
        checkpoint = new CampaignRosterCheckpoint(states);
        return true;
    }

    internal bool TryApply(CampaignRosterCheckpoint checkpoint, IEnumerable<TroopRoster> worldRosters)
    {
        if (checkpoint?.Rosters == null) return false;
        var remaining = new HashSet<TroopRoster>(worldRosters);
        var prepared = new List<(TroopRoster roster, TroopRosterElement[] elements)>();
        foreach (var state in checkpoint.Rosters)
        {
            if (state?.Elements == null || !objectManager.TryGetObject<TroopRoster>(state.RosterHandle, out var roster) ||
                !IsValidRoster(roster) || !remaining.Remove(roster) || roster._count != state.Elements.Length) return false;

            var counts = new Dictionary<(CharacterObject character, int number, int wounded), int>();
            for (int index = 0; index < roster._count; index++)
            {
                var element = roster.data[index];
                var key = (element.Character, element.Number, element.WoundedNumber);
                counts.TryGetValue(key, out int duplicates);
                counts[key] = duplicates + 1;
            }
            var elements = new TroopRosterElement[System.Math.Max(4, state.Elements.Length)];
            for (int index = 0; index < state.Elements.Length; index++)
            {
                var element = state.Elements[index];
                if (!ValidCounts(element.Number, element.Wounded, element.Xp) ||
                    !objectManager.TryGetObject<CharacterObject>(element.CharacterHandle, out var character)) return false;
                var key = (character, element.Number, element.Wounded);
                if (!counts.TryGetValue(key, out int duplicates) || duplicates <= 0) return false;
                counts[key] = duplicates - 1;
                elements[index] = new TroopRosterElement(character)
                { _number = element.Number, _woundedNumber = element.Wounded, _xp = element.Xp };
            }
            prepared.Add((roster, elements));
        }
        if (remaining.Count != 0) return false;

        // All identities, counts and wounds match before any roster changes. Restore XP and array
        // order directly; SetElementXp would invoke PartyBase.OnXpChanged again.
        using (new AllowedThread())
        foreach (var entry in prepared)
        {
            entry.roster.data = entry.elements;
            entry.roster.UpdateVersion();
        }
        return true;
    }

    private static bool IsValidRoster(TroopRoster roster) =>
        roster?.data != null && roster._count >= 0 && roster._count <= roster.data.Length;
    private static bool ValidCounts(int number, int wounded, int xp) =>
        number >= 0 && wounded >= 0 && wounded <= number && xp >= 0;
}
