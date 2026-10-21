using Common;
using Common.Network.Session;
using HarmonyLib;
using SandBox.View.Map;
using System;
using System.Collections.Generic;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade.ViewModelCollection.EscapeMenu;

namespace GameInterface.Services.UI.Patches;

/// <summary>
/// Adds "Invite Friends" as the second campaign-map escape-menu item for local players
/// in the session's Steam lobby.
/// </summary>
[HarmonyPatch(typeof(MapScreen), "GetEscapeMenuItems")]
internal class EscapeMenuInviteFriendsPatch
{
    [HarmonyPostfix]
    internal static void AddInviteFriendsItem(List<EscapeMenuItemVM> __result)
    {
        if (ModInformation.IsDedicatedAuthority) return;
        if (!ContainerProvider.TryResolve<ISessionAdvertiser>(out var advertiser)) return;
        if (!advertiser.CanInviteFriends) return;

        __result.Insert(1, new EscapeMenuItemVM(
            new TextObject("邀请 Steam 好友"),
            _ =>
            {
                if (!advertiser.InviteFriends())
                {
                    InformationManager.DisplayMessage(new InformationMessage("无法打开 Steam 好友邀请界面，请确认 Steam 在线且已启用游戏内覆盖。"));
                }
            },
            identifier: null,
            getIsDisabledAndReason: () => new Tuple<bool, TextObject>(false, new TextObject("")),
            isPositiveBehaviored: false));
    }
}
