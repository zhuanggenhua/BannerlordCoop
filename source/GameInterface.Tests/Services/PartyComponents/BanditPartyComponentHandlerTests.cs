using Common;
using Common.Messaging;
using Common.Network;
using Common.Tests.Utils;
using Common.Util;
using Coop.Tests.Mocks;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PartyComponents.BanditPartyComponents.Handlers;
using GameInterface.Services.PartyComponents.BanditPartyComponents.Messages;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.PartyComponents;

public class BanditPartyComponentHandlerTests
{
    static BanditPartyComponentHandlerTests() => RuntimeHelpers.RunModuleConstructor(typeof(TestNetwork).Module.ModuleHandle);

    [Fact]
    public void ConversionWithoutSpawnArgumentsDoesNotEraseExistingInitialization()
    {
        using var authority = new Fixture();
        using var replica = new Fixture();
        var existing = new BanditPartyComponent.InitializationArgs(authority.Clan, authority.Template, default);
        var replicaExisting = new BanditPartyComponent.InitializationArgs(replica.Clan, replica.Template, default);
        authority.Component._initializationArgs = existing;
        replica.Component._initializationArgs = replicaExisting;
        authority.Broker.Publish(this, new BanditPartyComponentInitArgsUpdated(authority.Component, null));
        Assert.Empty(authority.Sent);
        Assert.Same(existing, authority.Component._initializationArgs);
        Assert.Same(replicaExisting, replica.Component._initializationArgs);
        Assert.Empty(authority.Errors.Events);
        Assert.Empty(replica.Errors.Events);
    }

    [Fact]
    public void DisposedHandlerDoesNotSendOrApplyInitializationMessages()
    {
        using var fixture = new Fixture();
        var existing = new BanditPartyComponent.InitializationArgs(fixture.Clan, fixture.Template, default);
        fixture.Component._initializationArgs = existing;
        fixture.StopHandler();
        fixture.Broker.Publish(this, new BanditPartyComponentInitArgsUpdated(fixture.Component,
            new BanditPartyComponent.InitializationArgs(fixture.Clan, null, default)));
        fixture.Broker.Publish(this, new NetworkUpdateBanditPartyComponentInitArgs("bandit", "clan", default, null));
        GameThread.Run(() => { }, blocking: true);
        Assert.Empty(fixture.Sent);
        Assert.Same(existing, fixture.Component._initializationArgs);
        Assert.Empty(fixture.Errors.Events);
    }

    [Fact]
    public void AlreadyQueuedInitializationDoesNotApplyAfterDisposal()
    {
        using var fixture = new Fixture();
        var existing = new BanditPartyComponent.InitializationArgs(fixture.Clan, fixture.Template, default);
        fixture.Component._initializationArgs = existing;
        GameThread.Run(() =>
        {
            Task.Run(() => fixture.Broker.Publish(this,
                new NetworkUpdateBanditPartyComponentInitArgs("bandit", "clan", default, null))).GetAwaiter().GetResult();
            fixture.StopHandler();
        }, blocking: true);
        GameThread.Run(() => { }, blocking: true);
        Assert.Same(existing, fixture.Component._initializationArgs);
        Assert.Empty(fixture.Errors.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonNullMissingTemplateStillFailsInsteadOfLosingInitializationSilently(bool send)
    {
        using var fixture = new Fixture();
        if (send)
        {
            var missing = ObjectHelper.SkipConstructor<PartyTemplateObject>();
            fixture.Broker.Publish(this, new BanditPartyComponentInitArgsUpdated(fixture.Component,
                new BanditPartyComponent.InitializationArgs(fixture.Clan, missing, default)));
        }
        else
        {
            fixture.Broker.Publish(this, new NetworkUpdateBanditPartyComponentInitArgs("bandit", "clan", default, "missing_template"));
            GameThread.Run(() => { }, blocking: true);
        }
        Assert.Empty(fixture.Sent);
        Assert.Null(fixture.Component._initializationArgs);
        Assert.NotEmpty(fixture.Errors.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpawnInitializationReplicatesClanPositionAndOptionalTemplate(bool hasTemplate)
    {
        using var authority = new Fixture();
        using var replica = new Fixture();
        var position = new CampaignVec2(new Vec2(17f, 29f), true);
        authority.Broker.Publish(this, new BanditPartyComponentInitArgsUpdated(authority.Component,
            new BanditPartyComponent.InitializationArgs(authority.Clan, hasTemplate ? authority.Template : null, position)));
        var message = Assert.IsType<NetworkUpdateBanditPartyComponentInitArgs>(Assert.Single(authority.Sent));
        Assert.Equal("bandit", message.BanditPartyComponentId);
        Assert.Equal("clan", message.ClanId);
        Assert.Equal(position, message.InitialPosition);
        Assert.Equal(hasTemplate ? "template" : null, message.PartyTemplateId);
        replica.Broker.Publish(this, message);
        GameThread.Run(() => { }, blocking: true);
        var applied = replica.Component._initializationArgs;
        Assert.NotNull(applied);
        Assert.Same(replica.Clan, applied.Clan);
        Assert.Equal(position, applied.InitialPosition);
        Assert.Same(hasTemplate ? replica.Template : null, applied.PartyTemplate);
        Assert.Empty(authority.Errors.Events);
        Assert.Empty(replica.Errors.Events);
    }

    [Fact]
    public void NullTemplateOnReceivedSpawnStillRestoresInitialization()
    {
        using var replica = new Fixture();
        var position = new CampaignVec2(new Vec2(31f, 47f), true);
        replica.Broker.Publish(this, new NetworkUpdateBanditPartyComponentInitArgs("bandit", "clan", position, null));
        GameThread.Run(() => { }, blocking: true);
        Assert.NotNull(replica.Component._initializationArgs);
        Assert.Same(replica.Clan, replica.Component._initializationArgs.Clan);
        Assert.Equal(position, replica.Component._initializationArgs.InitialPosition);
        Assert.Null(replica.Component._initializationArgs.PartyTemplate);
        Assert.Empty(replica.Errors.Events);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly TestMessageBroker Broker = new();
        public readonly List<IMessage> Sent = new();
        public readonly ErrorSink Errors = new();
        public readonly ObjectManager Objects;
        public readonly BanditPartyComponent Component = ObjectHelper.SkipConstructor<BanditPartyComponent>();
        public readonly Clan Clan = ObjectHelper.SkipConstructor<Clan>();
        public readonly PartyTemplateObject Template = ObjectHelper.SkipConstructor<PartyTemplateObject>();
        private readonly BanditPartyComponentHandler handler;
        private readonly Serilog.Core.Logger logger;

        public Fixture()
        {
            logger = new LoggerConfiguration().WriteTo.Sink(Errors).CreateLogger();
            Objects = new ObjectManager(logger);
            Assert.True(Objects.AddExisting("bandit", Component, 1));
            Assert.True(Objects.AddExisting("clan", Clan, 2));
            Assert.True(Objects.AddExisting("template", Template, 3));
            var network = new Mock<INetwork>();
            network.Setup(value => value.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(Sent.Add);
            handler = new BanditPartyComponentHandler(Broker, network.Object, Objects);
        }

        public void Dispose()
        {
            handler.Dispose();
            GameThread.Run(() => { }, blocking: true);
            Broker.Dispose();
            logger.Dispose();
        }

        public void StopHandler() => handler.Dispose();
    }

    private sealed class ErrorSink : ILogEventSink
    {
        public readonly List<LogEvent> Events = new();
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level >= LogEventLevel.Error) Events.Add(logEvent);
        }
    }
}
