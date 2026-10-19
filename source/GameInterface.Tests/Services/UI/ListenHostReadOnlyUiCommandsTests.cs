using Common;
using Common.Commands;
using Common.Util;
using GameInterface.Services.GameDebug.Commands;
using HarmonyLib;
using Moq;
using SandBox.View.Map;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.UI;

[Collection(ModInformationRoleCollection.Name)]
public sealed class ListenHostReadOnlyUiCommandsTests : IDisposable
{
    private readonly SessionMode priorMode = ModInformation.SessionMode;
    private readonly Campaign priorCampaign = Campaign.Current;
    private readonly Campaign campaign = ObjectHelper.SkipConstructor<Campaign>();
    private readonly Harmony harmony = new(nameof(ListenHostReadOnlyUiCommandsTests));

    public ListenHostReadOnlyUiCommandsTests()
    {
        Campaign.Current = campaign;
        harmony.Patch(AccessTools.PropertyGetter(typeof(MapScreen), nameof(MapScreen.Instance)),
            prefix: new HarmonyMethod(typeof(ListenHostReadOnlyUiCommandsTests), nameof(NoMapScreen)));
    }

    [Theory]
    [InlineData(SessionMode.ListenAuthority, true)]
    [InlineData(SessionMode.ListenAuthority, false)]
    [InlineData(SessionMode.RemoteReplica, true)]
    [InlineData(SessionMode.RemoteReplica, false)]
    public void NativeReadOnlyCommandsReachAvailabilityChecksForLocalPlayers(SessionMode mode, bool evidence)
    {
        ModInformation.SessionMode = mode;
        ICoopCommand command = evidence ? new UiDebugCommands.UiEvidenceMapStateCoopCommand()
            : new UiDebugCommands.UiMapMovementStateCoopCommand();
        var registry = new CoopCommandRegistry(new[] { command }, Mock.Of<ILogger>());

        var result = registry.ProcessCommand(command.Prefix + "." + command.Name,
            new CoopCommandArgsFactory().FromValues(Array.Empty<string>()));

        Assert.Equal(CoopCommandSide.Both, command.Side);
        Assert.False(result.Succeeded);
        Assert.NotEqual("command_wrong_side", result.ErrorCode);
        Assert.DoesNotContain("Run this command on a client", result.Output);
        Assert.Contains(evidence ? "map screen is unavailable" : "no active campaign", result.Output);
        Assert.Same(campaign, Campaign.Current);
        Assert.Null(campaign.MainParty);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DedicatedAuthorityStillRejectsNativeLocalPlayerUiReads(bool evidence)
    {
        ModInformation.SessionMode = SessionMode.DedicatedAuthority;
        ICoopCommand command = evidence ? new UiDebugCommands.UiEvidenceMapStateCoopCommand()
            : new UiDebugCommands.UiMapMovementStateCoopCommand();
        var result = command.ProcessCommand(new CoopCommandArgsFactory().FromValues(Array.Empty<string>()));
        Assert.False(result.Succeeded);
        Assert.Contains("Run this command on a client", result.Output);
        Assert.Same(campaign, Campaign.Current);
        Assert.Null(campaign.MainParty);
    }

    [Fact]
    public void MutableEvidencePreparationRemainsClientOnly()
    {
        Assert.Equal(CoopCommandSide.Client, new UiDebugCommands.UiPrepareEvidenceMapCoopCommand().Side);
    }

    private static bool NoMapScreen(ref MapScreen __result)
    {
        __result = null;
        return false;
    }

    public void Dispose()
    {
        harmony.UnpatchAll(harmony.Id);
        Campaign.Current = priorCampaign;
        ModInformation.SessionMode = priorMode;
    }
}
