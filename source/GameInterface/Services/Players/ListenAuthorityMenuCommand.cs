#if DEBUG
using Common;
using Common.Commands;
using SandBox.View.Map;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;

namespace GameInterface.Services.Players;

/// <summary>Bounded native navigation for testing local authority menus without a remote-client VM.</summary>
public sealed class ListenAuthorityMenuCommand : ICoopCommand
{
    public string Prefix => "coop.debug.listen";
    public string Name => "open_menu";
    public string Description => "Opens one native local-player menu through the map navigation handler.";
    public CoopCommandSide Side => CoopCommandSide.Server;
    public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
    {
        new ExpectedArgs("menu", "inventory, party, clan, escape, or close_escape", true),
    };

    public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
    {
        if (!ModInformation.IsListenAuthority || Game.Current?.GameStateManager.ActiveState is not MapState)
            return new CoopCommandResult(false, "An active ListenAuthority map is required", "wrong_state");
        var navigation = MapScreen.Instance?.NavigationHandler;
        if (navigation == null)
            return new CoopCommandResult(false, "Map navigation is not ready", "not_ready");
        switch (args[0])
        {
            case "inventory": navigation.OpenInventory(); break;
            case "party": navigation.OpenParty(); break;
            case "clan": navigation.OpenClan(); break;
            case "escape": navigation.OpenEscapeMenu(); break;
            case "close_escape":
                if (MapScreen.Instance.IsEscapeMenuOpened) MapScreen.Instance.CloseEscapeMenu();
                break;
            default: return new CoopCommandResult(false, "Unsupported menu", "invalid_arguments");
        }
        return new CoopCommandResult(true, $"Native {args[0]} navigation requested");
    }
}
#endif
