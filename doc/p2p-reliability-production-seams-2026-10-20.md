# P0 production composition

Baseline a9a984a3ffb2afb6d468e2419424bb442ceebd42. This records wiring and required acceptance, not native PASS.

## Core boundary before editing

1. Original MobilePartyRegistry initializes received replicas, adds/removes them from the native world and publishes typed destruction. The roster checkpoint already validates all original identities/counts and restores XP without replaying XP effects. Keep these implementations.
2. Active22 exposed constructor-created temporary attachments before the original ordered reference messages replaced them. Natural04 removed identities before the outer native call completed. A frozen retry can bypass the initial async drain. Timing adapters and a checkpoint decorator resolve these evidenced boundaries without rewriting registry allocation, AutoSync, movement transport or roster algorithms.
3. Preserve exactly-once native publication/removal, original exceptions/ERR, existing unknown-handle rejection, original message order and lease qualification. Deferred replica publication is received state and uses AllowedThread; authority simulation never acquires that receive scope. Pending/faulted state must refuse capture/apply/ACK, including retry.
4. Verify production server/client DI references the same session completion services, scope isolation, graph and lifetime refusal before/after native task completion, unchanged successful checkpoint data and exactly one delegation. Native acceptance still requires both processes, complete frozen coordinates, real natural24h and original UDP fault scenarios.

## Seams

- Register ObjectReadinessModule, CampaignLifetimeCompletionModule and MovementModule in GameInterfaceModule once per session scope. Legacy direct constructors remain optional only for existing tests; production registration tests require all services.
- MobilePartyRegistry tracks the received creation before temporary attachments, submits the unchanged native world-add callback to readiness, and records removal completion only after the original world removal succeeds. Missing native object managers now produce an explicit failed publication instead of a silent successful return.
- The original roster service is wrapped by ReadyCampaignRosterCheckpointService. Both graph and typed lifetime must be complete before and after capture/apply. It does not repair unknown identities or change roster contents.
- CampaignAsyncTaskDrain checks the same completion services before and after native WaitAsyncTasks. GameThread.Update's typed postfix observes direct AutoSync field application without modifying the generator.
- WorldStateCommand returns object_graph_not_ready for positively tracked incomplete construction. Untracked or formerly committed missing identities still reach the original diagnostics and ERR.

No deployment or native acceptance is implied by unit tests. The existing five-part checkpoint is not a complete crash-recovery transaction; missing full state continues to require safe freeze.
