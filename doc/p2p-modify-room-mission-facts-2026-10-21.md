# Room Mission membership facts

This increment implements membership knowledge, independently of the campaign writer. Mission admission, battle claims, result settlement and the full ListenHost Mission entry remain closed pending their separate adapters.

Before changing the existing owners:

1. `MissionManager` already owns actual local and remote membership, credentials and relay routing. The existing membership interface exposes two read queries. Original join snapshots and catch-up own campaign objects; authority barriers own writer activation.
2. A client candidate has no room-wide membership registration. Same-instance peer announcements deliberately reach only Mission members. An adapter supplies a session read model, but the source needs an atomic detached snapshot and the barrier must encode the exact knowledge being acknowledged. An external wrapper cannot infer private membership mutations or add evidence to an old acknowledgement.
3. Preserve exact peer identity, credential and introduction ordering, original query meanings, one original operation, reliable ordered delivery, game-thread application, existing graph readiness and safe freeze. Emit notifications outside state locks. Never alter the local hero/party or execute native AI/economy during a facts installation.
4. Regress serialization, explicit empty versus uninitialized, duplicate/stale/conflicting versions, reconnect/disposal, real source peer validation at receive and apply, initial loading/catch-up, deferred lease application, changed membership during a barrier, and production DI identity. Existing movement, Owner/Puppet, transport, AutoSync, Registry and native algorithms remain intact.

`IRoomMissionFactsView` extends the existing interface without changing its two mature query contracts. A complete membership snapshot contains room identity, monotonically increasing revision, canonical hash and detached controller/instance/generation rows. It does not claim to contain battle arbitration or pending admission protection. A populated membership snapshot cannot authorize a world handoff until the protection contract is implemented; missing evidence remains an explicit refusal.

No native acceptance or performance claim follows from managed tests. The coordinator must deploy the final integrated build under the game lock and retain old failure evidence.


The initial reliable sequence is `SendImmediate(complete membership)` followed by `SendImmediate(lease with that version)`. Waiting until post-catch-up for the first facts would deadlock the existing delta lease validation; no save/replay algorithm was changed. `PlayerCampaignSynchronized` sends the latest complete facts again. An observer can confirm authenticated lease metadata once logical facts are installed without resolving native objects. A local writer candidate still requires the existing graph/task drain and the exact membership version. Every barrier and its apply acknowledgement carry the version; the room host rechecks it against its current atomic snapshot before committing. Membership changes refresh current lease metadata so a deferred observer does not wait forever for an obsolete version.

The server source activation check now captures once and compares the required version and empty coverage against that same snapshot. This corrects the independent review's double-capture race in the first increment. Occupied membership has no battle-claim/pre-entry proof in M3-A and is explicitly refused for new writer activation. This is an intermediate safety boundary, not Mission acceptance.


The first full Coop run retained all 195 historical failures and added one obsolete structure assertion: `ContainerBuildTests.Client_Container_Build` line 63 previously required that clients have no `IMissionMembershipRegistry`. The approved projection intentionally changes that architecture. The test now requires the concrete projection, both view interfaces and the same instance, and proves an uninitialized view cannot activate a writer. No gameplay consistency assertion or historical failure entry was removed. The original 196-failure TRX and exact name comparison remain in the task evidence.
