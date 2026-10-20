# Native replica readiness after object ID assignment

Base: `1c863e12cb8a69d52cc399992de00adc4e8aa480`.

## Finding and correction

The first R3 natural-day run, `ecdb4dd8cf4b46418b0649bd0a377a53`, failed its final critical equality poll. A was PID 4436, B PID 8424. The original failure reported 73 B parties as `creation-pending`, from `Created_2902` through `Created_3046`. This did **not** prove missing handles, missing backlinks or failed AutoSync messages.

The retained B log has exactly 73 `Pending replica creation` lines and exactly 73 corresponding `Committed replica creation` lines, one pair for every reported party. For example, `Created_2902` both began and committed at 10:21:20. Every graph had therefore satisfied the five-handle and backlink gate and successfully returned from its original publication callback. The original failed summary SHA256 is `9fc6e75540c1f2a669d42ba2bb964b6c9915ce37618a07a047dabc748a198ad0`; it is unchanged. The local evidence audit records the individual original lines and log source/hash.

`CampaignObjectGraphReadiness.pending` used a default `Dictionary<MobileParty, Creation>`. Its callback in `MobilePartyRegistry.OnClientCreated` adds the party to native campaign objects and calls `MBObjectManager.RegisterObjectInternalWithoutTypeId`. Native object registration can change `MobileParty.Id`, which changes its native `GetHashCode`. The service then calls `pending.Remove(entry.Key)` with the changed hash, so the original entry remains. Its `Published` flag is true and callback null, preventing another publication but permanently refusing readiness.

The installed native `MobileParty` type reproduces this without a game: assign a real `MBGUID` inside the publication callback, assert its native hash changed, and inspect readiness. With the old dictionary this fails even though all five handles and all three backlinks are correct and publication completed once. With an identity comparer it passes. This controlled test exercises native ID/hash behavior; it does not execute the live native object-manager registration method or replace the required dual-process rerun.

## Scope and four core-change grounds

1. Existing function: retain tracked replica construction, five registered handles, distinct member/prison rosters, owner backlinks, exactly-once publication and actual-removal fencing. `creations` already uses reference identity through `ConditionalWeakTable`.
2. Evidence and minimal adapter: the original 73 committed log pairs plus native-ID RED demonstrate an adapter bookkeeping defect, not a reason to rewrite the constructor, AutoSync or registry. Only the adapter's pending dictionary needs the existing `Common.Network.ReferenceComparer<MobileParty>.Instance`.
3. Invariants: tracking remains tied to the exact native object throughout its ID changes. The same instance cannot publish twice, cancel twice into duplicate dictionary entries or survive actual removal as a stranded pending entry. `HasRegisteredGraph`, every handle/backlink check, original publication and original network algorithms are unchanged.
4. Regressions: real native ID changes during publication; subsequent ID change and repeated Begin/Publish; cancellation and ID changes before/during removal for both unpublished and published parties; the original graph-readiness and related lifetime/checkpoint suites. Actual natural-day and movement runs remain coordinator-owned acceptance.

No additional diagnostic format, timeout, delay, back-reference repair, object skip or assertion relaxation is part of the final change.

## Existing construction and sync paths

* `MobilePartySync` already registers `MobileParty.Party`; `PartyBaseSync` already registers `PartyBase.MobileParty`, `ItemRoster`, `MemberRoster` and `PrisonRoster`.
* `TroopRosterOwnerPartyRegistrationPatch` registers the roster first and explicitly publishes the owner reference after native `set_OwnerParty`. Its existing special handling avoids the small inlined TroopRoster constructor. This fix leaves it intact.
* `MobilePartyRegistry.OnClientCreated` creates temporary native attachments under AllowedThread, then defers world publication through readiness. All 73 successful original graph publications refute missing required construction references as the cause of this particular permanent pending failure. Other future missing-reference incidents must still be investigated individually.

## Verification and preserved failures

* `hash-red-native`: 1 FAIL, exit 1, old adapter with real native ID/hash mutation. Log SHA256 `4bf4dc006b5e046cf4ce04e75b6868c33f4ab8109026221c821f7f571e92430f`.
* `readiness-final`: 23 PASS / 0 FAIL, including three new native-ID cases. Log SHA256 `213efd17ec06aaceedd77dfb5374cfcfb6804811ebfff333fd0b239e304966d8`.
* `related-final`: 48 PASS / 0 FAIL using the same compiled assembly, covering readiness, checkpoint fencing, party lifetime and visual lifetime. Log SHA256 `7f70af3be52211887f249b6c1b93abec414f3b84fd637bcba554e1f433d5f41a`.
* An initial fixture compile failure (`hash-red`, missing MBGUID namespace) is preserved, exit 1. Before the native hash cause was identified, draft predicate-diagnostic tests were written. Their provisional `readiness-green` run was actually 10 FAIL / 23 PASS after diagnostics were removed but the draft tests remained; the log and actual exit 1 are preserved. Those uncommitted diagnostic tests were then removed with the abandoned diagnostic implementation. Existing original assertions remain intact. The final code retains only the three native-ID regression cases.
* No worker deployment, game launch or MCP call was performed. No complete-suite or native PASS is claimed. The coordinator must run integrated regression and dual-process native creation/movement, then repeat actual natural 24 hours and A-to-B-to-A acceptance. A quick spawn probe cannot replace that natural-day test.

## Existing short native creation probes

`coop.debug.mobileparty.spawn_test_parties 1 town_ES1` invokes the original `MobilePartyHelper.SpawnLordParty` for an available party-less lord near Danustica. It is server-side and has an explicit static client rejection, so use A while A is confirmed writer. It can report that no eligible lord exists; do not treat that as a creation PASS.

For a native looter path, `coop.debug.party_visuals.stage_over_limit_fixture` calls `BanditPartyComponent.CreateLooterParty` with clan `looters` near the supplied settlement. Its argument is a target eligible party count, **minimum 2500**, rather than a number of parties to spawn. Read `coop.debug.party_visuals.fixture_state` first and choose the current eligible count plus one only when that is at least 2500; otherwise this command is unsuitable for a small probe. It has companion `restore_over_limit_fixture` cleanup. Avoid using it merely to generate a large unrelated fixture. The existing stale-leader fixture also creates a bandit, but starts a MapEvent and changes other parties, so it is a broader probe than needed here.
