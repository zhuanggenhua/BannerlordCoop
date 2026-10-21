using Common.Messaging;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Common.Network.Session;

[ProtoContract(SkipConstructor = true)]
public readonly struct RoomMissionFactsVersion : IEquatable<RoomMissionFactsVersion>
{
    [ProtoMember(1)] public readonly string RoomId;
    [ProtoMember(2)] public readonly long Revision;
    [ProtoMember(3)] public readonly string Sha256;

    public RoomMissionFactsVersion(string roomId, long revision, string sha256)
    {
        RoomId = roomId;
        Revision = revision;
        Sha256 = sha256;
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(RoomId) && Revision >= 0 &&
        Sha256 != null && Sha256.Length == 64 && Sha256.All(character =>
            (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));
    public bool Equals(RoomMissionFactsVersion other) => RoomId == other.RoomId &&
        Revision == other.Revision && Sha256 == other.Sha256;
    public override bool Equals(object obj) => obj is RoomMissionFactsVersion other && Equals(other);
    public override int GetHashCode() => (RoomId?.GetHashCode() ?? 0) ^ Revision.GetHashCode() ^ (Sha256?.GetHashCode() ?? 0);
    public static bool operator ==(RoomMissionFactsVersion left, RoomMissionFactsVersion right) => left.Equals(right);
    public static bool operator !=(RoomMissionFactsVersion left, RoomMissionFactsVersion right) => !left.Equals(right);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct RoomMissionMember
{
    [ProtoMember(1)] public readonly string ControllerId;
    [ProtoMember(2)] public readonly string InstanceId;
    [ProtoMember(3)] public readonly long Generation;

    public RoomMissionMember(string controllerId, string instanceId, long generation)
    {
        ControllerId = controllerId;
        InstanceId = instanceId;
        Generation = generation;
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(ControllerId) &&
        !string.IsNullOrWhiteSpace(InstanceId) && Generation > 0;
}

[ProtoContract(SkipConstructor = true)]
public sealed class RoomMissionFactsSnapshot
{
    [ProtoMember(1)] public readonly RoomMissionFactsVersion Version;
    [ProtoMember(2)] private readonly RoomMissionMember[] members;
    [ProtoMember(3)] public readonly bool MembershipComplete;
    public RoomMissionMember[] Members => members ?? Array.Empty<RoomMissionMember>();

    public RoomMissionFactsSnapshot(RoomMissionFactsVersion version, RoomMissionMember[] members)
    {
        Version = version;
        this.members = members?.ToArray();
        MembershipComplete = members != null;
    }

    public static bool TryCreate(string roomId, long revision, IEnumerable<RoomMissionMember> members,
        out RoomMissionFactsSnapshot snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(roomId) || revision < 0 || members == null) return false;
        var rows = members.OrderBy(member => member.ControllerId, StringComparer.Ordinal).ToArray();
        if (rows.Any(member => !member.IsValid) ||
            rows.Select(member => member.ControllerId).Distinct(StringComparer.Ordinal).Count() != rows.Length) return false;
        snapshot = new RoomMissionFactsSnapshot(new RoomMissionFactsVersion(roomId, revision,
            Hash(roomId, revision, rows)), rows);
        return true;
    }

    public bool IsValid => Version.IsValid && MembershipComplete &&
        TryCreate(Version.RoomId, Version.Revision, Members, out var canonical) && canonical.Version == Version;

    private static string Hash(string roomId, long revision, RoomMissionMember[] members)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1);
            writer.Write(roomId);
            writer.Write(revision);
            writer.Write(members.Length);
            foreach (var member in members)
            {
                writer.Write(member.ControllerId);
                writer.Write(member.InstanceId);
                writer.Write(member.Generation);
            }
        }
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRoomMissionFactsSnapshot : IServerToClientCommand
{
    [ProtoMember(1)] public readonly RoomMissionFactsSnapshot Snapshot;
    public NetworkRoomMissionFactsSnapshot(RoomMissionFactsSnapshot snapshot) => Snapshot = snapshot;
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRoomMissionFactsApplied : IMessage
{
    [ProtoMember(1)] public readonly RoomMissionFactsVersion Version;
    public NetworkRoomMissionFactsApplied(RoomMissionFactsVersion version) => Version = version;
}
