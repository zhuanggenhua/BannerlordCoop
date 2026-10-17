using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Common.Network.Packets;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Time.Interfaces;
using Serilog;
using System;
using System.Threading;

namespace Coop.Core.Client.Services.Time.Handlers;

/// <summary>Publishes the campaign clock when this client holds the confirmed authority lease.</summary>
public sealed class CampaignAuthorityTimePublisher : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignAuthorityTimePublisher>();
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);

    private readonly INetwork network;
    private readonly ICampaignAuthorityContext authorityContext;
    private readonly IMapTimeTrackerInterface mapTimeTracker;
    private readonly Timer timer;
    private int sampleQueued;
    private int disposed;

    public CampaignAuthorityTimePublisher(
        INetwork network,
        ICampaignAuthorityContext authorityContext,
        IMapTimeTrackerInterface mapTimeTracker)
    {
        this.network = network;
        this.authorityContext = authorityContext;
        this.mapTimeTracker = mapTimeTracker;
        timer = new Timer(_ => QueueSample(), null, PublishInterval, PublishInterval);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        timer.Dispose();
    }

    internal void QueueSample()
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref sampleQueued, 1) != 0) return;

        try
        {
            GameThread.EnqueueSafe(() =>
            {
                try
                {
                    if (Volatile.Read(ref disposed) != 0 ||
                        !authorityContext.TryGetLocalAuthorityLease(out var lease) ||
                        !mapTimeTracker.TryGetCurrentTicks(out long ticks))
                        return;

                    network.SendAll(new CampaignTimePacket(ticks, -1, lease));
                }
                finally
                {
                    Interlocked.Exchange(ref sampleQueued, 0);
                }
            }, context: nameof(CampaignAuthorityTimePublisher));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref sampleQueued, 0);
            Logger.Warning(ex, "Could not queue campaign authority clock sample");
        }
    }
}
