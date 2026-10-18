using Common;
using GameInterface.Services.CampaignAuthority;
using Moq;
using Serilog;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;
using ObjectManagerService = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.CampaignAuthority;

public class CampaignRosterCheckpointServiceTests
{
    static CampaignRosterCheckpointServiceTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);
    }

    [Fact]
    public void RestoresAuthorityXpAndOrderWithoutCollapsingDuplicateCharacterStacks()
    {
        var sourceManager = new ObjectManagerService(Mock.Of<ILogger>());
        var targetManager = new ObjectManagerService(Mock.Of<ILogger>());
        var sourceCharacter = RegisterCharacter(sourceManager);
        var targetCharacter = RegisterCharacter(targetManager);
        var source = RegisterRoster(sourceManager, 1,
            Element(sourceCharacter, 2, 0, 12), Element(sourceCharacter, 2, 0, 34), Element(sourceCharacter, 1, 1, 5));
        var target = RegisterRoster(targetManager, 1,
            Element(targetCharacter, 1, 1, 0), Element(targetCharacter, 2, 0, 999), Element(targetCharacter, 2, 0, 888));
        Assert.Equal(0, target.GetTroopRoster()[0].Xp);
        int version = target.VersionNo;

        Assert.True(new CampaignRosterCheckpointService(sourceManager).TryCapture(new[] { source }, out var checkpoint));
        Assert.True(new CampaignRosterCheckpointService(targetManager).TryApply(checkpoint, new[] { target }));

        Assert.Equal(new[] { 12, 34, 5 }, target.GetTroopRoster().Select(element => element.Xp));
        Assert.Equal(new[] { 2, 2, 1 }, target.GetTroopRoster().Select(element => element.Number));
        Assert.Equal(new[] { 0, 0, 1 }, target.GetTroopRoster().Select(element => element.WoundedNumber));
        Assert.All(target.GetTroopRoster(), element => Assert.Same(targetCharacter, element.Character));
        Assert.Equal(version + 1, target.VersionNo);
    }

    [Theory]
    [InlineData(999u, 2, 0, 100)]
    [InlineData(10u, 3, 0, 100)]
    [InlineData(10u, 2, 1, 100)]
    [InlineData(10u, 2, 0, -1)]
    public void RejectsUnknownCharacterCountsWoundsOrInvalidXpBeforeAnyApply(uint character, int number, int wounded, int xp)
    {
        var manager = new ObjectManagerService(Mock.Of<ILogger>());
        var troop = RegisterCharacter(manager);
        var first = RegisterRoster(manager, 1, Element(troop, 2, 0, 7));
        var second = RegisterRoster(manager, 2, Element(troop, 2, 0, 8));
        var checkpoint = new CampaignRosterCheckpoint(new[]
        {
            State(1, new CampaignRosterElementState(10, 2, 0, 500)),
            State(2, new CampaignRosterElementState(character, number, wounded, xp)),
        });

        Assert.False(new CampaignRosterCheckpointService(manager).TryApply(checkpoint, new[] { first, second }));
        Assert.Equal(7, first.data[0].Xp);
        Assert.Equal(8, second.data[0].Xp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMissingOrRepeatedWorldRoster(bool duplicate)
    {
        var manager = new ObjectManagerService(Mock.Of<ILogger>());
        var troop = RegisterCharacter(manager);
        var first = RegisterRoster(manager, 1, Element(troop, 2, 0, 7));
        var second = RegisterRoster(manager, 2, Element(troop, 2, 0, 8));
        var state = State(1, new CampaignRosterElementState(10, 2, 0, 500));
        var checkpoint = new CampaignRosterCheckpoint(duplicate ? new[] { state, state } : new[] { state });
        Assert.False(new CampaignRosterCheckpointService(manager).TryApply(checkpoint, new[] { first, second }));
        Assert.Equal(7, first.data[0].Xp);
    }

    [Fact]
    public void CaptureDoesNotSilentlyOmitUnregisteredCharacters()
    {
        var manager = new ObjectManagerService(Mock.Of<ILogger>());
        var roster = RegisterRoster(manager, 1, Element(new CharacterObject(), 2, 0, 7));
        Assert.False(new CampaignRosterCheckpointService(manager).TryCapture(new[] { roster }, out var checkpoint));
        Assert.Null(checkpoint);
    }

    private static CharacterObject RegisterCharacter(ObjectManagerService manager)
    {
        var character = new CharacterObject();
        Assert.True(manager.AddExisting("character", character, 10));
        return character;
    }

    private static TroopRoster RegisterRoster(ObjectManagerService manager, uint handle, params TroopRosterElement[] elements)
    {
        var roster = new TroopRoster { data = elements, _count = elements.Length };
        roster.UpdateVersion();
        Assert.True(manager.AddExisting("roster-" + handle, roster, handle));
        return roster;
    }

    private static TroopRosterElement Element(CharacterObject character, int number, int wounded, int xp) =>
        new TroopRosterElement(character) { _number = number, _woundedNumber = wounded, _xp = xp };
    private static CampaignRosterState State(uint roster, params CampaignRosterElementState[] elements) => new(roster, elements);
}
