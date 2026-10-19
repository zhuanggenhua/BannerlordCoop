using Common;
using Common.Logging;
using Common.Network.Session;
using GameInterface.Services.MobileParties.Data;
using ProtoBuf;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.CampaignAuthority.Movement;

public interface ICampaignMovementCheckpointService
{
    CampaignMovementCheckpoint LastCaptured { get; }
    CampaignMovementCheckpoint LastApplied { get; }
    bool TryCapture(AuthorityLease source, long ticks, out CampaignMovementCheckpoint checkpoint);
    bool IsInstalled(CampaignMovementCheckpoint checkpoint);
    bool TryPrepare(CampaignMovementCheckpoint checkpoint, AuthorityLease source, long ticks, out Func<bool> apply);
}

public sealed class CampaignMovementCheckpointService : ICampaignMovementCheckpointService
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignMovementCheckpointService>();
    private readonly IMobilePartyBehaviorSnapshot snapshot;
    private readonly IPartyBehaviorWireMapper mapper;
    public CampaignMovementCheckpoint LastCaptured { get; private set; }
    public CampaignMovementCheckpoint LastApplied { get; private set; }

    public CampaignMovementCheckpointService(IMobilePartyBehaviorSnapshot snapshot, IPartyBehaviorWireMapper mapper)
    {
        this.snapshot = snapshot;
        this.mapper = mapper;
    }

    public bool TryCapture(AuthorityLease source, long ticks, out CampaignMovementCheckpoint checkpoint)
    {
        checkpoint = null;
        if (!source.IsValid || source.IsPaused || ticks < 0 || !TryCaptureStates(out var states)) return false;
        byte[] payload = Encode(states);
        checkpoint = new CampaignMovementCheckpoint(source, ticks, states.Length, payload, Hash(payload));
        LastCaptured = checkpoint;
        Logger.Information("Captured final campaign movement epoch {Epoch} ticks {Ticks} parties {PartyCount} sha256 {Sha256}",
            source.Epoch, ticks, states.Length, checkpoint.Sha256);
        return true;
    }

    public bool TryPrepare(CampaignMovementCheckpoint checkpoint, AuthorityLease source, long ticks, out Func<bool> apply)
    {
        apply = null;
        if (checkpoint == null || !checkpoint.Matches(source, ticks) || checkpoint.PartyCount < 0 ||
            checkpoint.Payload == null || !string.Equals(checkpoint.Sha256, Hash(checkpoint.Payload), StringComparison.Ordinal)) return false;
        if (LastApplied != null && LastApplied.SourceLease == source &&
            (LastApplied.Ticks != ticks || !string.Equals(LastApplied.Sha256, checkpoint.Sha256, StringComparison.Ordinal))) return false;

        NetworkMobilePartyJoinState[] wire;
        try
        {
            using var stream = new MemoryStream(checkpoint.Payload, writable: false);
            wire = Serializer.Deserialize<MovementPayload>(stream)?.States;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Rejected invalid final campaign movement payload at epoch {Epoch}", source.Epoch);
            return false;
        }
        if (wire == null || wire.Length != checkpoint.PartyCount) return false;
        var states = new MobilePartyJoinState[wire.Length];
        var handles = new HashSet<uint>();
        for (int i = 0; i < wire.Length; i++)
        {
            if (wire[i].Behavior.MobilePartyId == 0 || !handles.Add(wire[i].Behavior.MobilePartyId) ||
                !Finite(wire[i].Behavior.PartyPosition.X) || !Finite(wire[i].Behavior.PartyPosition.Y) ||
                !mapper.TryFromNetwork(wire[i].Behavior, out var behavior)) return false;
            states[i] = wire[i].ToLocal(behavior);
        }
        if (!snapshot.TryPrepareAuthorityBaseline(states, out var install)) return false;
        Logger.Information("Prepared final campaign movement epoch {Epoch} ticks {Ticks} parties {PartyCount} sha256 {Sha256}",
            source.Epoch, ticks, states.Length, checkpoint.Sha256);
        bool consumed = false;
        apply = () =>
        {
            if (consumed) return false;
            consumed = true;
            // An exact duplicate verifies the installed baseline without replaying movement writes.
            bool duplicate = LastApplied != null && LastApplied.SourceLease == source;
            if ((!duplicate && !install()) || !MatchesInstalled(states)) return false;
            LastApplied = checkpoint;
            Logger.Information("Installed final campaign movement epoch {Epoch} ticks {Ticks} parties {PartyCount} sha256 {Sha256} duplicate {Duplicate}",
                source.Epoch, ticks, states.Length, checkpoint.Sha256, duplicate);
            return true;
        };
        return true;
    }

    public bool IsInstalled(CampaignMovementCheckpoint checkpoint)
    {
        if (checkpoint?.Payload == null || !string.Equals(checkpoint.Sha256, Hash(checkpoint.Payload), StringComparison.Ordinal) ||
            !TryCaptureStates(out var states) || states.Length != checkpoint.PartyCount) return false;
        return Encode(states).SequenceEqual(checkpoint.Payload);
    }

    private bool TryCaptureStates(out NetworkMobilePartyJoinState[] states)
    {
        states = null;
        var manager = Campaign.Current?.CampaignObjectManager;
        if (manager?.MobileParties == null || manager.Settlements == null) return false;
        var parties = new HashSet<MobileParty>(manager.MobileParties.Where(p => p?.IsActive == true));
        var settlements = new HashSet<Settlement>(manager.Settlements);
        var result = new List<NetworkMobilePartyJoinState>();
        foreach (var party in parties)
        {
            if (!snapshot.TryCreateAuthorityState(party, parties, settlements, out var state) ||
                !mapper.TryToNetwork(state.Behavior, out var behavior))
            {
                Logger.Warning("Could not capture final movement for party {Party}; authority remains fenced", party.StringId);
                return false;
            }
            result.Add(new NetworkMobilePartyJoinState(state, behavior));
        }
        states = result.OrderBy(p => p.Behavior.MobilePartyId).ToArray();
        return true;
    }

    private bool MatchesInstalled(MobilePartyJoinState[] states)
    {
        if (!TryCaptureStates(out var current) || current.Length != states.Length) return false;
        var expected = new List<NetworkMobilePartyJoinState>();
        foreach (var state in states)
        {
            if (!mapper.TryToNetwork(state.Behavior, out var behavior)) return false;
            expected.Add(new NetworkMobilePartyJoinState(state, behavior));
        }
        var ordered = expected.OrderBy(p => p.Behavior.MobilePartyId).ToArray();
        if (Encode(current).SequenceEqual(Encode(ordered))) return true;
        for (int i = 0; i < current.Length; i++)
        {
            if (Encode(new[] { current[i] }).SequenceEqual(Encode(new[] { ordered[i] }))) continue;
            Logger.Error("Final movement verification differs for party handle {Handle}: expected {@Expected}, actual {@Actual}",
                ordered[i].Behavior.MobilePartyId, ordered[i], current[i]);
            break;
        }
        return false;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static byte[] Encode(NetworkMobilePartyJoinState[] states)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, new MovementPayload { States = states });
        return stream.ToArray();
    }

    [ProtoContract]
    internal sealed class MovementPayload
    {
        [ProtoMember(1)] public NetworkMobilePartyJoinState[] States { get; set; } = Array.Empty<NetworkMobilePartyJoinState>();
    }
}
