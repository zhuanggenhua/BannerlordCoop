using Common;
using GameInterface.Services.CampaignAuthority.NativeState;
using GameInterface.Services.ObjectManager;
using Moq;
using ProtoBuf;
using Serilog;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using Xunit;
using NativeObjects = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.CampaignAuthority.NativeState;

public class CampaignNativeBehaviorCheckpointServiceTests
{
    [Fact]
    public void AgingRoundtripRestoresAbsolutePrivateValuesUsingLocalRegistryReferences()
    {
        var source = new Fixture();
        var target = new Fixture();
        source.Aging._gameStartDay = 197;
        source.Aging._extraLivesContainer[source.Hero] = 3;
        source.Aging._heroesYoungerThanHeroComesOfAge[source.Hero] = 17;
        Assert.True(source.Service.TryCapture(source.Aging, out var checkpoint));
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, new CampaignSessionCheckpoint("campaign", "{}", checkpoint));
        stream.Position = 0;
        var roundtrip = Serializer.Deserialize<CampaignSessionCheckpoint>(stream);
        Assert.NotSame(checkpoint, roundtrip.NativeBehaviors);
        source.Aging._extraLivesContainer[source.Hero] = 99;
        Assert.True(target.Service.TryPrepare(roundtrip.NativeBehaviors, target.Aging, out var install));
        Assert.Empty(target.Aging._extraLivesContainer);
        install();
        Assert.Equal(197, target.Aging._gameStartDay);
        Assert.Equal(3, target.Aging._extraLivesContainer[target.Hero]);
        Assert.Equal(17, target.Aging._heroesYoungerThanHeroComesOfAge[target.Hero]);
        Assert.False(target.Aging._extraLivesContainer.ContainsKey(source.Hero));
        Assert.Equal(3, checkpoint.Aging.ExtraLives[0].Value);
    }

    [Fact]
    public void EmptyAgingDictionariesRemainValidAfterProtobufRoundtrip()
    {
        var source = new Fixture();
        Assert.True(source.Service.TryCapture(source.Aging, out var checkpoint));
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, checkpoint);
        stream.Position = 0;
        var copy = Serializer.Deserialize<CampaignNativeBehaviorCheckpoint>(stream);
        Assert.True(source.Service.TryPrepare(copy, source.Aging, out var install));
        install();
        Assert.Empty(source.Aging._extraLivesContainer);
        Assert.Empty(source.Aging._heroesYoungerThanHeroComesOfAge);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("negative")]
    [InlineData("day")]
    [InlineData("null")]
    [InlineData("zero")]
    public void AgingRejectsEveryInvalidReferenceAndValueBeforeMutatingNativeState(string failure)
    {
        var fixture = new Fixture();
        fixture.Aging._gameStartDay = 80;
        fixture.Aging._extraLivesContainer[fixture.Hero] = 9;
        var lives = new[] { new CampaignHeroIntState(failure == "missing" ? 99u : failure == "zero" ? 0u : 7u,
            failure == "negative" ? -1 : 4) };
        var young = failure == "duplicate"
            ? new[] { new CampaignHeroIntState(7, 12), new CampaignHeroIntState(7, 13) }
            : new[] { new CampaignHeroIntState(7, 12) };
        var state = new CampaignAgingBehaviorState(failure == "day" ? -1 : 5, failure == "null" ? null! : lives, young);
        Assert.False(fixture.Service.TryPrepare(new CampaignNativeBehaviorCheckpoint(state), fixture.Aging, out var install));
        Assert.Null(install);
        Assert.Equal(80, fixture.Aging._gameStartDay);
        Assert.Equal(9, fixture.Aging._extraLivesContainer[fixture.Hero]);
        Assert.Empty(fixture.Aging._heroesYoungerThanHeroComesOfAge);
    }

    [Fact]
    public void MissingCaptureHandleDoesNotCreateAGhostOrReturnAPartialCheckpoint()
    {
        var fixture = new Fixture();
        fixture.Aging._extraLivesContainer[(Hero)RuntimeHelpers.GetUninitializedObject(typeof(Hero))] = 2;
        Assert.False(fixture.Service.TryCapture(fixture.Aging, out var checkpoint));
        Assert.Null(checkpoint);
        Assert.Equal(1, fixture.Aging._extraLivesContainer.Count);
    }

    private sealed class Fixture
    {
        internal readonly Hero Hero = (Hero)RuntimeHelpers.GetUninitializedObject(typeof(Hero));
        internal readonly AgingCampaignBehavior Aging = new();
        internal readonly NativeObjects Objects = new(Mock.Of<ILogger>());
        internal readonly CampaignNativeBehaviorCheckpointService Service;
        internal Fixture()
        {
            Assert.True(Objects.AddExisting("Hero_young", Hero, 7));
            Service = new CampaignNativeBehaviorCheckpointService(Objects);
        }
    }
}
