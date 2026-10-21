using GameInterface.CoopSessionData.Save.Data;
using GameInterface.CoopSessionData;
using GameInterface.CoopSessionData.Messages;
using Common.Messaging;
using Common.Network;
using Moq;
using Serilog;
using System.Runtime.Serialization;
using TaleWorlds.CampaignSystem;
using GameInterface.Surrogates;
using ProtoBuf;
using System;
using System.IO;
using System.Text.Json;
using TaleWorlds.Core;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority.Battles;

internal static class BattlePreparationTestData
{
    internal static BattlePreparationState State(BattlePreparationPhase phase = BattlePreparationPhase.Retired)
    {
        new SurrogateCollection();
        using var stream = new MemoryStream();
        Serializer.Serialize(stream,new MissionInitializerRecord("initial_field_scene") { RandomTerrainSeed=1977,SceneLevels="level_1" });
        var party=new BattleObjectReference("party",12);
        var input=new BattlePreparationInput(party,new[]{new BattleCohortMember("A",party,"original-room-reservation")});
        var preparedParty=new BattlePreparedParty(new BattleObjectReference("event-party",13),party,1,1,1,
            new[]{new BattlePreparedTroop(new BattleObjectReference("character",14),false,937,0,12,3)});
        var side=new BattlePreparedSide(new BattleObjectReference("event-side",15),0,party,new[]{preparedParty.MapEventParty},
            new[]{new BattleReadyTroop(preparedParty.MapEventParty,937,BitConverter.SingleToInt32Bits(0.75f))},true);
        return new BattlePreparationState(records:new[]{new BattlePreparationRecord(
            new BattlePreparationKey("original-room","native-campaign",new BattleObjectReference("map-event",11),"original-room-claim",7),
            input,phase,3,new BattlePreparedPayload(new[]{preparedParty},new[]{side},stream.ToArray()),phase==BattlePreparationPhase.Unknown?"partial native failure":null)});
    }
    internal static CoopSession Session(BattlePreparationState state)
    {
        var e=CoopSession.Empty;
        return new CoopSession("save",e.Players,e.CraftingPlayerData,e.WorkshopPlayerData,e.CaravansPlayerData,e.AlleyPlayerData,
            e.InteractionsPlayerData,e.TradePlayerData,e.InventoryPlayerData,e.HeroMeetingData,e.AgingPlayerData,battlePreparations:state);
    }
}

public sealed class BattlePreparationStateTests
{
    [Fact]
    public void CompletedHistoryLookupIsStableAcrossEpochAndDoesNotGrantPermission()
    {
        var state=BattlePreparationTestData.State();var row=Assert.Single(state.Records);
        Assert.Equal(BattlePreparationLookup.AlreadyPrepared,state.Inspect(row.Key,row.Input));
        Assert.DoesNotContain(typeof(BattlePreparationKey).GetProperties(),p=>p.Name.Contains("Epoch")||p.Name.Contains("Request")||p.Name.Contains("Controller"));
        Assert.Equal(3,row.ExecutionEpoch);Assert.Equal("InitialOrdinaryField",row.Key.Operation);
        Assert.True(state.CanTransferHistory(out _));
        Assert.Equal(BattlePreparationLookup.Conflict,state.Inspect(row.Key,new BattlePreparationInput(new BattleObjectReference("other",1),row.Input.InitialCohort)));
        Assert.Equal(BattlePreparationLookup.Missing,state.Inspect(new BattlePreparationKey("original-room","native-campaign",row.Key.MapEvent,"new-room-claim",8),row.Input));
    }

    [Theory]
    [InlineData(BattlePreparationPhase.Preparing)][InlineData(BattlePreparationPhase.Prepared)][InlineData(BattlePreparationPhase.Unknown)]
    public void ActiveOrUnknownStateCannotPretendAuthAndCompletedDrainExist(BattlePreparationPhase phase)
    {
        var state=BattlePreparationTestData.State(phase);
        Assert.False(state.CanTransferHistory(out var reason));Assert.Contains("NOT_IMPLEMENTED",reason);
        if(phase==BattlePreparationPhase.Unknown)Assert.Equal(BattlePreparationLookup.Blocked,state.Inspect(state.Records[0].Key,state.Records[0].Input));
    }

    [Fact]
    public void ImmutableArraysAndSessionCopyCannotAliasCallerMutableStorage()
    {
        var state=BattlePreparationTestData.State();var records=state.Records;var payload=records[0].Prepared;
        var originalBytes=payload.MissionInitializer;var bytes=payload.MissionInitializer;bytes[0]^=255;
        records[0]=null;var parties=payload.Parties;parties[0]=null;
        Assert.NotNull(Assert.Single(state.Records));Assert.NotNull(Assert.Single(payload.Parties));Assert.Equal(originalBytes,payload.MissionInitializer);
        var session=BattlePreparationTestData.Session(state);Assert.NotSame(state,session.BattlePreparations);
    }

    [Fact]
    public void TypedHistoryAndActualInitializerSurviveJsonAndProtobufWithoutRoomRebinding()
    {
        var original=BattlePreparationTestData.Session(BattlePreparationTestData.State());
        var json=JsonSerializer.Deserialize<CoopSession>(JsonSerializer.Serialize(original,new JsonSerializerOptions {IncludeFields=true}),new JsonSerializerOptions {IncludeFields=true});
        var protobuf=Serializer.DeepClone(original);
        foreach(var restored in new[]{json,protobuf})
        {
            var row=Assert.Single(restored.BattlePreparations.Records);
            Assert.Equal("original-room",row.Key.RoomId);Assert.Equal(7,row.Key.ClaimGeneration);
            Assert.Equal(937,Assert.Single(Assert.Single(row.Prepared.Parties).Troops).UniqueSeed);
            Assert.Equal(12,Assert.Single(Assert.Single(row.Prepared.Parties).Troops).Xp);
            using var stream=new MemoryStream(row.Prepared.MissionInitializer);
            var initializer=Serializer.Deserialize<MissionInitializerRecord>(stream);
            Assert.Equal("initial_field_scene",initializer.SceneName);Assert.Equal(1977,initializer.RandomTerrainSeed);
            Assert.True(restored.BattlePreparations.CanTransferHistory(out _));
        }
    }

    [Fact]
    public void OriginalHeroMigrationAndNetworkEnvelopePreserveBattleLifetimeAndPreparedFacts()
    {
        var provider=new CoopSessionProvider {CoopSession=BattlePreparationTestData.Session(BattlePreparationTestData.State())};
        var objects=new global::GameInterface.Services.ObjectManager.ObjectManager(Mock.Of<ILogger>());
        var oldHero=(Hero)FormatterServices.GetUninitializedObject(typeof(Hero));
        var newHero=(Hero)FormatterServices.GetUninitializedObject(typeof(Hero));
        Assert.True(objects.AddExisting("old-hero",oldHero,50));Assert.True(objects.AddExisting("new-hero",newHero,51));
        provider.CoopSession.CraftingPlayerData.PlayerOpenNewPartXpDictionary["old-hero"]=new();
        var network=new Mock<INetwork>();NetworkUpdateCoopSession? delivered=null;
        network.Setup(value=>value.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(message=>
        {delivered=Serializer.DeepClone(Assert.IsType<NetworkUpdateCoopSession>(message));});
        new CoopSessionMigrator(provider,objects,network.Object).MigratePlayerData(oldHero,newHero);
        Assert.True(provider.CoopSession.CraftingPlayerData.PlayerOpenNewPartXpDictionary.ContainsKey("new-hero"));
        Assert.False(provider.CoopSession.CraftingPlayerData.PlayerOpenNewPartXpDictionary.ContainsKey("old-hero"));
        Assert.True(delivered.HasValue);
        var row=Assert.Single(delivered.Value.UpdatedSession.BattlePreparations.Records);
        Assert.Equal("original-room",row.Key.RoomId);Assert.Equal("original-room-claim",row.Key.ClaimId);
        Assert.Equal("party",row.Input.InitiatingParty.Id);Assert.Equal(937,row.Prepared.Parties[0].Troops[0].UniqueSeed);
    }

    [Fact]
    public void LegacyProtobufMissingMemberAndUnknownSchemaCannotShareMutableDefaults()
    {
        using var firstWire=new MemoryStream();using var secondWire=new MemoryStream();
        var first=Serializer.Deserialize<CoopSession>(firstWire);var second=Serializer.Deserialize<CoopSession>(secondWire);
        Assert.NotSame(first.BattlePreparations,second.BattlePreparations);Assert.Empty(first.BattlePreparations.Records);
        Assert.False(new BattlePreparationState(99).CanTransferHistory(out _));
        Assert.Equal(BattlePreparationLookup.Blocked,new BattlePreparationState(99).Inspect(null,null));
        var state=BattlePreparationTestData.State();var row=state.Records[0];
        Assert.False(new BattlePreparationState(records:new[]{row,row}).CanTransferHistory(out _));
    }
}
