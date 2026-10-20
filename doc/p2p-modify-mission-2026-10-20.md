# MODIFY Mission endpoint implementation — 2026-10-20

## Change boundary and invariants

1. Existing MissionManager owns atomic membership, per-member credentials, conclusion fences, reconnect and relay routing. Its entry requires a real campaign NetPeer; ListenHost has no such connection. LocationHostHandler and BattleHostHandler coordinate original instance-host assignment and Mission HostEpoch.
2. An endpoint adapter suffices. Add typed local/remote identities at the membership boundary; reuse the same membership/conclusion state. No Registry, Owner/Puppet, Agent movement, damage, transport, serialization or native business rewrite is necessary. The local endpoint has no fabricated NetPeer.
3. Preserve one controller per instance, one peer per remote controller, credential rotation on remote reconnect, atomic departure/entry, current-session validation, conclusion fencing, per-Mission HostEpoch and the native MainHero/MainParty identity. A local endpoint cannot replace a remote endpoint or vice versa.
4. Regress local membership/duplicate entry/leave, local-to-remote fanout, remote disconnect retaining local occupancy, conclusion fencing, local identity rejection and unchanged remote reconnect. Later acceptance must cover actual shared Mission entry/election/relay, ordinary field battle and settlement results, asymmetric Mission world-writer eligibility and all-away pause/resume. Unit tests do not constitute native acceptance.

## First slice

Typed immutable LocalPlayerEndpoint and RemotePeerEndpoint feed the existing MissionManager. Existing remote overloads and remote-only tuple projections remain compatible; new endpoint snapshots retain local occupancy and distinguish it from transport peers. Local membership additionally checks the registered native controller. Local transport/NAT and room-control message dispatch remain a subsequent integration boundary; this slice alone is not shared-Mission support.

## Local room control and discovery slice

`LocalMissionRoomNetwork` is a Listen-only IRelayNetwork adapter over the existing room server, not another connection. Concrete control messages are published with the typed native actor as their source. The membership handler revalidates the actor/current remote peer on the game thread, replies locally through the original broker, and introduces local/remote members with the same per-membership credentials. Local NAT discovery authorizations require a current local membership and its issued credential; the existing Mission socket punches the existing room rendezvous. Tokens expire with leave and remain single-use.

The original remote overloads, NAT introduction algorithm and credential flow remain unchanged. Local-to-remote relay uses the original raw send only after same-instance membership checks. Remote-to-local raw relay is still refused by the existing NetPeer-only relay handler: a typed receiver must express the verified remote source before that path is enabled. No null/fake peer is delivered to PacketManager. Listen location entry remains closed in these slices, so this is not a claim of working shared Mission transport.

Validation: membership02 passed 81/81; GI endpoints01 passed 5/5; membership03 passed 84/84 including local reply, peer introduction, token lifecycle and concrete-message dispatch. Initial membership01 and membership04 compile failures are retained in ignored artifacts, corrected without changing assertions. No game or deployment occurred. Full shared native Mission, battle results and campaign resource settlement remain NOT_RUN.
