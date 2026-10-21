using ProtoBuf;
using System;
using System.Linq;
using System.Text.Json;

namespace GameInterface.CoopSessionData.Save.Data;

// Serialized facts only. These values never authenticate a claim or grant a writer lease.
public enum BattlePreparationPhase { Preparing, Prepared, Retired, Unknown }
public enum BattlePreparationLookup { Missing, AlreadyPrepared, Conflict, Blocked }

[ProtoContract(SkipConstructor = true)]
public sealed class BattleObjectReference
{
    [ProtoMember(1)] public string Id { get; }
    [ProtoMember(2)] public uint Handle { get; }
    public BattleObjectReference(string id, uint handle)
    {
        Id = id;
        Handle = handle;
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class BattlePreparationKey
{
    [ProtoMember(1)] public string RoomId { get; }
    [ProtoMember(2)] public string CampaignId { get; }
    [ProtoMember(3)] public BattleObjectReference MapEvent { get; }
    [ProtoMember(4)] public string ClaimId { get; }
    [ProtoMember(5)] public long ClaimGeneration { get; }
    [ProtoMember(6)] public string Operation { get; }
    public BattlePreparationKey(string roomId, string campaignId, BattleObjectReference mapEvent, string claimId, long claimGeneration, string operation = "InitialOrdinaryField")
    {
        RoomId = roomId;
        CampaignId = campaignId;
        MapEvent = mapEvent;
        ClaimId = claimId;
        ClaimGeneration = claimGeneration;
        Operation = operation;
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class BattleCohortMember
{
    [ProtoMember(1)] public string ControllerId { get; }
    [ProtoMember(2)] public BattleObjectReference Party { get; }
    [ProtoMember(3)] public string ReservationId { get; }
    public BattleCohortMember(string controllerId, BattleObjectReference party, string reservationId)
    {
        ControllerId = controllerId;
        Party = party;
        ReservationId = reservationId;
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class BattlePreparationInput
{
    [ProtoMember(1)] public BattleObjectReference InitiatingParty { get; }
    private BattleCohortMember[] initialCohortData;
    [ProtoMember(2)] public BattleCohortMember[] InitialCohort { get => (BattleCohortMember[])(initialCohortData ?? Array.Empty<BattleCohortMember>()).Clone(); private set => initialCohortData = value; }
    public BattlePreparationInput(BattleObjectReference initiatingParty, BattleCohortMember[] initialCohort)
    {
        InitiatingParty = initiatingParty;
        InitialCohort = (BattleCohortMember[])(initialCohort ?? Array.Empty<BattleCohortMember>()).Clone();
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class BattlePreparedTroop
{
    [ProtoMember(1)] public BattleObjectReference Actor { get; }
    [ProtoMember(2)] public bool IsHero { get; }
    [ProtoMember(3)] public int UniqueSeed { get; }
    [ProtoMember(4)] public int State { get; }
    [ProtoMember(5)] public int Xp { get; }
    [ProtoMember(6)] public int XpGained { get; }
    public BattlePreparedTroop(BattleObjectReference actor, bool isHero, int uniqueSeed, int state, int xp, int xpGained)
    {
        Actor = actor;
        IsHero = isHero;
        UniqueSeed = uniqueSeed;
        State = state;
        Xp = xp;
        XpGained = xpGained;
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class BattlePreparedParty
{
    [ProtoMember(1)] public BattleObjectReference MapEventParty { get; }
    [ProtoMember(2)] public BattleObjectReference Party { get; }
    [ProtoMember(3)] public int ParticipatingTroops { get; }
    [ProtoMember(4)] public int HealthyTroopsAtStart { get; }
    [ProtoMember(5)] public int Contribution { get; }
    private BattlePreparedTroop[] troopsData;
    [ProtoMember(6)] public BattlePreparedTroop[] Troops { get => (BattlePreparedTroop[])(troopsData ?? Array.Empty<BattlePreparedTroop>()).Clone(); private set => troopsData = value; }
    public BattlePreparedParty(BattleObjectReference mapEventParty, BattleObjectReference party, int participatingTroops, int healthyTroopsAtStart, int contribution, BattlePreparedTroop[] troops)
    {
        MapEventParty = mapEventParty;
        Party = party;
        ParticipatingTroops = participatingTroops;
        HealthyTroopsAtStart = healthyTroopsAtStart;
        Contribution = contribution;
        Troops = (BattlePreparedTroop[])(troops ?? Array.Empty<BattlePreparedTroop>()).Clone();
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class BattleReadyTroop
{
    [ProtoMember(1)] public BattleObjectReference MapEventParty { get; }
    [ProtoMember(2)] public int UniqueSeed { get; }
    [ProtoMember(3)] public int PriorityBits { get; }
    public BattleReadyTroop(BattleObjectReference mapEventParty, int uniqueSeed, int priorityBits)
    {
        MapEventParty = mapEventParty;
        UniqueSeed = uniqueSeed;
        PriorityBits = priorityBits;
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class BattlePreparedSide
{
    [ProtoMember(1)] public BattleObjectReference Side { get; }
    [ProtoMember(2)] public int MissionSide { get; }
    [ProtoMember(3)] public BattleObjectReference LeaderParty { get; }
    private BattleObjectReference[] partiesData;
    [ProtoMember(4)] public BattleObjectReference[] Parties { get => (BattleObjectReference[])(partiesData ?? Array.Empty<BattleObjectReference>()).Clone(); private set => partiesData = value; }
    private BattleReadyTroop[] readyTroopsData;
    [ProtoMember(5)] public BattleReadyTroop[] ReadyTroops { get => (BattleReadyTroop[])(readyTroopsData ?? Array.Empty<BattleReadyTroop>()).Clone(); private set => readyTroopsData = value; }
    [ProtoMember(6)] public bool CacheDirty { get; }
    public BattlePreparedSide(BattleObjectReference side, int missionSide, BattleObjectReference leaderParty, BattleObjectReference[] parties, BattleReadyTroop[] readyTroops, bool cacheDirty)
    {
        Side = side;
        MissionSide = missionSide;
        LeaderParty = leaderParty;
        Parties = (BattleObjectReference[])(parties ?? Array.Empty<BattleObjectReference>()).Clone();
        ReadyTroops = (BattleReadyTroop[])(readyTroops ?? Array.Empty<BattleReadyTroop>()).Clone();
        CacheDirty = cacheDirty;
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class BattlePreparedPayload
{
    private BattlePreparedParty[] partiesData;
    [ProtoMember(1)] public BattlePreparedParty[] Parties { get => (BattlePreparedParty[])(partiesData ?? Array.Empty<BattlePreparedParty>()).Clone(); private set => partiesData = value; }
    private BattlePreparedSide[] sidesData;
    [ProtoMember(2)] public BattlePreparedSide[] Sides { get => (BattlePreparedSide[])(sidesData ?? Array.Empty<BattlePreparedSide>()).Clone(); private set => sidesData = value; }
    private byte[] missionInitializerData;
    [ProtoMember(3)] public byte[] MissionInitializer { get => (byte[])(missionInitializerData ?? Array.Empty<byte>()).Clone(); private set => missionInitializerData = value; }
    public BattlePreparedPayload(BattlePreparedParty[] parties, BattlePreparedSide[] sides, byte[] missionInitializer)
    {
        Parties = (BattlePreparedParty[])(parties ?? Array.Empty<BattlePreparedParty>()).Clone();
        Sides = (BattlePreparedSide[])(sides ?? Array.Empty<BattlePreparedSide>()).Clone();
        MissionInitializer = (byte[])(missionInitializer ?? Array.Empty<byte>()).Clone();
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class BattlePreparationRecord
{
    [ProtoMember(1)] public BattlePreparationKey Key { get; }
    [ProtoMember(2)] public BattlePreparationInput Input { get; }
    [ProtoMember(3)] public BattlePreparationPhase Phase { get; }
    [ProtoMember(4)] public long ExecutionEpoch { get; }
    [ProtoMember(5)] public BattlePreparedPayload Prepared { get; }
    [ProtoMember(6)] public string Failure { get; }
    public BattlePreparationRecord(BattlePreparationKey key, BattlePreparationInput input, BattlePreparationPhase phase, long executionEpoch, BattlePreparedPayload prepared, string failure)
    {
        Key = key;
        Input = input;
        Phase = phase;
        ExecutionEpoch = executionEpoch;
        Prepared = prepared;
        Failure = failure;
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class BattlePreparationState
{
    private BattlePreparationRecord[] records;
    [ProtoMember(1)] public int SchemaVersion { get; }
    [ProtoMember(2)] public BattlePreparationRecord[] Records
    { get => (BattlePreparationRecord[])(records ?? Array.Empty<BattlePreparationRecord>()).Clone(); private set => records = value; }
    public BattlePreparationState(int schemaVersion = 1, BattlePreparationRecord[] records = null)
    { SchemaVersion = schemaVersion; Records = (BattlePreparationRecord[])(records ?? Array.Empty<BattlePreparationRecord>()).Clone(); }

    public BattlePreparationState Copy() => new BattlePreparationState(SchemaVersion, Records);

    public BattlePreparationLookup Inspect(BattlePreparationKey key, BattlePreparationInput input)
    {
        if (SchemaVersion != 1) return BattlePreparationLookup.Blocked;
        if (key == null || input == null) return BattlePreparationLookup.Conflict;
        var matches = Records.Where(value => value != null && Same(value.Key, key)).ToArray();
        if (matches.Length == 0) return BattlePreparationLookup.Missing;
        if (matches.Length != 1 || !Same(matches[0].Input, input)) return BattlePreparationLookup.Conflict;
        var row = matches[0];
        if (row.Prepared == null || !string.IsNullOrEmpty(row.Failure) || row.ExecutionEpoch <= 0)
            return BattlePreparationLookup.Blocked;
        return row.Phase == BattlePreparationPhase.Prepared || row.Phase == BattlePreparationPhase.Retired
            ? BattlePreparationLookup.AlreadyPrepared : BattlePreparationLookup.Blocked;
    }

    // Active installation needs the future authenticated claim and real async-drain receipt.
    // No serialized boolean can authorize it. Historical retired records retain their original room.
    public bool CanTransferHistory(out string reason)
    {
        reason = null;
        if (SchemaVersion != 1) { reason = "Unsupported battle preparation schema";return false; }
        var entries = Records;
        for (int i = 0; i < entries.Length; i++)
        {
            var row = entries[i];var key = row?.Key;
            if (key == null || string.IsNullOrWhiteSpace(key.RoomId) || string.IsNullOrWhiteSpace(key.CampaignId) ||
                !ValidReference(key.MapEvent) || string.IsNullOrWhiteSpace(key.ClaimId) || key.ClaimGeneration <= 0 || key.Operation != "InitialOrdinaryField" ||
                row.Input == null || !ValidReference(row.Input.InitiatingParty) || row.ExecutionEpoch <= 0 ||
                entries.Take(i).Any(other => Same(other?.Key, key)))
            { reason = "Invalid or duplicated battle lifetime";return false; }
            var cohort = row.Input.InitialCohort;
            if (cohort.Length == 0 || cohort.Any(member => member == null || string.IsNullOrWhiteSpace(member.ControllerId) ||
                string.IsNullOrWhiteSpace(member.ReservationId) || !ValidReference(member.Party)) ||
                cohort.Select(member => member.ControllerId).Distinct(StringComparer.Ordinal).Count() != cohort.Length ||
                cohort.Select(member => member.ReservationId).Distinct(StringComparer.Ordinal).Count() != cohort.Length ||
                !cohort.Any(member => Same(member.Party, row.Input.InitiatingParty)) ||
                cohort.Select(member => JsonSerializer.Serialize(member.Party)).Distinct(StringComparer.Ordinal).Count() != cohort.Length)
            { reason = "Invalid canonical initial battle cohort";return false; }
            if (row.Phase != BattlePreparationPhase.Retired || row.Prepared == null || !string.IsNullOrEmpty(row.Failure))
            { reason = "Active or partial battle preparation requires authenticated claim and completed drain evidence (NOT_IMPLEMENTED)";return false; }
        }
        return true;
    }
    internal bool CanInstallHistoryOver(BattlePreparationState previous, out string reason)
    {
        if (!CanTransferHistory(out reason)) return false;
        if (previous == null) return true;
        if (!previous.CanTransferHistory(out reason)) return false;
        foreach (var existing in previous.Records)
        {
            var incoming = Records.SingleOrDefault(value => Same(value.Key, existing.Key));
            if (incoming == null || !Same(incoming, existing))
            { reason = "Checkpoint would drop or change an existing battle preparation result";return false; }
        }
        return true;
    }

    internal static bool ValidReference(BattleObjectReference value) => value != null && !string.IsNullOrWhiteSpace(value.Id) && value.Handle != 0;
    internal static bool Same<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
