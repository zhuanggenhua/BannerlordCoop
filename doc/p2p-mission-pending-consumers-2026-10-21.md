# Pending Mission apply consumers, 2026-10-21

Base `f798aee97feab76ea2ab867ca942269eb0f342a3`. This first slice adapts only the existing location spawn queue. No reliable family or entry guard is enabled.

## Core boundary before implementation

1. `LocationPuppetSpawner` already decodes bounded batches, retries at most 64 records per tick, respects readiness/render capacity, deduplicates existing agents, tombstones despawns, drops departed players and retains former-host crowd records. Its original spawn/roster/navigation code remains the implementation.
2. `SpawnPuppetBatch` buffers plain records after the surrounding GameThread observation finishes. An outer relay adapter cannot keep that receipt pending or validate a later `DrainPendingPuppets`. A finite retained handle beside each original pending record is sufficient; no native algorithm needs rewriting.
3. Preserve batch order, budget, readiness, host withdrawal, sender/authority checks and original native results. Each pending record owns a distinct retain; a retry retains that same record rather than creating another hold. Source checks and apply activation grant no authority. A closed finite handle is never used as a persistent Mission packet source/guard. Stale cleanup operates on the actual removed record, not all records with its AgentId. Completion/cancellation callbacks run outside the queue lock.
4. RED/GREEN covers real broker-to-buffer lifetime, multiple records and delayed completion, retry, stale/throwing source, explicit native rejection versus success, native apply exception, despawn/player withdrawal/disposal, former-host retention, reentrant cancellation and same-Agent newer records. Original location generation/ownership suites and runtime DLL hashes/MVIDs remain separate evidence. Native scenarios are NOT_RUN for the worker slice.

## Bounded lifecycle contract

The existing list holds a small typed record containing the unmodified `LocationAgentSpawnData` and optional `MessageApplyScope.RetainedObservation`. Every received record owns its own retain from the current observation; a batch never shares one prematurely completed handle. No current observation preserves original behavior.

Enqueue captures the retain, drain validates and activates it at actual use, false native results requeue the same object, and successful/deduplicated apply completes it. Original `true` results that mean explicit rejection carry a rejection reason rather than certifying apply. Throws report failure and preserve the original exception. Despawn/withdrawal/disposal remove exact matching queued objects under the lock and settle their finite responsibility outside it; disposal also prevents already queued receive work from creating new pending records.

Battle pending spawns/formations/authorities/retained-player handoffs, location point-use records, and remote action/pilot-seat buffers remain unadapted and gated. Their persistent source receipts must stay separate from this finite lifetime API. This slice does not establish complete Mission reliable-message admission or native acceptance.

## First implementation evidence

The first stable RED run has seven failures proving plain buffered records finish the surrounding observation before actual apply. Two earlier new-fixture attempts are retained: rebinding GameThread conflicted with Coop.Tests' existing background pump; allowing assertions to escape that pump then aborted a second run. The fixture now marshals onto the existing pump and returns the original captured assertion exception to the test caller, with no production pump change and unchanged assertions.

A second negative round found two real lifecycle boundaries: budget calculation after dequeue could lose all finite handles, and a source validator can reenter Dispose after the record leaves the queue. The adapter now settles the extracted batch on budget failure while preserving the original error and checks disposal after activation. One new mock incorrectly targeted TryGetObject while production calls TryGetObjectWithLogging; correcting the mock preserves the actual error assertion. A reentrant-new-record fixture initially allowed that record to apply immediately; it now explicitly holds render capacity at zero so it actually exercises requeue and later consumption. All original failures remain in separate logs/TRX.

The first complete bounded consumer suite passes 18/18. It retains real broker/game-thread lifetime, per-record batch holds, retry, stale and throwing sources, success/deduplication versus explicit rejection, native apply failure, budget failure, descendants, despawn/withdrawal/disposal and disposal reentry, lock-free cancellation callbacks, same-ID newer records, former-host retention, and unobserved behavior.

Before production changes the complete original Services.Locations E2E command reports 42 passing and 36 failing tests (78 total, exit 1). The full test names/errors plus f798 source hash and actual Missions DLL SHA/MVID are preserved in location-baseline-detail.json. This specific result is not relabelled as the separately recorded E2E10 baseline. The final command must be compared test by test.
