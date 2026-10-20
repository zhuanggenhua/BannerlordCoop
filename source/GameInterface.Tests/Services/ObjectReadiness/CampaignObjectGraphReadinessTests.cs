using Autofac;
using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Registry.Auto;
using GameInterface.AutoSync;
using GameInterface.AutoSync.Builders;
using GameInterface.Services.TroopRosters.Handlers;
using GameInterface.Services.TroopRosters.Messages;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.ItemRosters.Handlers;
using GameInterface.Services.ItemRosters.Messages;
using GameInterface.Services.ObjectReadiness;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PartyBases;
using GameInterface.Services.TroopRosters;
using GameInterface.Utils;
using GameInterface.Utils.NetworkEvents;
using Moq;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;
using Objects = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.ObjectReadiness;

[Collection(nameof(ModInformationRoleCollection))]
public sealed class CampaignObjectGraphReadinessTests
{
    private static readonly Lazy<Assembly> generatedHandlers = new(GenerateHandlers);
    static CampaignObjectGraphReadinessTests() =>
        RuntimeHelpers.RunModuleConstructor(typeof(Coop.Tests.Mocks.TestNetwork).Module.ModuleHandle);

    [Fact]
    public void FixtureHandlersAreExcludedFromProductionDiscovery()
    {
        var handlers = InterfaceCollector.GetInterfaces<IHandler>("GameInterface").ToArray();
        Assert.DoesNotContain(typeof(OwnerReferences<>), handlers);
        Assert.DoesNotContain(typeof(PartyReferences<>), handlers);
    }

    [Fact]
    public void TempRosterWindowRefusesCaptureUntilRealCreateAndReferenceMessagesApply()
    {
        using var fixture = new Fixture();
        bool beforeCreate = true, afterCreate = true, afterMembers = true, final = false;
        OnGame(() =>
        {
            fixture.Begin();
            Task.Run(() =>
            {
                GameThread.Run(() => beforeCreate = fixture.Ready());
                fixture.CreateAttachments();
                GameThread.Run(() => afterCreate = fixture.Ready());
                fixture.BindOwnerAndItems();
                fixture.BindMember();
                GameThread.Run(() => afterMembers = fixture.Ready());
                fixture.BindPrison();
                GameThread.Run(() => final = fixture.Ready());
            }).GetAwaiter().GetResult();
        });
        OnGame(() => { });
        Assert.False(beforeCreate);
        Assert.False(afterCreate);
        Assert.False(afterMembers);
        Assert.True(final);
        Assert.Equal(1, fixture.Publications);
        OnGame(() =>
        {
            Assert.True(new CampaignRosterCheckpointService(fixture.Objects).TryCapture(
                new[] { fixture.Party.MemberRoster, fixture.Party.PrisonRoster }, out var checkpoint));
            Assert.Equal(2, checkpoint.Rosters.Length);
            Assert.NotSame(fixture.TempOwner, fixture.Party.Party);
        });
    }

    [Fact]
    public void CompleteHandlesWithoutBackReferencesCannotPublishOrAcknowledge()
    {
        using var fixture = new Fixture();
        OnGame(() => fixture.Begin());
        fixture.CreateAttachments();
        fixture.BindOwnerAndItems();
        fixture.BindMember(false);
        fixture.BindPrison(false);
        OnGame(() =>
        {
            Assert.False(fixture.Ready());
            Assert.Equal(0, fixture.Publications);
            fixture.Party.MemberRoster.OwnerParty = fixture.Party.Party;
            Assert.False(fixture.Ready());
            fixture.Party.PrisonRoster.OwnerParty = fixture.Party.Party;
            Assert.True(fixture.Ready());
        });
        Assert.Equal(1, fixture.Publications);
    }

    [Fact]
    public void RepeatedApplyAndPublicationSignalsDoNotRegisterWorldTwice()
    {
        using var fixture = new Fixture();
        fixture.Complete();
        OnGame(() =>
        {
            fixture.Readiness.BeginReplicaCreation(fixture.Party);
            fixture.Readiness.PublishReplicaWhenReady(fixture.Party, () => fixture.Publications++);
            for (int index = 0; index < 10; index++) fixture.Readiness.ReferencesApplied();
            Assert.True(fixture.Ready());
        });
        Assert.Equal(1, fixture.Publications);
    }

    [Fact]
    public void MissingFinalReferenceNeverBecomesReadyThroughRepeatedChecks()
    {
        using var fixture = new Fixture();
        OnGame(() => fixture.Begin());
        fixture.CreateAttachments();
        fixture.BindOwnerAndItems();
        fixture.BindMember();
        OnGame(() =>
        {
            for (int index = 0; index < 100; index++)
            {
                Assert.False(fixture.Readiness.TryEnsureReady(out var reason));
                Assert.Contains("creation-pending", reason);
            }
        });
        Assert.Equal(0, fixture.Publications);
    }

    [Fact]
    public void PublicationFailureLatchesAndIsNeverRetriedOrAcknowledged()
    {
        using var fixture = new Fixture();
        int attempts = 0;
        OnGame(() => fixture.Begin(() => { attempts++; throw new InvalidOperationException("world-add-failure"); }));
        fixture.CreateAttachments();
        fixture.BindOwnerAndItems();
        fixture.BindMember();
        fixture.BindPrison();
        OnGame(() =>
        {
            for (int index = 0; index < 5; index++)
            {
                Assert.False(fixture.Readiness.TryEnsureReady(out var reason));
                Assert.Contains("publication-fault", reason);
            }
        });
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void ACommittedGraphDoesNotClassifyAnUnknownMissingRosterAsPending()
    {
        using var fixture = new Fixture();
        fixture.Complete();
        OnGame(() =>
        {
            Assert.True(fixture.Ready());
            fixture.Party.Party.MemberRoster = new TroopRoster();
            Assert.True(fixture.Ready());
            Assert.False(new CampaignRosterCheckpointService(fixture.Objects).TryCapture(
                new[] { fixture.Party.MemberRoster }, out var checkpoint));
            Assert.Null(checkpoint);
        });
    }

    [Fact]
    public void UnknownUntrackedPartyIsNotGrantedPendingStatus()
    {
        using var fixture = new Fixture();
        OnGame(() =>
        {
            Assert.True(fixture.Ready());
            Assert.False(new CampaignRosterCheckpointService(fixture.Objects).TryCapture(
                new[] { fixture.Party.MemberRoster }, out var checkpoint));
            Assert.Null(checkpoint);
            Assert.Contains(fixture.ObjectLog.Invocations, invocation => invocation.Method.Name == "Error" &&
                invocation.Arguments.OfType<string>().Any(text => text.Contains("Failed to get network handle")));
        });
    }

    [Fact]
    public void ANewReplicaAfterReconnectMustProveItsOwnGraph()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        first.Complete();
        OnGame(() =>
        {
            second.Begin();
            Assert.True(first.Ready());
            Assert.False(second.Ready());
        });
        second.CreateAttachments();
        second.BindOwnerAndItems();
        second.BindMember();
        second.BindPrison();
        OnGame(() => Assert.True(second.Ready()));
        Assert.Equal(1, second.Publications);
    }

    [Fact]
    public void CannotReleasePendingCreationBeforeRegistryRemoval()
    {
        using var fixture = new Fixture();
        OnGame(() =>
        {
            fixture.Begin();
            fixture.Readiness.CompleteReplicaRemoval(fixture.Party);
            Assert.False(fixture.Ready());
            fixture.Objects.Remove(fixture.Party);
            fixture.Readiness.CompleteReplicaRemoval(fixture.Party);
            Assert.True(fixture.Ready());
        });
        Assert.Equal(0, fixture.Publications);
    }

    [Fact]
    public void NetworkThreadCannotInspectOrCommitGameObjects()
    {
        using var fixture = new Fixture();
        Assert.False(GameThread.Instance.IsGameThread);
        Assert.Throws<InvalidOperationException>(() => fixture.Ready());
        Assert.Throws<InvalidOperationException>(() => fixture.Readiness.BeginReplicaCreation(fixture.Party));
    }

    [Fact]
    public void ActualGeneratedReferenceHandlersAndOwnerHandlerCommitOnlyACompleteRegisteredGraph()
    {
        using var fixture = new Fixture();
        var network = new Mock<INetwork>();
        var registry = new AutoSyncRegistry();
        var assembly = generatedHandlers.Value;
        using var mobile = (IDisposable)Activator.CreateInstance(assembly.GetType("AutoSync.ReadinessMobileHandler")!,
            fixture.Broker, fixture.Objects, network.Object, registry)!;
        using var party = (IDisposable)Activator.CreateInstance(assembly.GetType("AutoSync.ReadinessPartyHandler")!,
            fixture.Broker, fixture.Objects, network.Object, registry)!;
        using var owners = new TroopRosterOwnerPartyHandler(fixture.Broker, network.Object, fixture.Objects);
        OnGame(() => fixture.Begin());
        fixture.CreateAttachments();
        Publish("MobileParty_Party", 1, 2);
        Publish("PartyBase_MobileParty", 2, 1);
        Publish("PartyBase_ItemRoster", 2, 3);
        Publish("PartyBase_MemberRoster", 2, 4);
        Publish("PartyBase_PrisonRoster", 2, 5);
        OnGame(() => Assert.False(fixture.Ready()));
        fixture.Broker.Publish(this, new NetworkPartyOwnerSet("TroopRoster_members", "PartyBase_owner"));
        fixture.Broker.Publish(this, new NetworkPartyOwnerSet("TroopRoster_prisoners", "PartyBase_owner"));
        OnGame(() =>
        {
            Assert.True(fixture.Ready());
            Assert.Equal(1, fixture.Publications);
            Assert.Same(fixture.Party.Party, fixture.Party.MemberRoster.OwnerParty);
            Assert.Same(fixture.Party.Party, fixture.Party.PrisonRoster.OwnerParty);
        });
        network.Verify(instance => instance.SendAll(It.IsAny<IMessage>()), Times.Never);

        void Publish(string name, uint instance, uint value)
        {
            var type = assembly.GetType("AutoSync." + name + "_SetNetworkMessage", true)!;
            var message = Activator.CreateInstance(type, instance, value)!;
            typeof(IMessageBroker).GetMethod(nameof(IMessageBroker.Publish))!.MakeGenericMethod(type)
                .Invoke(fixture.Broker, new[] { this, message });
        }
    }

    [Fact]
    public void DirectFieldApplyCompletesAtOriginalGameThreadDrainBoundaryWithoutSetterSignals()
    {
        using var fixture = new Fixture();
        OnGame(() => fixture.Begin());
        fixture.CreateAttachments();
        OnGame(() =>
        {
            Assert.True(fixture.Objects.TryGetObject<PartyBase>(2u, out var owner));
            Assert.True(fixture.Objects.TryGetObject<ItemRoster>(3u, out var items));
            Assert.True(fixture.Objects.TryGetObject<TroopRoster>(4u, out var members));
            Assert.True(fixture.Objects.TryGetObject<TroopRoster>(5u, out var prisoners));
            SetBackingReference(fixture.Party, nameof(MobileParty.Party), owner);
            SetBackingReference(owner, nameof(PartyBase.MobileParty), fixture.Party);
            SetBackingReference(owner, nameof(PartyBase.ItemRoster), items);
            SetBackingReference(owner, nameof(PartyBase.MemberRoster), members);
            SetBackingReference(owner, nameof(PartyBase.PrisonRoster), prisoners);
            members.OwnerParty = owner;
            prisoners.OwnerParty = owner;
            Assert.Equal(0, fixture.Publications);
            fixture.Readiness.ReferencesApplied();
            Assert.Equal(1, fixture.Publications);
            Assert.True(fixture.Ready());
        });
    }

    [Fact]
    public void DestroyBeforeReadyCancelsPublicationButKeepsFenceUntilRegistryRemoval()
    {
        using var fixture = new Fixture();
        OnGame(() =>
        {
            fixture.Begin();
            fixture.Readiness.CancelReplicaPublication(fixture.Party);
        });
        fixture.CreateAttachments();
        fixture.BindOwnerAndItems();
        fixture.BindMember();
        fixture.BindPrison();
        OnGame(() =>
        {
            Assert.False(fixture.Ready());
            fixture.Readiness.CompleteReplicaRemoval(fixture.Party);
            Assert.False(fixture.Ready());
            fixture.Objects.Remove(fixture.Party);
            Assert.True(fixture.Ready());
        });
        Assert.Equal(0, fixture.Publications);
    }

    [Fact]
    public void ModuleSharesStateWithinSessionAndDoesNotLeakItIntoNewScope()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(Mock.Of<ILogger>());
        builder.RegisterType<Objects>().As<IObjectManager>().InstancePerLifetimeScope();
        builder.RegisterModule<ObjectReadinessModule>();
        using var container = builder.Build();
        using var first = container.BeginLifetimeScope();
        using var second = container.BeginLifetimeScope();
        Assert.Same(first.Resolve<ICampaignObjectGraphReadiness>(), first.Resolve<ICampaignObjectGraphReadiness>());
        Assert.NotSame(first.Resolve<ICampaignObjectGraphReadiness>(), second.Resolve<ICampaignObjectGraphReadiness>());
    }

    [Fact]
    public void AReferencePublishedBeforeItsCreateRemainsAnErrorAndCannotAcknowledge()
    {
        using var fixture = new Fixture();
        OnGame(() => fixture.Begin());
        fixture.BindOwnerAndItems();
        fixture.CreateAttachments();
        fixture.BindMember();
        fixture.BindPrison();
        OnGame(() =>
        {
            Assert.False(fixture.Ready());
            Assert.Equal(0, fixture.Publications);
            Assert.Contains(fixture.ObjectLog.Invocations, invocation => invocation.Method.Name == "Error");
        });
    }

    [Fact]
    public void ReloadingCampaignDiscardsOldCallbacksAndCannotPublishOldObjects()
    {
        using var fixture = new Fixture();
        OnGame(() =>
        {
            var previous = Campaign.Current;
            try
            {
                Campaign.Current = ObjectHelper.SkipConstructor<Campaign>();
                fixture.Begin();
                Assert.False(fixture.Ready());
                Campaign.Current = ObjectHelper.SkipConstructor<Campaign>();
                Assert.True(fixture.Ready());
                Assert.Throws<InvalidOperationException>(() => fixture.Readiness.PublishReplicaWhenReady(
                    fixture.Party, () => fixture.Publications++));
                Assert.Equal(0, fixture.Publications);
            }
            finally { Campaign.Current = previous; }
        });
    }

    [Fact]
    public void DestroyDuringWorldPublicationKeepsFenceUntilRemovalActuallyCommits()
    {
        using var fixture = new Fixture();
        OnGame(() => fixture.Begin(() =>
        {
            fixture.Publications++;
            fixture.Readiness.CancelReplicaPublication(fixture.Party);
        }));
        fixture.CreateAttachments();
        fixture.BindOwnerAndItems();
        fixture.BindMember();
        fixture.BindPrison();
        OnGame(() =>
        {
            Assert.False(fixture.Ready());
            fixture.Readiness.CompleteReplicaRemoval(fixture.Party);
            Assert.False(fixture.Ready());
            fixture.Objects.Remove(fixture.Party);
            Assert.True(fixture.Ready());
        });
        Assert.Equal(1, fixture.Publications);
    }

    [Fact]
    public void DisposedSessionCannotExecuteLatePublicationCallbacks()
    {
        using var fixture = new Fixture();
        OnGame(() => fixture.Begin());
        fixture.Readiness.Dispose();
        OnGame(() =>
        {
            Assert.Throws<ObjectDisposedException>(() => fixture.Ready());
            Assert.Throws<ObjectDisposedException>(() => fixture.Readiness.ReferencesApplied());
            Assert.Throws<ObjectDisposedException>(() => fixture.Readiness.PublishReplicaWhenReady(
                fixture.Party, () => fixture.Publications++));
        });
        Assert.Equal(0, fixture.Publications);
    }

    [Fact]
    public void ProductionPostfixUsesCurrentSessionAfterRealQueueDrainWithoutSetterEvents()
    {
        using var fixture = new Fixture();
        OnGame(() => fixture.Begin());
        fixture.CreateAttachments();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(fixture.Readiness).As<ICampaignObjectGraphReadiness>().ExternallyOwned();
        using var container = builder.Build();
        var harmony = new Harmony("ObjectReadiness.UpdateTest." + Guid.NewGuid().ToString("N"));
        ContainerProvider.TryGetContainer(out var previous);
        try
        {
            OnGame(() =>
            {
                Assert.True(fixture.Objects.TryGetObject<PartyBase>(2u, out var owner));
                Assert.True(fixture.Objects.TryGetObject<ItemRoster>(3u, out var items));
                Assert.True(fixture.Objects.TryGetObject<TroopRoster>(4u, out var members));
                Assert.True(fixture.Objects.TryGetObject<TroopRoster>(5u, out var prisoners));
                SetBackingReference(fixture.Party, nameof(MobileParty.Party), owner);
                SetBackingReference(owner, nameof(PartyBase.MobileParty), fixture.Party);
                SetBackingReference(owner, nameof(PartyBase.ItemRoster), items);
                SetBackingReference(owner, nameof(PartyBase.MemberRoster), members);
                SetBackingReference(owner, nameof(PartyBase.PrisonRoster), prisoners);
                members.OwnerParty = owner;
                prisoners.OwnerParty = owner;
                Assert.Equal(0, fixture.Publications);
                ContainerProvider.SetContainer(container);
                harmony.Patch(AccessTools.Method(typeof(GameThread), nameof(GameThread.Update)),
                    postfix: new HarmonyMethod(typeof(ObjectGraphReadinessUpdatePatch), nameof(ObjectGraphReadinessUpdatePatch.Postfix)));
                GameThread.Instance.Update(TimeSpan.Zero);
                Assert.Equal(1, fixture.Publications);
                Assert.True(fixture.Ready());
            });
        }
        finally
        {
            OnGame(() =>
            {
                harmony.UnpatchAll(harmony.Id);
                if (previous != null) ContainerProvider.SetContainer(previous); else ContainerProvider.Clear();
            });
        }
    }

    private static Assembly GenerateHandlers()
    {
        var factory = new Mock<IAutoRegistryFactory>();
        factory.Setup(instance => instance.IsManaged(It.IsAny<Type>())).Returns(true);
        var registry = new AutoSyncRegistry();
        var generator = new AutoSyncPropertyBuilder(factory.Object, registry, new AutoSyncConstantsBuilder());
        var properties = new[]
        {
            AccessTools.Property(typeof(MobileParty), nameof(MobileParty.Party)),
            AccessTools.Property(typeof(PartyBase), nameof(PartyBase.MobileParty)),
            AccessTools.Property(typeof(PartyBase), nameof(PartyBase.ItemRoster)),
            AccessTools.Property(typeof(PartyBase), nameof(PartyBase.MemberRoster)),
            AccessTools.Property(typeof(PartyBase), nameof(PartyBase.PrisonRoster)),
        };
        bool export = AutoSyncConfiguration.ExportFiles;
        var syntax = new List<SyntaxTree>();
        try
        {
            AutoSyncConfiguration.ExportFiles = false;
            foreach (var property in properties)
            {
                registry.AddProperty(property);
                syntax.AddRange(generator.GetMessages(new Debuggable<PropertyInfo>(property, false)).Select(source => CSharpSyntaxTree.ParseText(source)));
            }
            syntax.Add(new AutoSyncAssemblyInfoBuilder(registry).Build(Array.Empty<string>()));
            foreach (var type in new[] { typeof(MobileParty), typeof(PartyBase) })
            {
                string name = type == typeof(MobileParty) ? "ReadinessMobileHandler" : "ReadinessPartyHandler";
                string subscriptions = string.Join("\n", properties.Where(property => property.DeclaringType == type)
                    .Select(property => generator.GetSubscription(new Debuggable<PropertyInfo>(property, false))));
                string headers = "using System; using Common; using Common.Util; using Common.Messaging; using Common.Network; using GameInterface.AutoSync; using GameInterface.Utils; using GameInterface.Services.ObjectManager; using HarmonyLib; using TaleWorlds.CampaignSystem.Party; using TaleWorlds.CampaignSystem.Roster; namespace AutoSync;";
                syntax.Add(CSharpSyntaxTree.ParseText(headers + "public sealed class " + name + ": GenericHandler<" + name + "," + type.Name + ">{ public " + name + "(IMessageBroker broker,IObjectManager objects,INetwork network,AutoSyncRegistry registry):base(broker,objects,network){" + subscriptions + "}}"));
            }
        }
        finally { AutoSyncConfiguration.ExportFiles = export; }
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(value => !value.IsDynamic && !string.IsNullOrEmpty(value.Location)).Select(value => value.Location)).Distinct();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithMetadataImportOptions(MetadataImportOptions.All);
        typeof(CSharpCompilationOptions).GetProperty("TopLevelBinderFlags", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(options, (uint)1 << 22);
        var compilation = CSharpCompilation.Create("AutoSyncAsm.ObjectReadinessTests." + Guid.NewGuid().ToString("N"), syntax,
            paths.Select(path => MetadataReference.CreateFromFile(path)), options);
        using var bytes = new MemoryStream();
        var result = compilation.Emit(bytes);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return Assembly.Load(bytes.ToArray());
    }

    private static void SetBackingReference<T>(T instance, string property, object value)
    {
        var getter = AccessTools.PropertyGetter(typeof(T), property);
        var bytes = getter.GetMethodBody()!.GetILAsByteArray()!;
        Assert.Equal(7, bytes.Length);
        Assert.Equal(0x02, bytes[0]);
        Assert.Equal(0x7b, bytes[1]);
        Assert.Equal(0x2a, bytes[6]);
        var field = getter.Module.ResolveField(BitConverter.ToInt32(bytes, 2));
        Assert.Equal(typeof(T), field.DeclaringType);
        field.SetValue(instance, value);
    }

    private static void OnGame(Action action)
    {
        Exception? failure = null;
        GameThread.Run(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }, blocking: true);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Mock<ILogger> ObjectLog = new();
        internal readonly Objects Objects;
        internal readonly MessageBroker Broker = new();
        internal readonly MobileParty Party = ObjectHelper.SkipConstructor<MobileParty>();
        internal readonly PartyBase TempOwner = ObjectHelper.SkipConstructor<PartyBase>();
        internal readonly CampaignObjectGraphReadiness Readiness;
        internal int Publications;
        private readonly List<IDisposable> handlers = new();

        internal Fixture()
        {
            Objects = new Objects(ObjectLog.Object);
            Readiness = new CampaignObjectGraphReadiness(Objects, Mock.Of<ILogger>());
            OnGame(() =>
            {
                Party.StringId = "Created_2930";
                Party.Party = TempOwner;
                TempOwner.MobileParty = Party;
                TempOwner.ItemRoster = new ItemRoster();
                TempOwner.MemberRoster = new TroopRoster(TempOwner);
                TempOwner.PrisonRoster = new TroopRoster(TempOwner);
                Assert.True(Objects.AddExisting("MobileParty_Created_2930", Party, 1));
            });
            var factory = Mock.Of<IAutoRegistryFactory>();
            var network = Mock.Of<INetwork>();
            var logger = Mock.Of<ILogger>();
            handlers.Add(new AutoRegistryHandler<PartyBase>(new PartyBaseRegistry(logger, factory, Objects), Broker, network, Objects));
            handlers.Add(new ItemRosterLifetimeHandler(Broker, Objects, network));
            handlers.Add(new AutoRegistryHandler<TroopRoster>(new TroopRosterRegistry(logger, factory, Objects), Broker, network, Objects));
            handlers.Add(new OwnerReferences<object>(Broker, Objects, network, Readiness));
        }

        internal void Begin(Action? publish = null)
        {
            Readiness.BeginReplicaCreation(Party);
            Readiness.PublishReplicaWhenReady(Party, publish ?? (() => Publications++));
        }

        internal bool Ready() => Readiness.TryEnsureReady(out _);

        internal void CreateAttachments()
        {
            Broker.Publish(this, new NetworkCreateInstance<PartyBase>("owner", 2));
            Broker.Publish(this, new NetworkCreateItemRoster("ItemRoster_items", 3));
            Broker.Publish(this, new NetworkCreateInstance<TroopRoster>("members", 4));
            Broker.Publish(this, new NetworkCreateInstance<TroopRoster>("prisoners", 5));
        }

        internal void BindOwnerAndItems()
        {
            Broker.Publish(this, new OwnerReference(1, 2));
            Broker.Publish(this, new ItemsReference(2, 3));
        }
        internal void BindMember(bool backlink = true) => Broker.Publish(this, new MemberReference(2, 4, backlink));
        internal void BindPrison(bool backlink = true) => Broker.Publish(this, new PrisonReference(2, 5, backlink));

        internal void Complete()
        {
            OnGame(() => Begin());
            CreateAttachments();
            BindOwnerAndItems();
            BindMember();
            BindPrison();
            OnGame(() => Assert.True(Ready()));
        }

        public void Dispose()
        {
            OnGame(() => { });
            foreach (var handler in handlers) handler.Dispose();
            Broker.Dispose();
        }
    }

    private sealed record OwnerReference(uint Party, uint Owner) : GenericNetworkReferenceEvent<MobileParty, PartyBase>(Party, Owner)
    { public override uint InstanceId { get; set; } public override uint ValueId { get; set; } }
    private sealed record ItemsReference(uint Owner, uint Items) : GenericNetworkReferenceEvent<PartyBase, ItemRoster>(Owner, Items)
    { public override uint InstanceId { get; set; } public override uint ValueId { get; set; } }
    private sealed record MemberReference(uint Owner, uint Members, bool Backlink) : GenericNetworkReferenceEvent<PartyBase, TroopRoster>(Owner, Members)
    { public override uint InstanceId { get; set; } public override uint ValueId { get; set; } }
    private sealed record PrisonReference(uint Owner, uint Prisoners, bool Backlink) : GenericNetworkReferenceEvent<PartyBase, TroopRoster>(Owner, Prisoners)
    { public override uint InstanceId { get; set; } public override uint ValueId { get; set; } }

    // Generic fixtures are excluded from production handler discovery.
    private sealed class OwnerReferences<T> : GenericHandler<OwnerReferences<T>, MobileParty>
    {
        private readonly PartyReferences<T> partyReferences;
        internal OwnerReferences(IMessageBroker broker, IObjectManager objects,
            INetwork network, ICampaignObjectGraphReadiness readiness) : base(broker, objects, network)
        {
            SubscribeNetworkReference<PartyBase, OwnerReference>((party, owner, _) =>
            {
                party.Party = owner;
                owner.MobileParty = party;
                readiness.ReferencesApplied();
            });
            partyReferences = new PartyReferences<T>(broker, objects, network, readiness);
        }
        public new void Dispose() { partyReferences.Dispose(); base.Dispose(); }
    }

    private sealed class PartyReferences<T> : GenericHandler<PartyReferences<T>, PartyBase>
    {
        internal PartyReferences(IMessageBroker broker, IObjectManager objects,
            INetwork network, ICampaignObjectGraphReadiness readiness) : base(broker, objects, network)
        {
            SubscribeNetworkReference<ItemRoster, ItemsReference>((owner, items, _) =>
            { owner.ItemRoster = items; readiness.ReferencesApplied(); });
            SubscribeNetworkReference<TroopRoster, MemberReference>((owner, members, message) =>
            {
                owner.MemberRoster = members;
                if (message.Backlink) members.OwnerParty = owner;
                readiness.ReferencesApplied();
            });
            SubscribeNetworkReference<TroopRoster, PrisonReference>((owner, prisoners, message) =>
            {
                owner.PrisonRoster = prisoners;
                if (message.Backlink) prisoners.OwnerParty = owner;
                readiness.ReferencesApplied();
            });
        }
    }
}
