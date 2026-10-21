# Room Mission membership facts

This increment implements membership knowledge, independently of the campaign writer. Mission admission, battle claims, result settlement and the full ListenHost Mission entry remain closed pending their separate adapters.

Before changing the existing owners:

1. `MissionManager` already owns actual local and remote membership, credentials and relay routing. The existing membership interface exposes two read queries. Original join snapshots and catch-up own campaign objects; authority barriers own writer activation.
2. A client candidate has no room-wide membership registration. Same-instance peer announcements deliberately reach only Mission members. An adapter supplies a session read model, but the source needs an atomic detached snapshot and the barrier must encode the exact knowledge being acknowledged. An external wrapper cannot infer private membership mutations or add evidence to an old acknowledgement.
3. Preserve exact peer identity, credential and introduction ordering, original query meanings, one original operation, reliable ordered delivery, game-thread application, existing graph readiness and safe freeze. Emit notifications outside state locks. Never alter the local hero/party or execute native AI/economy during a facts installation.
4. Regress serialization, explicit empty versus uninitialized, duplicate/stale/conflicting versions, reconnect/disposal, real source peer validation at receive and apply, initial loading/catch-up, deferred lease application, changed membership during a barrier, and production DI identity. Existing movement, Owner/Puppet, transport, AutoSync, Registry and native algorithms remain intact.

`IRoomMissionFactsView` extends the existing interface without changing its two mature query contracts. A complete membership snapshot contains room identity, monotonically increasing revision, canonical hash and detached controller/instance/generation rows. It does not claim to contain battle arbitration or pending admission protection. A populated membership snapshot cannot authorize a world handoff until the protection contract is implemented; missing evidence remains an explicit refusal.

No native acceptance or performance claim follows from managed tests. The coordinator must deploy the final integrated build under the game lock and retain old failure evidence.
