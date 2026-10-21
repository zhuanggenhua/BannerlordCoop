using E2E.Tests.Util;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Session;
using GameInterface.Services.Heroes.Handlers;
using GameInterface.Services.Heroes.Messages;
using Moq;
using TaleWorlds.CampaignSystem;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Heroes
{
    public class CharacterRelationManagerSyncTests : SyncTestBase
    {
        private readonly string Hero1Id;
        private readonly string Hero2Id;

        public CharacterRelationManagerSyncTests(ITestOutputHelper output) : base(output)
        {
            Hero1Id = TestEnvironment.CreateRegisteredObject<Hero>();
            Hero2Id = TestEnvironment.CreateRegisteredObject<Hero>();
        }

        [Fact]
        public void Server_SetHeroRelation_SyncsToClients()
        {
            // SetHeroRelation funnels every relation write; the server postfix publishes it and clients re-apply.
            Server.Call(() =>
            {
                Assert.True(Server.ObjectManager.TryGetObject(Hero1Id, out Hero hero1));
                Assert.True(Server.ObjectManager.TryGetObject(Hero2Id, out Hero hero2));
                CharacterRelationManager.SetHeroRelation(hero1, hero2, 42);
            });

            foreach (var client in Clients)
            {
                client.Call(() =>
                {
                    Assert.True(client.ObjectManager.TryGetObject(Hero1Id, out Hero hero1));
                    Assert.True(client.ObjectManager.TryGetObject(Hero2Id, out Hero hero2));
                    Assert.Equal(42, CharacterRelationManager.GetHeroRelation(hero1, hero2));
                });
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ScopedRelationMessage_ActuallyAppliesAndCompletesWithoutEcho(bool queued)
        {
            Server.Call(() =>
            {
                Assert.True(Server.ObjectManager.TryGetObject(Hero1Id, out Hero hero1));
                Assert.True(Server.ObjectManager.TryGetObject(Hero2Id, out Hero hero2));
                using var broker = new MessageBroker();
                var identity = new SessionIdentity();
                var lease = new AuthorityLease("room", "authority", "a", 3);
                Assert.True(identity.TryApply(lease, "a"));
                using var binding = CampaignApplyCompletion.Attach(broker, identity);
                var completion = CampaignApplyCompletion.For(identity);
                var network = new Mock<INetwork>();
                using var handler = new CharacterRelationManagerHandler(broker, Server.ObjectManager, network.Object);
                int initialMessages = Server.InternalMessages.GetMessages<HeroRelationChanged>().Count();
                int previousGameThread = GameThread.Instance.GameThreadId;
                try
                {
                    if (queued) GameThread.Instance.UnmarkGameThread();
                    broker.Publish(this, new NetworkCampaignAuthorityHeroRelationChanged(Hero1Id, Hero2Id, 37));
                    if (queued)
                    {
                        Assert.Equal(CampaignApplyStatus.Pending, completion.GetStatusThrough(lease, out _));
                        Assert.NotEqual(37, CharacterRelationManager.GetHeroRelation(hero1, hero2));
                        GameThread.Instance.RestoreGameThread(previousGameThread);
                        GameThread.Instance.Update(TimeSpan.Zero);
                    }
                    Assert.Equal(37, CharacterRelationManager.GetHeroRelation(hero1, hero2));
                    Assert.True(completion.CanAcknowledgeThrough(lease, out _));
                    Assert.Equal(initialMessages, Server.InternalMessages.GetMessages<HeroRelationChanged>().Count());
                    network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
                }
                finally { GameThread.Instance.RestoreGameThread(previousGameThread); }
            });
        }

        [Fact]
        public void LegacyRelationMessage_ActuallyAppliesWithoutEcho()
        {
            Server.Call(() =>
            {
                Assert.True(Server.ObjectManager.TryGetObject(Hero1Id, out Hero hero1));
                Assert.True(Server.ObjectManager.TryGetObject(Hero2Id, out Hero hero2));
                using var broker = new MessageBroker();
                var network = new Mock<INetwork>();
                using var handler = new CharacterRelationManagerHandler(broker, Server.ObjectManager, network.Object);
                int initialMessages = Server.InternalMessages.GetMessages<HeroRelationChanged>().Count();
                broker.Publish(this, new NetworkHeroRelationChanged(Hero1Id, Hero2Id, -17));
                Assert.Equal(-17, CharacterRelationManager.GetHeroRelation(hero1, hero2));
                Assert.Equal(initialMessages, Server.InternalMessages.GetMessages<HeroRelationChanged>().Count());
                network.Verify(value => value.SendAll(It.IsAny<IMessage>()), Times.Never);
            });
        }

        [Fact]
        public void ScopedRelationMessage_DisposedHandlerCannotApplyOrAcknowledge()
        {
            Server.Call(() =>
            {
                Assert.True(Server.ObjectManager.TryGetObject(Hero1Id, out Hero hero1));
                Assert.True(Server.ObjectManager.TryGetObject(Hero2Id, out Hero hero2));
                int initial = CharacterRelationManager.GetHeroRelation(hero1, hero2);
                using var broker = new MessageBroker();
                var identity = new SessionIdentity();
                var lease = new AuthorityLease("room", "authority", "a", 3);
                Assert.True(identity.TryApply(lease, "a"));
                using var binding = CampaignApplyCompletion.Attach(broker, identity);
                var handler = new CharacterRelationManagerHandler(broker, Server.ObjectManager, Mock.Of<INetwork>());
                handler.Dispose();
                broker.Publish(this, new NetworkCampaignAuthorityHeroRelationChanged(Hero1Id, Hero2Id, 37));
                Assert.Equal(initial, CharacterRelationManager.GetHeroRelation(hero1, hero2));
                Assert.Equal(CampaignApplyStatus.Faulted,
                    CampaignApplyCompletion.For(identity).GetStatusThrough(lease, out var reason));
                Assert.Contains("No subscriber", reason);
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ScopedRelationMessage_MissingHeroCannotAcknowledge(bool firstHeroMissing)
        {
            Server.Call(() =>
            {
                using var broker = new MessageBroker();
                var identity = new SessionIdentity();
                var lease = new AuthorityLease("room", "authority", "a", 3);
                Assert.True(identity.TryApply(lease, "a"));
                using var binding = CampaignApplyCompletion.Attach(broker, identity);
                using var handler = new CharacterRelationManagerHandler(broker, Server.ObjectManager, Mock.Of<INetwork>());
                broker.Publish(this, new NetworkCampaignAuthorityHeroRelationChanged(
                    firstHeroMissing ? "missing-hero-first" : Hero1Id,
                    firstHeroMissing ? Hero2Id : "missing-hero-second", 37));
                Assert.Equal(CampaignApplyStatus.Faulted,
                    CampaignApplyCompletion.For(identity).GetStatusThrough(lease, out var reason));
                Assert.Contains(firstHeroMissing ? "missing-hero-first" : "missing-hero-second", reason);
            });
        }
    }
}
