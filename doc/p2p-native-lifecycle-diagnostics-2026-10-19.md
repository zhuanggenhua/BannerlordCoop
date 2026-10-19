# Native lifecycle diagnostic adapter — 2026-10-19

Base: `53e169d7338a8c3d6c4fc320634132de2c03833a`. This is a DEBUG-only evidence adapter, not an NPC lifecycle fix. Original Natural02 FAIL and raw logs remain retained. No game/deployment was performed by this worker.

## Evidence and change boundary

Natural02 run `78ebd66dcfaa4165b7378640be1380cf`, A25960/B15444, produced1 null MobileParty ERR,3 Army missing-id ERRs and1 PartyBase missing-handle ERR (`reference:000fabdf`). The failed PartyBase value was sent from the original AutoSync LeaderParty path during native MapEvent finalization. Logs do not yet prove that reference is the earlier retired garrison; Army/null callers also lack stacks. Only15.63026186125353h of A natural simulation occurred, not full24h or B's cycle. Those failures are not waived by unchanged lifecycle source vs78ac.

Four-point review:

1. Existing ObjectManager owns lookups/removal and retains original error logging; existing MobilePartyExtensions validates null input. Existing AutoRegistry DEBUG logs type/id/stack but lacks identity/handle, and Army/PartyBase do not enable it. Original mature synchronization and retirement behavior remains protected.
2. Read-only nongeneric Harmony adapters suffice to capture missing identity/caller evidence. No change to AutoSync, registries, engine timing, native leader updates, object retirement or game actions is necessary or justified yet.
3. Preserve exactly one original execution and its bool/out values, exception, registration state and original ERR severity. No new severe log for expected lookup misses; no replay, game setter, RNG, action, scheduler mutation, static object-state table, packet/schema change or closed-generic detour. Debug logging failures must make evidence incomplete instead of creating false acceptance.
4. Test original mappings before/after observation, real Remove true/false behavior under patches, untouched original errors/null results, restricted object types, diagnostic read/log failure visibility and original exception preservation. Check real DesktopCLR binding before root's full regression and exclusive native re-run. Automatic checks do not replace native acceptance.

## New adapter and exact targets

`source/GameInterface/Services/CampaignAuthority/CampaignObjectLifecycleDiagnostics.cs` only, under `#if DEBUG`:

- `ObjectManager.Remove(object)`: Prefix reads existing id and handle with original nongeneric lookups, runtime identity via `RuntimeHelpers.GetHashCode`, type and stack. Finalizer logs `ranOriginal`, actual bool result and original exception, then returns that exact exception. Finalizer covers failure as well as success; it never re-reads a faulting registry after the original exception.
- `ObjectManager.TryGetId(object, out string)`: Postfix emits DEBUG context for failed Army/PartyBase lookups only. Null/other objects, success, and skipped original bodies are excluded. A miss can be legitimate; this is not another ERR.
- `MobilePartyExtensions.IsPlayerParty(MobileParty)` and `IsControlledByThisInstance(MobileParty)`: Prefix reports the exact helper and caller stack only when its input is null. The original helper still runs, reports its original ERR and returns false.

`reference:xxxxxxxx` uses exactly the runtime-reference formatting already used by existing handle failures; the process PID/role in original log headers scopes its identity. Before/after records retain the original registered id and handle without assuming any NPC identity or world event.

Diagnostic read exceptions are isolated from the original Remove and retained as `ObservationError`. Diagnostic output exceptions use Trace fallback. Both increment explicit process counters and, when a Campaign exists, the existing per-Campaign simulation keys `CampaignLifecycleProbe.observation.failed` / `CampaignLifecycleProbe.logging.failed`. Existing cycle acceptance rejects `.failed`; if even the diagnostic collector is unavailable, process counters remain nonzero. No original game exception is swallowed or converted to success. Before a Campaign exists, only the process counter is available; this is not campaign-cycle acceptance evidence.

## Actual checks and retained failures

Final focused run:9 new lifecycle tests plus16 existing campaign-cycle tests, **25 PASS /0 FAIL**. Scope includes real nongeneric patch binding and actual registry Remove/lookups, existing two missing-id ERRs, both null helper false returns, state/handle stability, Debug-only ordinary misses, same original exception, read failure and logger failure counters, and current cycle `.failed` keys. This is targeted coverage, not a claim that the whole suite is green.

Two initial test-fixture failures are retained in ignored artifacts: Harmony `Patch()` returns generated replacement DynamicMethods whose DeclaringType can be null, so ABI checks now inspect original targets from `GetPatchedMethods()`; the no-authority ObjectManager constructor allows id-only registration, so the handle-required test now uses the original explicit-handle overload. Neither change relaxes production behavior or a native assertion. No production failure was suppressed.

Fresh64-bit Windows PowerShell5 DesktopCLR probe bound all7 previously added cycle classes plus3 new lifecycle classes: **10 classes /31 targets PASS**. The probe records CLR/Harmony version, actual resolved assembly paths, SHA256 and MVID for worker GameInterface, Harmony and installed CampaignSystem. It does not launch a game and proves binding compatibility only. Original generic failure evidence remains in coordinator archival storage.

Root must still run full baseline comparisons (historical Coop195 /specified E2E10 failures are not green), exclusive native03, correlate `000fabdf` to an actual retirement event, and identify Army/null caller stacks. No natural cycle, NPC economy, Mission lifecycle or seamless recovery acceptance is claimed here. Do not change retirement or skip native leader transitions until new evidence selects a cause.

Independent review of the first diagnostic commit8085cdc2 reproduced a custom exception whose ToString itself throws: formatting outside the logging guard could replace the original exception and bypass counters. The follow-up uses a narrow exception-text formatter that records observation.failed and returns an explicit unavailable marker. The original exception object is still returned. A negative test covers both finalizer formatting and diagnostic-read exception formatting. The reviewer retained the original DesktopCLR FAIL; the final focused and fresh DesktopCLR binding runs were repeated after this repair.
