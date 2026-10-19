using Autofac;
using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.Clans.Messages;
using GameInterface.Services.Clans.Patches;
using Moq;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using Xunit;

namespace GameInterface.Tests.Services.Clans;

[Collection(ModInformationRoleCollection.Name)]
public sealed class ClanCachePatchesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeLoadRebuildsCachesWithoutPublishingUnregisteredReferences(bool client)
    {
        using var fixture = new Fixture(client, true);
        Assert.True(ClanCachePatches.OnWarPartyAddedPrefix(ref fixture.Clan, fixture.Party));
        fixture.Clan.OnWarPartyAdded(fixture.Party);
        Assert.True(ClanCachePatches.OnWarPartyRemovedPrefix(ref fixture.Clan, fixture.Party));
        Assert.True(ClanCachePatches.OnSupporterNotableAddedPrefix(ref fixture.Clan, fixture.Hero));
        Assert.True(ClanCachePatches.OnSupporterNotableRemovedPrefix(ref fixture.Clan, fixture.Hero));
        Assert.Equal(0, fixture.Published);
        Assert.Contains(fixture.Party, fixture.Clan.WarPartyComponents);
    }

    [Fact]
    public void RunningAuthorityStillPublishesEachCacheMutation()
    {
        using var fixture = new Fixture(false, false);
        Assert.True(ClanCachePatches.OnWarPartyAddedPrefix(ref fixture.Clan, fixture.Party));
        fixture.Clan.OnWarPartyAdded(fixture.Party);
        Assert.True(ClanCachePatches.OnWarPartyRemovedPrefix(ref fixture.Clan, fixture.Party));
        Assert.True(ClanCachePatches.OnSupporterNotableAddedPrefix(ref fixture.Clan, fixture.Hero));
        Assert.True(ClanCachePatches.OnSupporterNotableRemovedPrefix(ref fixture.Clan, fixture.Hero));
        Assert.Equal(4, fixture.Published);
    }

    [Fact]
    public void ReceivedApplicationDoesNotPublishBackToAuthority()
    {
        using var fixture = new Fixture(false, false);
        using (new AllowedThread())
        {
            Assert.True(ClanCachePatches.OnSupporterNotableAddedPrefix(ref fixture.Clan, fixture.Hero));
            Assert.True(ClanCachePatches.OnSupporterNotableRemovedPrefix(ref fixture.Clan, fixture.Hero));
        }
        Assert.Equal(0, fixture.Published);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode mode = ModInformation.SessionMode;
        private readonly bool hadContainer;
        private readonly ILifetimeScope previous;
        private readonly IContainer container;
        private readonly Action<MessagePayload<WarPartyAdded>> added;
        private readonly Action<MessagePayload<WarPartyRemoved>> removed;
        private readonly Action<MessagePayload<SupporterNotableAdded>> supporterAdded;
        private readonly Action<MessagePayload<SupporterNotableRemoved>> supporterRemoved;
        public Clan Clan = ObjectHelper.SkipConstructor<Clan>();
        public readonly WarPartyComponent Party = ObjectHelper.SkipConstructor<LordPartyComponent>();
        public readonly Hero Hero = ObjectHelper.SkipConstructor<Hero>();
        public int Published;

        public Fixture(bool client, bool original)
        {
            hadContainer = ContainerProvider.TryGetContainer(out previous);
            ModInformation.SessionMode = client ? SessionMode.RemoteReplica : SessionMode.ListenAuthority;
            Clan.InitMembers();
            var policy = new Mock<ISyncPolicy>();
            policy.Setup(value => value.AllowOriginal()).Returns(original);
            var builder = new ContainerBuilder();
            builder.RegisterInstance(policy.Object).As<ISyncPolicy>();
            container = builder.Build();
            ContainerProvider.SetContainer(container);
            added = _ => Published++;
            removed = _ => Published++;
            supporterAdded = _ => Published++;
            supporterRemoved = _ => Published++;
            MessageBroker.Instance.Subscribe(added);
            MessageBroker.Instance.Subscribe(removed);
            MessageBroker.Instance.Subscribe(supporterAdded);
            MessageBroker.Instance.Subscribe(supporterRemoved);
        }

        public void Dispose()
        {
            MessageBroker.Instance.Unsubscribe(added);
            MessageBroker.Instance.Unsubscribe(removed);
            MessageBroker.Instance.Unsubscribe(supporterAdded);
            MessageBroker.Instance.Unsubscribe(supporterRemoved);
            ModInformation.SessionMode = mode;
            if (hadContainer) ContainerProvider.SetContainer(previous); else ContainerProvider.Clear();
            container.Dispose();
        }
    }
}
