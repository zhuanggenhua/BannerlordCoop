using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.Tests.Utils;
using Coop.Core.Common.Configuration;
using Coop.Core.Server;
using Coop.Core.Server.Services.Session;
using Coop.Core.Common.Session;
using Coop.Steam;
using Coop.Tests.Steam;
using Moq;
using Xunit;

namespace Coop.Tests.Server.Services.Session;

[Collection(ModInformationRoleCollection.Name)]
public class ListenSteamSessionRegistrationTests
{
    [Fact]
    public void ListenHostWithoutSteamKeepsDirectIpCompositionAndNoAdvertisementHandler()
    {
        var previousMode = ModInformation.SessionMode;
        var previousSteam = SessionDiscovery.SteamAvailable;
        try
        {
            ModInformation.SessionMode = SessionMode.ListenAuthority;
            SessionDiscovery.SteamAvailable = false;
            var builder = new ContainerBuilder();
            builder.RegisterModule<ServerModule>();
            builder.RegisterModule<global::GameInterface.GameInterfaceModule>();
            using var container = builder.Build();
            Assert.IsType<NoopSessionAdvertiser>(container.Resolve<ISessionAdvertiser>());
            Assert.IsType<NoopSessionTunnelHost>(container.Resolve<ISessionTunnelHost>());
            Assert.Same(container.Resolve<ISessionTunnelHost>(), container.Resolve<ISessionTunnelIdentityResolver>());
            Assert.False(container.IsRegistered<IListenSessionAdvertisementHandler>());
            Assert.IsType<CoopServer>(container.Resolve<INetwork>());
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
            SessionDiscovery.SteamAvailable = previousSteam;
        }
    }

    [Fact]
    public void ListenCompositionSharesAdvertiserAndAuthenticatedTunnelWithoutClientOrGameServer()
    {
        var builder = new ContainerBuilder();
        ServerModule.RegisterListenSteamSessionServices(builder);
        builder.RegisterInstance(new FakeSteamLobbyApi()).As<ISteamLobbyApi>();
        builder.RegisterInstance(new Mock<ISteamTunnelTransport>().Object).As<ISteamTunnelTransport>();
        builder.RegisterInstance(new TestMessageBroker()).As<IMessageBroker>();
        builder.RegisterInstance(new Mock<INetwork>().Object).As<INetwork>();
        builder.RegisterInstance(new NetworkConfig()).As<INetworkConfig>();
        builder.Register(_ => new Common.Logging.ReceivePathDiagnostics()).As<Common.Logging.IReceivePathDiagnostics>();
        using var container = builder.Build();
        Assert.IsType<ListenSteamLobbyAdvertiser>(container.Resolve<ISessionAdvertiser>());
        Assert.Same(container.Resolve<ISessionAdvertiser>(), container.Resolve<ISteamLobbyOwner>());
        Assert.IsType<SteamTunnelHost>(container.Resolve<ISessionTunnelHost>());
        Assert.Same(container.Resolve<ISessionTunnelHost>(), container.Resolve<ISessionTunnelIdentityResolver>());
        Assert.IsType<ListenSessionAdvertisementHandler>(container.Resolve<IListenSessionAdvertisementHandler>());
        Assert.False(container.IsRegistered<Coop.Core.Client.ICoopClient>());
        Assert.False(container.IsRegistered<ServerSessionAdvertisementHandler>());
        Assert.Equal(0UL, container.Resolve<ISessionJoinInfoSource>().Get().ServerSteamId);
        GameThread.Run(() => { }, blocking: true);
    }
}
