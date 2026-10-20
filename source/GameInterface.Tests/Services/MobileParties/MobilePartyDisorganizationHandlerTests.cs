using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Session;
using Common.PacketHandlers;
using Common.Serialization;
using Common.Util;
using Coop.Core.Client.Services.Session;
using Coop.Core.Common.Network;
using Coop.Tests.Mocks;
using GameInterface.Policies;
using GameInterface.Registry;
using GameInterface.Registry.Auto;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using GameInterface.Services.MobileParties.Handlers;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.MobileParties.Patches;
using GameInterface.Services.ObjectManager;
using Moq;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem;
using Xunit;
using ObjectManagerService = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.MobileParties;

[Collection(ModInformationRoleCollection.Name)]
public sealed class MobilePartyDisorganizationHandlerTests
{
    static MobilePartyDisorganizationHandlerTests() => RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void TestRetirementFixtureIsNotDiscoveredAsProductionRegistry()
    {
        var registries=(IEnumerable<Type>)AccessTools.Method(typeof(RegistryModule),"GetRegistries").Invoke(new RegistryModule(),null);
        Assert.DoesNotContain(registries,type=>type.DeclaringType==typeof(MobilePartyDisorganizationHandlerTests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerResultIsPublishedBeforeSameReferenceOriginalRetirementWithoutWaitingForGameThread(bool remoteWriter)
    {
        using var f = remoteWriter ? new Fixture("b","b",SessionMode.RemoteReplica) : new Fixture();
        using var entered = new ManualResetEventSlim();using var release = new ManualResetEventSlim();
        GameThread.EnqueueSafe(() => { entered.Set();Assert.True(release.Wait(Timeout)); });
        try
        {
            Assert.True(entered.Wait(Timeout));
            Assert.True(Task.Run(() =>
            {
                f.Broker.Publish(this, new MobilePartyDisorganizationChanged(f.Party, true));
                f.Broker.Publish(this, new InstanceDestroyed<MobileParty>(f.Party));
            }).Wait(Timeout));
            Assert.False(f.Objects.TryGetHandle(f.Party, out _));
        }
        finally { release.Set(); }
        GameThread.Run(() => { }, blocking: true);
        Assert.Collection(f.Sent,
            result => { var value = Assert.IsType<NetworkMobilePartyDisorganizationChanged>(result);Assert.Equal(77u,value.PartyId);Assert.True(value.IsDisorganized); },
            result => Assert.Equal(77u,Assert.IsType<NetworkDestroyInstance<MobileParty>>(result).InstanceHandle));
        Assert.DoesNotContain(f.Logger.Invocations, call => call.Method.Name == "Error");
    }

    [Theory]
    [InlineData("observer-a")]
    [InlineData("observer-b")]
    [InlineData("frozen")]
    [InlineData("paused")]
    public void OnlyConfirmedUnfrozenWriterCanRunNativeChecksSetOrPublish(string state)
    {
        using var f = state == "observer-a" ? new Fixture("a","b") : new Fixture("b",state == "observer-b" ? "a" : "b",SessionMode.RemoteReplica);
        if(state=="frozen") Assert.True(f.Identity.TryFreezeLocalAuthority(f.Identity.CurrentLease,1));
        if(state=="paused") Assert.True(f.Identity.TryApply(new AuthorityLease("room","paused","",4,true)));
        Assert.False(Prefix("CheckPrefix"));Assert.False(Prefix("SetPrefix"));
        f.Broker.Publish(this,new MobilePartyDisorganizationChanged(f.Party,true));
        Postfix(f.Party,true);Assert.Empty(f.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostAndRemoteWritersKeepOneNativeSetterAndPublishItsOriginalResult(bool remote)
    {
        using var f=remote?new Fixture("b","b",SessionMode.RemoteReplica):new Fixture();
        Assert.True(Prefix("CheckPrefix"));Assert.True(Prefix("SetPrefix"));
        f.Party._isDisorganized=true;f.Party.VersionNo=100;f.Party._disorganizedUntilTime=new CampaignTime(123456);
        f.Party.SetDisorganized(false);Postfix(f.Party,true);
        Assert.False(f.Party.IsDisorganized);Assert.Equal(101,f.Party.VersionNo);Assert.Equal(123456,f.Party._disorganizedUntilTime.NumTicks);
        var result=Assert.IsType<NetworkMobilePartyDisorganizationChanged>(Assert.Single(f.Sent));Assert.False(result.IsDisorganized);Assert.Equal(77u,result.PartyId);
    }

    [Theory]
    [InlineData(SessionMode.DedicatedAuthority,true)]
    [InlineData(SessionMode.ListenAuthority,true)]
    [InlineData(SessionMode.RemoteReplica,false)]
    public void UnknownLeaseKeepsOriginalInitializationRoleBehavior(SessionMode mode,bool canWrite)
    {
        using var f=new Fixture(mode:mode,known:false);
        Assert.Equal(canWrite,Prefix("CheckPrefix"));Assert.Equal(canWrite,Prefix("SetPrefix"));
        f.Broker.Publish(this,new MobilePartyDisorganizationChanged(f.Party,true));
        Assert.Equal(canWrite?1:0,f.Sent.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeaseChangedOrFrozenAfterCaptureCannotRepackageOldResult(bool freeze)
    {
        using var f=new Fixture(authorityFactory:(identity,controller)=>
        {
            var real=new CampaignAuthorityContext(identity,controller);var lease=identity.CurrentLease;
            var proxy=new Mock<ICampaignAuthorityContext>();
            proxy.Setup(a=>a.TryGetCurrentLease(out lease)).Returns(true);
            proxy.Setup(a=>a.TryGetLocalAuthorityLease(out lease)).Returns(true);
            proxy.Setup(a=>a.TryExecuteWithCurrentLease(lease,It.IsAny<Action>())).Returns((AuthorityLease current,Action action)=>
            {
                if(freeze) Assert.True(identity.TryFreezeLocalAuthority(current,1));
                else Assert.True(identity.TryApply(new AuthorityLease("room","next","a",4)));
                return real.TryExecuteWithCurrentLease(current,action);
            });
            return proxy.Object;
        });
        f.Broker.Publish(this,new MobilePartyDisorganizationChanged(f.Party,true));
        Assert.Empty(f.Sent);Assert.True(f.Objects.TryGetHandle(f.Party,out _));
    }

    [Fact]
    public void ReceiveUpdatesObserverExactlyOnceWithoutReplacingExpiryAndNeverEchoes()
    {
        using var f=new Fixture("a","b");f.Party.VersionNo=100;f.Party._disorganizedUntilTime=new CampaignTime(123456);
        f.Broker.Publish(this,new NetworkMobilePartyDisorganizationChanged(77,true));GameThread.Run(()=>{},blocking:true);
        Assert.True(f.Party.IsDisorganized);Assert.Equal(101,f.Party.VersionNo);Assert.Equal(123456,f.Party._disorganizedUntilTime.NumTicks);
        f.Broker.Publish(this,new NetworkMobilePartyDisorganizationChanged(77,true));GameThread.Run(()=>{},blocking:true);
        Assert.Equal(101,f.Party.VersionNo);Assert.Empty(f.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedReplicaApplyUsesRoleAtGameThreadExecution(bool becomeWriter)
    {
        using var f=new Fixture("a",becomeWriter?"b":"a");
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        GameThread.EnqueueSafe(()=>{entered.Set();Assert.True(release.Wait(Timeout));});
        try
        {
            Assert.True(entered.Wait(Timeout));
            f.Broker.Publish(this,new NetworkMobilePartyDisorganizationChanged(77,true));
            Assert.True(f.Identity.TryApply(new AuthorityLease("room","next",becomeWriter?"a":"b",4)));
        }
        finally{release.Set();}
        GameThread.Run(()=>{},blocking:true);Assert.Equal(!becomeWriter,f.Party.IsDisorganized);Assert.Empty(f.Sent);
    }

    [Fact]
    public void MissingIdentityStaysVisibleAndIsNeverRegisteredOrPublishedAgain()
    {
        using var f=new Fixture();Assert.True(f.Objects.Remove(f.Party));
        f.Broker.Publish(this,new MobilePartyDisorganizationChanged(f.Party,true));
        Assert.Contains(f.Logger.Invocations,call=>call.Method.Name=="Error");Assert.Empty(f.Sent);
        Assert.False(f.Objects.TryGetHandle(f.Party,out _));
    }

    [Fact]
    public void SkippedOriginalAllowedThreadAndSyncPolicyBypassNeverPublishDuplicateResults()
    {
        using var f=new Fixture();Postfix(f.Party,false);Assert.Empty(f.Sent);
        using(new AllowedThread()){Assert.True(Prefix("SetPrefix"));Postfix(f.Party,true);f.Broker.Publish(this,new MobilePartyDisorganizationChanged(f.Party,true));}
        Assert.Empty(f.Sent);
        f.Sync.Setup(p=>p.AllowOriginal()).Returns(true);Postfix(f.Party,true);Assert.Empty(f.Sent);
        f.Sync.Setup(p=>p.AllowOriginal()).Returns(false);Postfix(f.Party,true);
        Assert.IsType<NetworkMobilePartyDisorganizationChanged>(Assert.Single(f.Sent));
    }

    [Fact]
    public void OriginalSenderAndEnvelopeRejectBareDuplicateStaleAndWrongWriterResults()
    {
        using var f=new Fixture("a","b");var serializer=new ProtoBufSerializer(new SerializableTypeMapper());
        var writer=new ControllerIdProvider();writer.SetControllerId("b");
        var sender=new CampaignAuthorityMessageSender(serializer,new CampaignAuthorityContext(f.Identity,writer));
        var message=new NetworkMobilePartyDisorganizationChanged(77,true);Assert.IsAssignableFrom<ICampaignAuthorityScopedMessage>(message);
        MessagePacket packet=default;sender.Send(message,value=>packet=value);
        var envelope=serializer.Deserialize<NetworkCampaignAuthoritySyncEnvelope>(packet.Data);
        Assert.Equal(3,envelope.Epoch);Assert.Equal(1,envelope.Sequence);Assert.Equal("b",envelope.AuthorityControllerId);
        var inner=serializer.Deserialize<NetworkMobilePartyDisorganizationChanged>(envelope.Payload);Assert.Equal(77u,inner.PartyId);Assert.True(inner.IsDisorganized);
        int deliveries=0;Action<MessagePayload<NetworkMobilePartyDisorganizationChanged>> observed=_=>deliveries++;
        f.Broker.Subscribe(observed);
        using var envelopes=new CampaignAuthoritySyncEnvelopeClientHandler(f.Broker,f.Identity,serializer);
        using var packetHandler=new MessagePacketHandler(f.Broker,Mock.Of<IPacketManager>(),serializer);
        packetHandler.PublishEvent(new TestNetwork().CreatePeer(),message);GameThread.Run(()=>{},blocking:true);Assert.Equal(0,deliveries);
        f.Broker.Publish(this,envelope);GameThread.Run(()=>{},blocking:true);Assert.True(f.Party.IsDisorganized);Assert.Equal(1,deliveries);
        var opposite=serializer.Serialize(new NetworkMobilePartyDisorganizationChanged(77,false));
        f.Broker.Publish(this,envelope);
        f.Broker.Publish(this,new NetworkCampaignAuthoritySyncEnvelope("room","old","b",2,99,opposite));
        f.Broker.Publish(this,new NetworkCampaignAuthoritySyncEnvelope("room","world","other",3,99,opposite));
        GameThread.Run(()=>{},blocking:true);Assert.Equal(1,deliveries);Assert.True(f.Party.IsDisorganized);Assert.Empty(f.Sent);
        GC.KeepAlive(observed);
    }

    private static bool Prefix(string name)=>(bool)AccessTools.Method(typeof(MobilePartyDisorganizationPatch),name).Invoke(null,null);
    private static void Postfix(MobileParty party,bool ran)=>AccessTools.Method(typeof(MobilePartyDisorganizationPatch),"SetPostfix").Invoke(null,new object[]{party,ran});

    private sealed class Fixture : IDisposable
    {
        private readonly SessionMode previous = ModInformation.SessionMode;
        private readonly ILifetimeScope previousContainer;
        private readonly MessageBroker previousBroker;
        private readonly ILifetimeScope scope;
        private readonly AutoRegistryHandler<MobileParty> retirement;
        private readonly MobilePartyDisorganizationHandler handler;
        internal readonly Mock<ILogger> Logger = new();
        internal readonly MessageBroker Broker = new();
        internal readonly ObjectManagerService Objects;
        internal readonly ConcurrentQueue<IMessage> Sent = new();
        internal readonly MobileParty Party = (MobileParty)RuntimeHelpers.GetUninitializedObject(typeof(MobileParty));
        internal readonly SessionIdentity Identity = new();
        internal readonly ControllerIdProvider Controller = new();
        internal readonly Mock<INetwork> Network = new();
        internal readonly Mock<ISyncPolicy> Sync=new();
        internal Fixture(string local="a",string writer="a",SessionMode mode=SessionMode.ListenAuthority,bool known=true,
            Func<SessionIdentity,ControllerIdProvider,ICampaignAuthorityContext> authorityFactory=null)
        {
            ContainerProvider.TryGetContainer(out previousContainer);
            previousBroker=(MessageBroker)AccessTools.Field(typeof(MessageBroker),"instance").GetValue(null);
            AccessTools.Field(typeof(MessageBroker),"instance").SetValue(null,Broker);
            ModInformation.SessionMode=mode;Controller.SetControllerId(local);
            if(known) Assert.True(Identity.TryApply(new AuthorityLease("room","world",writer,3),"a"));
            var builder=new ContainerBuilder();builder.RegisterInstance(authorityFactory?.Invoke(Identity,Controller)??new CampaignAuthorityContext(Identity,Controller)).As<ICampaignAuthorityContext>();
            builder.RegisterInstance(Sync.Object).As<ISyncPolicy>();scope=builder.Build();ContainerProvider.SetContainer(scope);
            Objects=new ObjectManagerService(Logger.Object);Party.StringId="Created_2827";Assert.True(Objects.AddExisting("party",Party,77));
            Network.Setup(n=>n.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(Sent.Enqueue);
            retirement=new AutoRegistryHandler<MobileParty>(new NoopRegistry<Fixture>(Logger.Object,Objects),Broker,Network.Object,Objects);
            handler=new MobilePartyDisorganizationHandler(Broker,Objects,Network.Object);
        }
        public void Dispose()
        {
            try { GameThread.Run(()=>{},blocking:true);handler.Dispose();retirement.Dispose();Broker.Dispose(); }
            finally
            {
                if (previousContainer == null) ContainerProvider.Clear();else ContainerProvider.SetContainer(previousContainer);
                AccessTools.Field(typeof(MessageBroker),"instance").SetValue(null,previousBroker);
                ModInformation.SessionMode=previous;scope.Dispose();
            }
        }
    }
    private sealed class NoopRegistry<TFixture> : AutoRegistryBase<MobileParty>
    {
        internal NoopRegistry(ILogger logger,IObjectManager objects) : base(logger,Mock.Of<IAutoRegistryFactory>(),objects) { }
        public override IEnumerable<MethodBase> Constructors=>Array.Empty<MethodBase>();
        public override IEnumerable<MethodBase> DestroyMethods=>Array.Empty<MethodBase>();
        public override void RegisterAllObjects() { }
        public override void OnClientCreated(MobileParty p,string id) { }
        public override void OnClientDestroyed(MobileParty p,string id) { }
        public override void OnServerCreated(MobileParty p,string id) { }
        public override void OnServerDestroyed(MobileParty p,string id) { }
    }
}
