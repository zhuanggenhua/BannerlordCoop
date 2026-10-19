using Autofac;
using Common;
using Common.Util;
using Coop.Tests.Mocks;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.MobileParties.Interfaces;
using GameInterface.Services.MobileParties.Patches.Disable;
using Moq;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignAuthorityPrisonerSaleBehaviorTests
{
    private static readonly AuthorityLease WriterLease =
        new("room-a", "authority-b", "controller-b", 4);

    private static readonly AuthorityLease NextWriterLease =
        new("room-a", "authority-c", "controller-c", 5);

    static CampaignAuthorityPrisonerSaleBehaviorTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ListenersRegisterOnEveryReplica(bool legacyIsServer)
    {
        WithAuthority(CreateAuthorityContext(local: false, WriterLease).Object, legacyIsServer, () =>
        {
            Assert.True(DisablePartiesSellPrisonerCampaignBehavior.Prefix());
        });
    }

    [Fact]
    public void CurrentLeaseHolderRunsCallbackOnGameThread()
    {
        var context = CreateAuthorityContext(local: true, WriterLease);
        var behavior = new PartiesSellPrisonerCampaignBehaviorInterface(context.Object);
        using var completed = new ManualResetEventSlim(false);
        bool ranOnGameThread = false;

        WithAuthority(context.Object, legacyIsServer: false, () =>
        {
            behavior.RunIfCurrentAuthority(() =>
            {
                ranOnGameThread = GameThread.Instance.IsGameThread;
                completed.Set();
            });

            Assert.True(completed.Wait(TimeSpan.FromSeconds(5)), "the lease holder callback did not run");
        });

        Assert.True(ranOnGameThread);
        context.Verify(authority => authority.TryExecuteWithCurrentLease(WriterLease, It.IsAny<Action>()), Times.Once);
    }

    [Fact]
    public void ObserverCannotRunCallbackUnderAllowedThread()
    {
        var context = CreateAuthorityContext(local: false, WriterLease);
        var behavior = new PartiesSellPrisonerCampaignBehaviorInterface(context.Object);
        bool ran = false;

        WithAuthority(context.Object, legacyIsServer: true, () =>
        {
            using (new AllowedThread())
            {
                behavior.RunIfCurrentAuthority(() => ran = true);
            }
        });

        Assert.False(ran);
        context.Verify(authority => authority.TryExecuteWithCurrentLease(It.IsAny<AuthorityLease>(), It.IsAny<Action>()), Times.Never);
    }

    [Fact]
    public void QueuedCallbackIsDroppedAfterAuthorityLeaseChanges()
    {
        var stateGate = new object();
        AuthorityLease currentLease = WriterLease;
        int localLeaseChecks = 0;
        int executeCalls = 0;
        int callbackCalls = 0;
        using var recheckEntered = new ManualResetEventSlim(false);
        using var releaseRecheck = new ManualResetEventSlim(false);
        using var queueDrained = new ManualResetEventSlim(false);

        var context = new Mock<ICampaignAuthorityContext>();
        context.Setup(authority => authority.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lock (stateGate) lease = currentLease;
                return true;
            });
        context.Setup(authority => authority.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                if (Interlocked.Increment(ref localLeaseChecks) == 2)
                {
                    recheckEntered.Set();
                    if (!releaseRecheck.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("the lease recheck was not released");
                }

                lock (stateGate) lease = currentLease;
                return true;
            });
        context.Setup(authority => authority.TryExecuteWithCurrentLease(
                It.IsAny<AuthorityLease>(), It.IsAny<Action>()))
            .Returns((AuthorityLease lease, Action action) =>
            {
                Interlocked.Increment(ref executeCalls);
                lock (stateGate)
                {
                    if (currentLease != lease) return false;
                    action();
                    return true;
                }
            });

        var behavior = new PartiesSellPrisonerCampaignBehaviorInterface(context.Object);

        WithAuthority(context.Object, legacyIsServer: false, () =>
        {
            behavior.RunIfCurrentAuthority(() => Interlocked.Increment(ref callbackCalls));
            Assert.True(recheckEntered.Wait(TimeSpan.FromSeconds(5)), "the queued callback did not reach its lease recheck");

            lock (stateGate) currentLease = NextWriterLease;
            releaseRecheck.Set();

            GameThread.Run(() => queueDrained.Set(), blocking: true, label: "prisoner-sale-lease-test-drain");
            Assert.True(queueDrained.IsSet);
        });

        releaseRecheck.Set();
        Assert.Equal(0, callbackCalls);
        Assert.Equal(0, executeCalls);
    }

    [Fact]
    public void LegacyServerCanRunCallbackBeforeLeaseConfirmation()
    {
        var context = new Mock<ICampaignAuthorityContext>();
        context.Setup(authority => authority.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = default;
                return false;
            });
        context.Setup(authority => authority.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease lease) =>
            {
                lease = default;
                return false;
            });

        var behavior = new PartiesSellPrisonerCampaignBehaviorInterface(context.Object);
        using var completed = new ManualResetEventSlim(false);
        bool ranOnGameThread = false;

        WithAuthority(context.Object, legacyIsServer: true, () =>
        {
            behavior.RunIfCurrentAuthority(() =>
            {
                ranOnGameThread = GameThread.Instance.IsGameThread;
                completed.Set();
            });

            Assert.True(completed.Wait(TimeSpan.FromSeconds(5)), "the bootstrap callback did not run");
        });

        Assert.True(ranOnGameThread);
        context.Verify(authority => authority.TryExecuteWithCurrentLease(It.IsAny<AuthorityLease>(), It.IsAny<Action>()), Times.Never);
    }

    private static Mock<ICampaignAuthorityContext> CreateAuthorityContext(bool local, AuthorityLease lease)
    {
        var context = new Mock<ICampaignAuthorityContext>();
        context.Setup(authority => authority.TryGetCurrentLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease currentLease) =>
            {
                currentLease = lease;
                return true;
            });
        context.Setup(authority => authority.TryGetLocalAuthorityLease(out It.Ref<AuthorityLease>.IsAny))
            .Returns((out AuthorityLease localLease) =>
            {
                localLease = lease;
                return local;
            });
        context.Setup(authority => authority.TryExecuteWithCurrentLease(
                It.IsAny<AuthorityLease>(), It.IsAny<Action>()))
            .Returns((AuthorityLease currentLease, Action action) =>
            {
                if (currentLease != lease || !local) return false;
                action();
                return true;
            });
        return context;
    }

    private static void WithAuthority(ICampaignAuthorityContext context, bool legacyIsServer, Action assertion)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(context).As<ICampaignAuthorityContext>();
        using var container = builder.Build();

        var previousMode = ModInformation.SessionMode;
        bool previousIsServer = ModInformation.IsServer;
        bool hadPreviousContainer = ContainerProvider.TryGetContainer(out var previousContainer);
        try
        {
            ModInformation.IsServer = legacyIsServer;
            ContainerProvider.SetContainer(container);
            assertion();
        }
        finally
        {
            ModInformation.SessionMode = previousMode;
            ModInformation.IsServer = previousIsServer;
            if (hadPreviousContainer) ContainerProvider.SetContainer(previousContainer);
            else ContainerProvider.Clear();
        }
    }
}
