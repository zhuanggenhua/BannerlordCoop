using Autofac;
using Common;
using Common.Commands;
using GameInterface.Services.Locations.Commands;
using GameInterface.Services.ObjectManager;
using Moq;
using Xunit;

namespace GameInterface.Tests.Services.Locations;

[Collection(ModInformationRoleCollection.Name)]
public class LocationLocalPlayerCommandTests
{
    [Theory]
    [InlineData(SessionMode.ListenAuthority, false)]
    [InlineData(SessionMode.RemoteReplica, false)]
    [InlineData(SessionMode.DedicatedAuthority, true)]
    public void NativeLocationEntryRequiresPlayerInsteadOfTransportClient(SessionMode mode, bool noPlayer)
    {
        var previousMode = ModInformation.SessionMode;
        bool hadContainer = ContainerProvider.TryGetContainer(out var previous);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(Mock.Of<IObjectManager>()).As<IObjectManager>();
        using var container = builder.Build();
        try
        {
            ModInformation.SessionMode = mode;
            ContainerProvider.SetContainer(container);
            var enter = new LocationDebugCommand.EnterLocationCoopCommand();
            var result = enter.ProcessCommand(new CoopCommandArgsFactory().FromValues(new[] { "absent_location" }));
            Assert.False(result.Succeeded);
            Assert.Equal(noPlayer, result.Output == "A local player is required.");
            Assert.Equal(CoopCommandSide.Both, enter.Side);
            Assert.Equal(CoopCommandSide.Both, new LocationDebugCommand.LeaveLocationCoopCommand().Side);
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
            if (hadContainer) ContainerProvider.SetContainer(previous);
            else ContainerProvider.Clear();
        }
    }
}
