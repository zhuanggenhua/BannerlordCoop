# DEBUG campaign movement latency observer (2026-10-20)

This adapter records native campaign input, movement-model writes, received snapshot applications and native visual-frame updates. It does not issue input, advance time, simulate a party, replace movement, alter a network message, or calculate accepted latency. The coordinator registers `GameInterface.Services.LiveTesting.MovementLatency.MovementLatencyModule` once under `#if DEBUG` in the campaign container. There is no AutoActivate; resolving commands or the per-scope service does not install any hook.

## Commands and collection

- `coop.debug.movement_probe.begin <unique_label>` requires exactly two active, registered player parties with distinct controller and network identities. A label is 1–80 letters/digits/underscore/hyphen, cannot repeat within the scope; at most 128 labels per scope. Stop and retain pages before beginning another label.
- `coop.debug.movement_probe.read <fromSeq> <maxRows>` uses a zero-based inclusive cursor, initially `0`, and `maxRows` 1–256. Follow `nextFromSeq` while `hasMore`. Read after stop for stable pagination. Reading captures a fresh calibration clock inside the command, including after stopping; row clocks remain the original event clocks.
- `coop.debug.movement_probe.stop` removes this adapter's hooks and retains the captured rows. Repeated stop is harmless. Scope disposal removes hooks and actor references. A Campaign instance change stops the observer and invalidates the retained trace. No event or network subscriptions remain when disarmed.

Commands return one-line JSON as their output, compatible with existing `jsonOutput` accepting plain JSON or LIVE_TEST_JSON-prefixed JSON. Registered ICoopCommand results explicitly carry Succeeded/ErrorCode. These are diagnostic arm/read/stop operations; real movement remains the original `coop.debug.ui.map_click_offset` route or actual game input.

## Output schema (camelCase)

Each page contains `schemaVersion:1`, `label`, `armed`, `complete`, `overflow`, `droppedRows`, `fault`, `stopReason`, `capacity:8192`, `firstSeq:0`, `nextSeq`, `nextFromSeq`, `hasMore`, `association`, `clockCalibration`, `visualEvidence`, `clock`, and `rows`.

`complete` means a stopped diagnostic collection has no recorded fault/overflow. It does not mean a gameplay, latency, or P2P acceptance passed. Buffer capacity retains the first 8192 rows without overwriting; subsequent attempted rows increment droppedRows, overflow=true and complete=false. The command then returns probe_invalid alongside the actual JSON evidence. Invalid cursors/labels return probe_rejected. Epoch changes, Campaign changes, nonfinite recorded coordinates, a rejected original snapshot application or a caught diagnostic failure invalidate the trace. Original game exceptions are returned unchanged by the observer finalizer.

`clock` (both page.clock and row.clock) is `{pid,stopwatchTicks,frequency,utc,engineFrame,basis:"Stopwatch.GetTimestamp"}`. page.clock is freshly sampled, suitable for the coordinator to bracket with its monotonic request/response timestamps. Frequency equality alone is not cross-process clock calibration.

Each row contains `seq`, `kind`, `observationId`, `clock`, `campaign`, `epoch`, `authorityController`, `frozen`, `localController`, `actorController`, `originController`, `partyId`, `handle`, `x`, `y`, `targetX`, `targetY`, `requestedX`, `requestedY`, `visualX`, `visualY`, `mode`, `behavior`, and `applied`.

- `begin`: initial state of each of the two bound player parties.
- `input_enter`: real MapScreen.HandleLeftMouseButtonClick entry for local MainParty. requestedX/Y are the actual intersectionPoint passed to the native handler. Existing model position and navigation target are retained separately.
- `input_exit`: return from that handler. targetX/Y are the resulting MoveTargetPoint, not an assumed raw command offset.
- `apply_enter`: original MobilePartyBehaviorSnapshot.TryApply entry for a tracked player. requestedX/Y come from received PartyBehaviorUpdateData.MoveTargetPoint; originController preserves its original value, including null. This is game-thread snapshot-apply entry, **not** an on-wire packet receive timestamp.
- `apply_exit`: original TryApply result, recorded in applied. Rejection invalidates the trace.
- `native_position`: original MobileParty.SetPositionParallel completed. This is the actual native simulation position-write path. It can also be called with unchanged position; first motion must be established by comparing coordinates.
- `position_set`: original MobileParty.Position setter completed. It may represent a correction/teleport/apply; do not treat it as proof of native simulation.
- `model_frame`: game-thread queue drain boundary observation of both tracked models. This is a frame observation, not the exact internal movement write instant.
- `visual_frame`: original MobilePartyVisual.Tick completed and the adapter read the actual StrategicEntity.GetFrame().origin into visualX/Y. It is a native visual-frame-ready observation, not a screenshot pixel, GPU-present or physical display scanout timestamp.

Observation IDs are local counters, not protocol CommandIds. Input and apply observations have separate IDs. The same player can receive a stale self echo; do not infer causal association from a reused local counter. Root analysis must independently validate origin, actual unique target, Epoch and time window; repeated or multiple matches are ambiguous. No wire change was introduced.

## Boundaries and measurement

The original project already provides local prediction, stale self-echo protection, atomic movement snapshots, LiteNetLib reliability, authority lease validation and native visual updates. There is no evidence that replacing these algorithms is needed to obtain a latency baseline. This adapter uses temporarily installed observation-only Harmony hooks and scope-owned storage. It has no permission to call movement, save, Registry, AI, clock or authority mutation APIs. Existing core files are unchanged; the only production composition edit belongs to the coordinator.

Actual method targets are resolved with installed signatures at arm time: MapScreen.HandleLeftMouseButtonClick, MobilePartyBehaviorSnapshot.TryApply, MobileParty.SetPositionParallel, MobileParty.Position.set, MobilePartyVisual.Tick, and GameThread.Update. Missing signatures fail arm; actual Harmony installation/removal is tested. No blanket PatchAll registration is used. The disabled configuration has no installed hook.

Use a short 6–10 second two-player map series, bounded frame sampling, and read complete retained pages after stop. Separate local native-command-to-model response, local visual readiness, remote apply readiness and remote visual readiness. Use per-process clock brackets with uncertainty bounds, keep clock and frame resolution explicit, and reject overflow/epoch change/correlation ambiguity. Non-authoritative presentation position differences during continuous movement may be reported as drift/jitter; critical economy/troops/experience/identity/Epoch comparisons retain their strict quiescent rules.

The existing metrics CSV, 500 ms polling and asynchronous screenshot capture do not establish a sub-50ms result. No near-zero, completed-turn, genuine Hold-stop or physical mouse-to-photon acceptance is claimed by this observer. Mission behavior, packet receive timing, full checkpoints and lossless authority recovery are outside this change.

## Validation and integration

New tests cover bounded retention/overflow, cursor ordering and exact pagination, fresh calibration clocks, original event clocks, label isolation, stop/dispose/Campaign cleanup, unchanged frozen game objects, Epoch invalidation, parallel ordered rows, distinct input/apply/model/visual evidence, original exception preservation, installed native signatures, actual Harmony installation/removal and Autofac per-scope composition without AutoActivate. Release builds exclude the complete adapter and commands with #if DEBUG; a metadata inspection verifies absence.

The worker runs compile-only checks with `-p:ModName=`. Actual double-process execution and measurements remain coordinator acceptance. Preserve the initial compile failure and all later validation logs rather than overwriting them. Existing Coop 195 and selected E2E 10 historical failures remain separate and are not declared green by these tests.
