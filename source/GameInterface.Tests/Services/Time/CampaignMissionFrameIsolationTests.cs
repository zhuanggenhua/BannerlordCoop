using Common;
using GameInterface.Services.Time.Patches;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace GameInterface.Tests.Services.Time;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignMissionFrameIsolationTests
{
    [Theory]
    [InlineData(SessionMode.ListenAuthority)]
    [InlineData(SessionMode.RemoteReplica)]
    public void MissionStackNeverReceivesAnAdditionalCampaignFrame(SessionMode mode)
    {
        var previous = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = mode;
            var manager = new GameStateManager(null, default);
            var map = new CountingMapState();
            manager._gameStates.Add(map);
            manager._gameStates.Add((MissionState)RuntimeHelpers.GetUninitializedObject(typeof(MissionState)));
            GameStateManagerPatches.Prefix(ref manager, .016f);
            Assert.Equal(0, map.Frames);
            Assert.Equal(2, manager._gameStates.Count);
        }
        finally { ModInformation.SessionMode = previous; }
    }

    [Fact]
    public void ActiveMapHasOneNativeFrameAndNoAdditionalAdapterFrame()
    {
        var previous = ModInformation.SessionMode;
        try
        {
            ModInformation.SessionMode = SessionMode.RemoteReplica;
            var manager = new GameStateManager(null, default);
            var map = new CountingMapState(); manager._gameStates.Add(map);
            GameStateManagerPatches.Prefix(ref manager, .016f);
            Assert.Equal(0, map.Frames);
            manager.OnTick(.016f);
            Assert.Equal(1, map.Frames);
        }
        finally { ModInformation.SessionMode = previous; }
    }

    private sealed class CountingMapState : MapState
    {
        public int Frames;
        public override void OnTick(float dt) { Frames++; }
    }
}
