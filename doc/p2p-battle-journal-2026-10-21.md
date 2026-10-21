# Battle preparation history foundation (M18)

Status: session/save/checkpoint foundation only. No new battle admission or execution path is enabled. Authenticated room claim production, real async-drain receipts, live native preparation capture/install/readback, and shared ordinary-field battle acceptance are NOT_IMPLEMENTED / NOT_RUN.

## Ownership and finite contract

`ICoopSession.BattlePreparations` and `CoopSession` Protobuf member19 retain finite operation facts. Existing tags1..18 are unchanged. Missing old JSON/Protobuf state normalizes to a separate empty instance. The session constructor copies the immutable state; input/output arrays and initializer bytes are defensively copied. The existing provider remains the owner, with no second journal manager or global queue.

`BattlePreparationKey` is original RoomId + native CampaignId + MapEvent original registry ID/handle + once room-owned ClaimId/generation + InitialOrdinaryField. RequestId, requester/controller and Campaign Epoch are excluded. ExecutionEpoch is historical metadata. No claim generation or authentication producer is added: constructors represent data, not authorization. A subsequent producer must supply real room-owned lifetime and ownership evidence; no caller boolean, per-replica Guid, current RoomHost or serialized phase grants world-write permission.

Canonical initiating party and initial cohort/reservation facts remain part of the immutable input. Read-only `Inspect` reports existing prepared/history, conflicting input, missing key or blocked partial/unknown state. This query is not an execution permit or proof that native state is installed. Unknown schemas, malformed or duplicate lifetimes and inconsistent cohorts are rejected by the history transfer gate.

The typed payload retains graph references, flattened descriptor/state/XP facts, side ready-priority bits and original initializer bytes. Tests serialize an actual MissionInitializerRecord using the existing surrogate; troop/side fixtures are typed history examples, not a claim of native preparation capture. In particular, this first slice does not certify that those fields are the entire live native continuation surface.

## Actual consumers

- `CampaignSessionCheckpointService.BuildSession` preserves the new state while retaining existing registered actor identities and local InventoryPlayerData behavior. Capture and Apply validate it. Only retired history can transfer. Local partial state cannot be erased, completed results cannot be dropped or replaced by a new checkpoint, and original room/claim identities are never rebound. Refusal feeds existing `CampaignApplyCompletion.RecordFailure`, so later Epochs cannot turn the failed boundary into an ACK.
- `SaveGameHandler.WriteCoopSession`, `Handle_GameLoaded` and `CopyWithLease` all retain the state. Unsupported active/partial restoration fails through the existing admission/finalizer path while leaving the saved file intact. Historical retired records remain associated with their original room even if the enclosing session gets a fresh boot lease.
- Original `CoopSessionMigrator.MigratePlayerData` and `NetworkUpdateCoopSession` remain unchanged. A real migration/network-envelope test verifies that migrating per-hero dictionaries does not change battle lifetime or result facts.

No MakeReady, hostility, Update, RNG, AI, mission launch, battle damage, casualty or reserve algorithm is called or changed. Retired initial payload is never applied onto live casualty state. Active Preparing/Prepared/Unknown records deliberately reject capture/apply/load until authenticated producer + no-new-task drain boundary and complete native readback are implemented. Non-null ITask alone cannot establish completion; ITask only exposes Invoke/Wait and may be reused.

## Verification and retained failures

The interrupted session serialization RED (3 failures due missing member19) is retained. New tests cover nonempty JSON/Protobuf, actual initializer serialization, absent legacy members, detached arrays, stable operation identity and conflict/unknown lookup, existing checkpoint actor/local-inventory behavior, retired graph-free transfer, sticky failure across Epoch, history drop/replacement rejection, real save/write/load/copy, unsupported load refusal and original hero migration/network envelope.

The related GI selection has 28 PASS / 0 FAIL. The independent save/copy selection has 2 PASS / 0 FAIL. The broader save group produced 37 PASS / 11 FAIL (actual exit1). Every old failure name and full exception message exactly matches the original195 baseline,94ae regression05 and7d regression06; the per-case comparison is retained. The old SaveLoad test fixture/expectations were not modified. New tests use an independent fixture in the same authorized file so they actually execute instead of failing before their bodies. Initial compilation/import and internal-interface Moq fixture failures are retained as failed attempts, not hidden.

Full Coop/E2E suites and native dual-process game were not run by this worker. This commit does not complete native preparation, dedup execution, shared battle or P2P acceptance. Root must review and integrate this finite foundation before authorizing the actual claim/prepare/drain continuation stage.
