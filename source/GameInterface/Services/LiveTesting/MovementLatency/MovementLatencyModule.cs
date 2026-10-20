#if DEBUG
using Autofac;
using Common.Commands;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Globalization;

namespace GameInterface.Services.LiveTesting.MovementLatency;

public sealed class MovementLatencyModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<MovementLatencyEnvironment>().As<IMovementLatencyEnvironment>().InstancePerLifetimeScope();
        builder.RegisterType<NativeMovementLatencyHooks>().As<IMovementLatencyHooks>().InstancePerLifetimeScope();
        builder.RegisterType<MovementLatencyProbe>().As<IMovementLatencyProbe>().InstancePerLifetimeScope();
    }
}

public static class MovementLatencyCommands
{
    private static readonly JsonSerializerSettings Json = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };
    private static CoopCommandResult Run(Func<MovementLatencyPage> action)
    {
        try
        {
            var page = action();
            return new CoopCommandResult(page.Fault == null && !page.Overflow, JsonConvert.SerializeObject(page, Json),
                page.Fault != null || page.Overflow ? "probe_invalid" : null);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is ObjectDisposedException)
        { return new CoopCommandResult(false, ex.Message, "probe_rejected"); }
    }

    public sealed class BeginCommand : ICoopCommand
    {
        private readonly IMovementLatencyProbe probe;
        public BeginCommand(IMovementLatencyProbe probe) => this.probe = probe;
        public string Prefix => "coop.debug.movement_probe";
        public string Name => "begin";
        public string Description => "Arms bounded observation-only movement diagnostics for exactly two player parties.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[] { new ExpectedArgs("label", "Unique evidence label.", true) };
        public CoopCommandResult ProcessCommand(ICoopCommandArgs args) => args.Count == 1 ? Run(() => probe.Begin(args[0])) : new(false, "Expected one unique label.", "probe_rejected");
    }

    public sealed class ReadCommand : ICoopCommand
    {
        private readonly IMovementLatencyProbe probe;
        public ReadCommand(IMovementLatencyProbe probe) => this.probe = probe;
        public string Prefix => "coop.debug.movement_probe";
        public string Name => "read";
        public string Description => "Reads retained rows without advancing simulation; clock is sampled freshly inside this command.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[] { new ExpectedArgs("fromSeq", "Zero-based inclusive cursor.", true), new ExpectedArgs("maxRows", "1..256 rows.", true) };
        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (args.Count != 2 || !long.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long cursor) ||
                !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int max))
                return new(false, "Expected nonnegative fromSeq and maxRows 1..256.", "probe_rejected");
            return Run(() => probe.Read(cursor, max));
        }
    }

    public sealed class StopCommand : ICoopCommand
    {
        private readonly IMovementLatencyProbe probe;
        public StopCommand(IMovementLatencyProbe probe) => this.probe = probe;
        public string Prefix => "coop.debug.movement_probe";
        public string Name => "stop";
        public string Description => "Disarms all movement observers and retains captured rows for pagination.";
        public CoopCommandSide Side => CoopCommandSide.Both;
        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();
        public CoopCommandResult ProcessCommand(ICoopCommandArgs args) => args.Count == 0 ? Run(probe.Stop) : new(false, "No arguments expected.", "probe_rejected");
    }
}
#endif
