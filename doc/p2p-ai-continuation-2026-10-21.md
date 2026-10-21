# Finite AI continuation in the final movement checkpoint

2026-10-21. Implements ADR 0003 in the existing movement checkpoint adapter.

1. Existing functionality: the original MobilePartyAi constructor initializes its owner,
   fleeing leaf, randomized HourCounter and field initializer initiatives. Incremental
   receive deliberately skips it and binds only the owner. Existing movement snapshots
   already capture navigation, BehaviorTarget and interactable references.
2. Evidence and boundary: map05 activated B after navigation installation, then four
   incremental Bandit AI Tick calls threw. The saved stack has no IL offset and does not
   prove which dereference failed. Native MobilePartyAi.GetBehaviors independently
   dereferences _fleeingData.Clear when leaving flee behavior. A constructor-free replica
   lacks that leaf. An adapter can transfer the finite saved continuation without changing
   native Tick, native decisions, registry or receive construction.
3. Invariants: preserve party/AI identities, no observer constructor/RNG, no replay echo,
   complete reference validation before writes, frozen source epoch/ticks/hash, actual
   install and recapture equality before ACK. Navigation remains on the original path.
   A failed partial apply stays fenced; no rollback or lossless-recovery claim.
4. Regressions: constructor-free native decision RED, finite continuation roundtrip and
   exact live flags/counters/times/initiatives, identity/coverage/malformed rejection,
   no writes before complete validation, duplicate verification, existing movement and
   AI regressions. Unit tests do not accept map05, native Tick, natural day or Mission.

The bounded record includes the four fleeing booleans, HourCounter, disabled/decision
and alert flags, attack/avoid initiatives, initiative/next-check/enable-again/no-attack
CampaignTime ticks. BehaviorTarget and interactable/cache are already installed by the
existing navigation snapshot; no recursive MobilePartyAIBinaryPackage is used.

`MovementPayload` keeps member 1 unchanged and adds member 2, a distinct nullable
`CampaignAiContinuationPayload` with explicit version 1. An explicitly empty section
supports a world with no active parties; a missing section is not an empty complete
checkpoint. Each active party requires its registered AI handle and exact owner binding.
Finite initiatives are required; signed native counters/time ticks are preserved rather
than replaced with guessed ranges. Preparation validates all records before returning
the install action. The action applies original navigation, then scalar/owned flee-leaf
state on the existing AI. Full payload recapture equality precedes LastApplied and ACK.
An exact duplicate verifies without rewriting a changed AI or replacing its live leaf.

The native-method fixture uses real `MobilePartyAi.GetBehaviors` with a controlled AI
model: constructor-free AI leaving FleeToPoint throws with a null leaf; supplying only
that owned leaf lets the same native branch execute. After checkpoint installation,
source nondefault live flags are present and the original native Clear branch executes.
This establishes this finite missing graph, not the null instruction of map05's saved
Tick stack. No native AI method or algorithm is replaced by the adapter.

Worker validation: original navigation-only checkpoint RED 0/1 (leaf remains null),
then movement/AI continuation 48/0 and related movement/batch/routing regressions 68/0.
All existing assertions remain. Initial compiler/fixture development failures are kept.
Original E2E AI class filter is 1 PASS/6 FAIL on both the candidate and pure base
`e43187d9b19bd9df6ce2915865cbaff7a2a61109`, with identical seven per-case outcomes
and no added/removed failures. Original messages are retained: client instance numbers
vary with test order and one Siege teardown queue count is 332 versus 333; this is not
a byte-identical error-message claim. These six failures are separately base-reproduced,
not silently assigned to the designated historical ten. No game, deployment, full natural day or Mission run
is performed by this worker. Final integrated dual-process A-B-A remains unaccepted.

Rejections log the first party/AI handles and reason per invocation. Captured, prepared
and installed movement logs include AI schema version and record count alongside the
existing source epoch, tick and full payload hash. The final diagnostic-only delta was
verified by the 48 movement tests; the earlier related AI run was 68/0. No assertions or
legacy E2E code were changed.
