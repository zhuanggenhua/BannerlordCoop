# Frozen movement acceptance

Baseline `a9a984a3ffb2afb6d468e2419424bb442ceebd42` retains the original M05 run and its five producer-stage PASS results. The separate same-tick NPC position acceptance remains FAIL.

The existing DEBUG `world_state` command already enumerates every campaign party. Optional `positions` detail now returns every active party's registered handle, identity and native coordinates, plus count, campaign, lease epoch, world ticks and stopped-clock evidence. It does not change movement, simulation or registration. Ordinary calls retain their prior hashes and sample.

The all-away Mission check now requires cross-peer equality of the original NPC sample and the complete active-party capture, in addition to all original same-peer clock, world, player and cycle checks. Empty, incomplete, unregistered, invalid or different-epoch evidence cannot establish equality. Differences outside the first 24 sampled NPCs also fail.

Existing movement and authority protocols remain unchanged by this QA commit. Installing a handoff movement baseline is a separate feature and requires real dual-process acceptance.

Verification in `codex-coordination/evidence/p2p-reliability-r3-20261020` under the Git common directory:

- Scenario Node tests: 116 PASS; `qa-node-final.log`, SHA256 `5906810f4713ae12339c640cbb5b083f9e95321f0b29b56af3919b394e622ab9`.
- Existing DEBUG command tests and compile: 6 PASS; `qa-command-build.log`, SHA256 `10ba80217d6ab8fa8e26b4724d4916c2e64751bc6295d6aa6f431acef1f24dcc`.
- Read-only replay rejects M05 for its actual same-tick position difference, preserving original bytes; `mission05-strict-replay.json`.
- Independent review found two empty-evidence gaps in the first QA draft. Positive counts and valid unique identities/coordinates were added before final review; all prior logs are retained.

These checks are offline. They do not establish a native Mission or complete P2P PASS.
