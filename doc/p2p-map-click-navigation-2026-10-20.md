# DEBUG map-click candidate qualification (2026-10-20)

## Existing functionality

UiDebugCommands.UiMapClickOffsetCoopCommand selects among four nearby rotated offsets using Face.IsValid and DoesPathExistBetweenFaces, then invokes the original MapScreen.HandleLeftMouseButtonClick once. It does not inject physical mouse input. Actual adoption is independently checked in the native movement trace.

## Evidence and minimum change

Native02 preserved 16 input/exit pairs: 7 targets adopted and 9 not adopted anywhere in the following model/visual observation window. This is a FAIL inventory, not an accepted latency/turning result. The read-only audit found that native MapScreen.HandleLeftMouseButtonClick and MapState.ProcessTravel additionally check NavigationHelper.CanPlayerNavigateToPosition. A face and geometric path do not establish that runtime player-navigation permission. The exact gate rejecting each original live event was not observed and remains unconfirmed.

Use that original NavigationHelper call in the existing DEBUG candidate loop, after its existing face/path checks. Count candidates rejected by this additional predicate in skippedPlayerNavigation, report the count on success or no-candidate failure, and retain exactly one original handler invocation. This adapter-level correction is sufficient to repair the helper's qualification gap; no evidence warrants changing the movement core, input pipeline, prediction, terrain algorithm or wire protocol.

## Invariants

Keep the four candidate offsets and their order, terrain/path checks, original handler arguments and one-call dispatch unchanged. Do not directly SetMove/SetNavigation, force camera flags, manufacture targets or mutate world state during preflight. A failure with no qualifying candidate dispatches no click. The skippedPlayerNavigation count covers only candidates that passed face/path and failed original player-navigation permission; it is not a total invalid-candidate count. A successful command still does not guarantee eventual native target adoption.

## Regression and native acceptance

This is a reversible, low-impact DEBUG QA helper change; do not write a mirrored navigation implementation or assertion-only copy of the loop. Compile Debug GameInterface with -p:ModName= and run existing UI/command/camera/party-command tests. The coordinator owns full regression and the next exclusive two-game run. Require original input and target-adoption evidence, retain the original native02 FAIL, and distinguish any remaining camera/menu/raft/MapEvent gate from the candidate permission check. No native repaired PASS is claimed by the worker.
