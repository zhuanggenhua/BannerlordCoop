# MODIFY Mission endpoint implementation — 2026-10-20

## Change boundary and invariants

1. Existing MissionManager owns atomic membership, per-member credentials, conclusion fences, reconnect and relay routing. Its entry requires a real campaign NetPeer; ListenHost has no such connection. LocationHostHandler and BattleHostHandler coordinate original instance-host assignment and Mission HostEpoch.
2. An endpoint adapter suffices. Add typed local/remote identities at the membership boundary; reuse the same membership/conclusion state. No Registry, Owner/Puppet, Agent movement, damage, transport, serialization or native business rewrite is necessary. The local endpoint has no fabricated NetPeer.
3. Preserve one controller per instance, one peer per remote controller, credential rotation on remote reconnect, atomic departure/entry, current-session validation, conclusion fencing, per-Mission HostEpoch and the native MainHero/MainParty identity. A local endpoint cannot replace a remote endpoint or vice versa.
4. Regress local membership/duplicate entry/leave, local-to-remote fanout, remote disconnect retaining local occupancy, conclusion fencing, local identity rejection and unchanged remote reconnect. Later acceptance must cover actual shared Mission entry/election/relay, ordinary field battle and settlement results, asymmetric Mission world-writer eligibility and all-away pause/resume. Unit tests do not constitute native acceptance.

## First slice

Typed immutable LocalPlayerEndpoint and RemotePeerEndpoint feed the existing MissionManager. Existing remote overloads and remote-only tuple projections remain compatible; new endpoint snapshots retain local occupancy and distinguish it from transport peers. Local membership additionally checks the registered native controller. Local transport/NAT and room-control message dispatch remain a subsequent integration boundary; this slice alone is not shared-Mission support.
