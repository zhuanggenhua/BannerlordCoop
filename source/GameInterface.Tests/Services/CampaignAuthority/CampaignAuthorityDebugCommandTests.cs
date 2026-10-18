#if DEBUG
using Common.Commands;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ObjectManager;
using Moq;
using System;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority;

public class CampaignAuthorityDebugCommandTests
{
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
}
#endif
