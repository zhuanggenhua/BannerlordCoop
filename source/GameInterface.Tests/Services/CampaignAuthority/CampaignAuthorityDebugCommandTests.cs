#if DEBUG
using Common.Commands;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using Moq;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

public class CampaignAuthorityDebugCommandTests
{
    [Fact]
    public void StateMissionEvidenceSerializesNullWhenNoNativeMissionExists()
    {
        var evidence = CampaignAuthorityDebugCommands.StateCommand.CaptureMissionState(null);
        Assert.Null(evidence);
        var json = JObject.FromObject(new { mission = evidence });
        Assert.Equal(JTokenType.Null, json["mission"].Type);
    }

    [Theory]
    [InlineData("", null, "invalid_argument")]
    [InlineData("other", null, "invalid_argument")]
    [InlineData("create", null, "not_world_writer")]
    [InlineData("inspect", "absent_probe", "probe_absent")]
    public void ProbeFailureReturnsStructuredReasonWithoutThrowing(string operation, string id, string code)
    {
        var command = new CampaignAuthorityDebugCommands.AllocationProbeCommand(
            Mock.Of<IObjectManager>(), Mock.Of<ICampaignAuthorityContext>());
        var values = operation.Length == 0 ? Array.Empty<string>() :
            id == null ? new[] { operation } : new[] { operation, id };
        var result = command.ProcessCommand(new CoopCommandArgsFactory().FromValues(values));
        Assert.False(result.Succeeded);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public void RosterFailureDiagnosticsAreReadOnlyAndReportCappedMissingReferencesAndDuplicatePartyIds()
    {
        var objects = new Mock<IObjectManager>(MockBehavior.Strict);
        var registeredAa = new object();
        var registeredBb = new object();
        var missingAa = new object();
        var missingBb = new object();
        var firstPartyBase = new object();
        var secondPartyBase = new object();
        var firstCharacter = new CharacterObject();
        var secondCharacter = new CharacterObject();
        var thirdCharacter = new CharacterObject();
        SetupHandle(objects, registeredAa, 41);
        SetupHandle(objects, registeredBb, 42);
        SetupHandle(objects, missingAa, null);
        SetupHandle(objects, missingBb, null);
        SetupHandle(objects, firstPartyBase, null);
        SetupHandle(objects, secondPartyBase, null);
        SetupHandle(objects, firstCharacter, null);
        SetupHandle(objects, secondCharacter, null);
        SetupHandle(objects, thirdCharacter, null);

        var firstRoster = new TroopRoster
        {
            data = new[]
            {
                new TroopRosterElement(firstCharacter) { _number = -1 },
                new TroopRosterElement(secondCharacter),
            },
            _count = 2,
        };
        var secondRoster = new TroopRoster
        {
            data = new[] { new TroopRosterElement(thirdCharacter) },
            _count = 1,
        };
        SetupHandle(objects, firstRoster, null);
        SetupHandle(objects, secondRoster, null);
        var diagnostics = CampaignAuthorityDebugCommands.CampaignRosterFailureDiagnostics.Describe(
            objects.Object,
            new[]
            {
                new CampaignAuthorityDebugCommands.CampaignRosterDiagnosticPartyReference("aa", missingAa),
                new CampaignAuthorityDebugCommands.CampaignRosterDiagnosticPartyReference("aa", registeredAa),
                new CampaignAuthorityDebugCommands.CampaignRosterDiagnosticPartyReference("bb", missingBb),
                new CampaignAuthorityDebugCommands.CampaignRosterDiagnosticPartyReference("bb", registeredBb),
            },
            new[]
            {
                new CampaignAuthorityDebugCommands.CampaignRosterDiagnosticRosterReference(
                    "mobileParty", "aa", missingAa, firstPartyBase, "member", firstRoster),
                new CampaignAuthorityDebugCommands.CampaignRosterDiagnosticRosterReference(
                    "mobileParty", "bb", missingBb, secondPartyBase, "prison", secondRoster),
            },
            maxEntries: 1,
            maxCharactersPerRoster: 1);

        var json = JObject.FromObject(diagnostics);
        Assert.Equal(2, (int)json["missingPartyCount"]);
        Assert.Single((JArray)json["missingParties"]);
        Assert.True((bool)json["missingPartiesTruncated"]);
        Assert.Equal(2, (int)json["duplicatePartyIdCount"]);
        Assert.Single((JArray)json["duplicatePartyIds"]);
        Assert.True((bool)json["duplicatePartyIdsTruncated"]);
        Assert.Equal("aa", (string)json["duplicatePartyIds"][0]["id"]);
        Assert.Equal(JTokenType.Null, json["duplicatePartyIds"][0]["parties"][0]["handle"].Type);
        Assert.Equal(41u, (uint)json["duplicatePartyIds"][0]["parties"][1]["handle"]);
        Assert.Equal(2, (int)json["rosterIssueCount"]);
        Assert.Single((JArray)json["rosterIssues"]);
        Assert.True((bool)json["rosterIssuesTruncated"]);
        Assert.Equal("member", (string)json["rosterIssues"][0]["rosterKind"]);
        Assert.Equal(2, (int)json["rosterIssues"][0]["count"]);
        Assert.Equal(2, (int)json["rosterIssues"][0]["missingCharacterCount"]);
        Assert.True((bool)json["rosterIssues"][0]["missingCharactersTruncated"]);
        Assert.Equal(1, (int)json["rosterIssues"][0]["invalidElementCount"]);
        Assert.Equal(1, (int)json["invalidElementCount"]);
        Assert.Equal(3, (int)json["missingCharacterReferenceCount"]);
        Assert.Equal(2, firstRoster._count);
        Assert.Equal(-1, firstRoster.data[0].Number);
    }

    private static void SetupHandle(Mock<IObjectManager> objects, object instance, uint? handle)
    {
        uint value = handle ?? 0;
        objects.Setup(manager => manager.TryGetHandle(instance, out value)).Returns(handle.HasValue);
    }
}
#endif
