using E2E.Tests.Environment;
using E2E.Tests.Util;
using Common;
using GameInterface.Services.Entity;
using GameInterface.Services.CampaignAuthority;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Party;
using Xunit.Abstractions;

namespace E2E.Tests.Services.MobilePartyAis;
public class MobilePartyAiLifetimeTests : IDisposable
{
    E2ETestEnvironment TestEnvironment { get; }

    public MobilePartyAiLifetimeTests(ITestOutputHelper output)
    {
        TestEnvironment = new E2ETestEnvironment(output);
    }

    public void Dispose()
    {
        TestEnvironment.Dispose();
    }

    [Fact]
    public void ServerCreate_MobilePartyAi_SyncAllClients()
    {
        // Arrange
        var server = TestEnvironment.Server;
        var mobilePartyId = TestEnvironment.CreateRegisteredObject<MobileParty>();

        // Act
        string? aiId = null;
        server.Call(() =>
        {
            Assert.True(server.ObjectManager.TryGetObject(mobilePartyId, out MobileParty mobileParty));
            Assert.True(server.ObjectManager.TryGetId(mobileParty, out _));
            var partyAi = new MobilePartyAi(mobileParty);

            Assert.True(server.ObjectManager.TryGetId(partyAi, out aiId));
        });

        // Assert
        TestEnvironment.PumpGameThreads();
        Assert.NotNull(aiId);

        foreach (var client in TestEnvironment.Clients)
        {
            Assert.True(client.ObjectManager.TryGetObject<MobilePartyAi>(aiId, out var clientAi));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var clientParty));
            Assert.Same(clientParty, clientAi._mobileParty);
        }
    }

    [Fact]
    public void ServerCreate_MobilePartyAi_UnregisteredOwner_SkipsAiRegistration()
    {
        var server = TestEnvironment.Server;

        server.Call(() =>
        {
            var mobileParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            Assert.True(server.ObjectManager.TryGetId(mobileParty, out _));
            Assert.True(server.ObjectManager.Remove(mobileParty));

            var partyAi = new MobilePartyAi(mobileParty);

            Assert.False(server.ObjectManager.TryGetId(partyAi, out _));
        });
        TestEnvironment.PumpGameThreads();
    }

    [Fact]
    public void RemoteProcessRoleWithConfirmedWriterCreatesNativeAiAndReplicatesItsIdentity()
    {
        var server = TestEnvironment.Server;
        var mobilePartyId = TestEnvironment.CreateRegisteredObject<MobileParty>();
        TestEnvironment.PumpGameThreads();
        var previousLease = server.Resolve<ISessionIdentity>().CurrentLease;
        var writerLease = new AuthorityLease(previousLease.RoomId, Guid.NewGuid().ToString("N"), "writer-b", previousLease.Epoch + 1);
        Assert.True(server.Resolve<ISessionIdentity>().TryApply(writerLease));
        foreach (var client in TestEnvironment.Clients)
            Assert.True(client.Resolve<ISessionIdentity>().TryApply(writerLease));
        string? aiId = null;
        server.Call(() =>
        {
            var identity = server.Resolve<ISessionIdentity>();
            var controller = server.Resolve<IControllerIdProvider>();
            var previousController = controller.ControllerId;
            var previousMode = ModInformation.SessionMode;
            try
            {
                controller.SetControllerId(identity.CurrentLease.AuthorityControllerId);
                ModInformation.SessionMode = SessionMode.RemoteReplica;
                Assert.True(server.Resolve<ICampaignAuthorityContext>().IsLocalCampaignAuthority);
                Assert.True(server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var party));
                var ai = new MobilePartyAi(party);
                Assert.Same(party, ai._mobileParty);
                Assert.NotNull(AccessTools.Field(typeof(MobilePartyAi), "_fleeingData").GetValue(ai));
                Assert.True(server.ObjectManager.TryGetId(ai, out aiId));
            }
            finally
            {
                ModInformation.SessionMode = previousMode;
                controller.SetControllerId(previousController);
            }
        });
        TestEnvironment.PumpGameThreads();
        Assert.NotNull(aiId);
        foreach (var client in TestEnvironment.Clients)
        {
            Assert.True(client.ObjectManager.TryGetObject<MobilePartyAi>(aiId, out var ai));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var party));
            Assert.Same(party, ai._mobileParty);
        }
    }
}
