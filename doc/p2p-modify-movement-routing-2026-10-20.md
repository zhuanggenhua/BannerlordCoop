# Campaign movement responsibility adapter

The existing movement patch captures the outermost native SetMove/short-term behavior command and
uses MobilePartyBehaviorHandler and the original snapshot mapper to publish a result. The existing
handler already distinguishes dynamic writer results from local player prediction. Its native
capture entrance still requires static IsServer, so B's native NPC commands never enter that path;
the observer position correction also still requires static IsClient, excluding A after transfer.
These are source-proven role conflicts, not proof of the first M05 packet that diverged.

Four change-boundary obligations:

1. Preserve the original nested command capture, native method body, snapshot/serialization,
   interpolation, position correction threshold, self-echo and original prediction algorithm.
2. A thin responsibility adapter suffices: use the existing world qualification at command capture,
   refuse replay/prediction scopes, and decide observer correction by confirmed local-writer role.
   There is no evidence requiring a movement algorithm rewrite.
3. Invariants at risk: exactly one outermost publication, no observer NPC publication, B writer
   publication, receive no-echo, legitimate local prediction, final freeze and held-navigation reset.
   Do not change global IsControlledByThisInstance or force every moving snapshot to set Position.
4. Regression: both static modes across A/B leases, freeze, restricted phases and AllowedThread;
   nested native capture only outermost; existing ListenAuthorityOperation and snapshot tests;
   final integrated two-process map movement/A-B-A with raw NPC position drift retained.

This slice changes role selection only. ResetNavigationToHold's original deferred producer has a
separate lifetime/lease boundary and is not modified here. Source checks are not native acceptance.
