# Native Mission and day acceptance, 2026-10-21

Accepted gameplay binary: `94ae3fb6840ade1d5a5b7106c340c61295e1626d`, Bannerlord `v1.4.8.119303`, mod `0.1.7+94ae3fb6840ade1d5a5b7106c340c61295e1626d`.
This records a bounded Location lifecycle acceptance, not shared field combat or overall P2P completion.

## Mission04: five original features PASS

Run `d67c0b444a604c1b8a72100d4468a803`, A PID `23820`, B PID `26360`. Each player ran one Bannerlord process. Original native Location entry opened `empire_house_c_tavern_a`; no availability command substituted for Mission entry. Room host stayed A.

| Actual scene | Confirmed world lease | Native NPC AI delta | Campaign Tick/RealTick/LateAI delta |
| --- | --- | --- | --- |
| A in Mission, B on map | B, Epoch5 | B +3795; A authority AI 0 | B +84 each; A 0 |
| Both in Mission | Paused, Epoch6, no writer | Both total party AI 0 | Both 0 |
| B returns, A remains in Mission | B, Epoch7 | B +3795; A authority AI 0 | B +109 each; A 0 |
| A returns | A, Epoch9 | A +3794; B authority AI 0 | A +81 each; B 0 |

Native Mission time, rendering and bidirectional game UDP continued during the global pause. Final player identities, positions, gold, troops, XP and registrations matched; each hero/party/clan was registered once. All 24 sampled NPC positions were unchanged at the committed all-away pause. Ordinary moving-world Pause snapshots still had 4/24 NPC position offsets, maxima 5.7231308 / 4.9570956 / 0.5430602 map units; continuous exact NPC position is not accepted from these results.

The existing stable-lease authority simulation assertions passed. Per-call Mission role groups remain NOT_OBSERVED because this original scenario reads the `cycles` section. This is not proof of every instant of entry. Shared ordinary field battle, casualties and battle-result settlement remain NOT_ACCEPTED.

Actual configured `PlayerInitiativeQualification` witnesses appeared in A once (Epoch9) and B twice (Epoch5/7), each matching the saved room/authority/controller/party identity. This proves real application log delivery; it does not reconstruct the first historical MoveTarget write. Original actor logs contain no ERR entries. All 18 screenshot hashes were checked and both populated taverns visually inspected.

Raw evidence: `.mcp-local/p2p-modify-mission-m16-04.jsonl`, its `.summary.json`, `.server.log`, `.client1.log`, deployment log and native output. Final build SHA/MVID manifest: `.git/codex-coordination/evidence/modify-integrated-20261020/m16-regression05/deployment-provenance.json`. Both processes' loaded module identities match that deployment. Independent report: `.git/codex-coordination/evidence/modify-evidence-review-m10/mission04-native-review.md`, SHA256 `6db0129de59b61ce93e334ac81665c28535bdff21bbaa37d5c4cbbe60e582901`.

Actual scenario exit0; both process trees stopped, cleanup confirmed, physical game lock released. Integration receipt `4ca355b0-e262-4f01-95a0-6184795ffb57`, SHA256 `7d97e8260d40191b2441eb7dfa76ef928b2e10960353145c0d045de76b812616`.

## Final integrated managed baseline

Every project was explicitly rebuilt before its tests ran with `--no-build`; test DLL SHA/MVID and timestamps were checked before/after. All named historical failures were compared individually.

| Suite | Passed | Failed | Actual exit |
| --- | ---: | ---: | ---: |
| GameInterface | 3537 | 0 | 0 |
| Common | 496 | 0 | 0 |
| Coop | 1619 | 195 | 1 |
| Designated E2E | 61 | 10 | 1 |
| Original AI lifetime subset | 3 | 5 | 1 |
| Relation subset | 6 | 1 | 1 |
| World-lifecycle Node oracle | 45 | 0 | 0 |

No added failing names. The previously investigated original AI fixture queue-drain improvement remains separately documented; it is not a new production AI claim. Mod explicit rebuild exit0. Actual regression receipts: `.git/codex-coordination/evidence/modify-integrated-20261020/m16-regression05/closing-regression.json`. The overall suite is not green.

## Natural02: FAIL retained

Same binary, run `2f8954cd426845c085f1e6cadab1f752`, A PID `27120`, B PID `18752`. A naturally advanced approximately 24.4 hours under native Play_2x. The original pre-handoff day oracle observed 24 hourly slots, one daily slot and all 20 ticker families. Finance/wage-model and gold changes are observations, not proof of exact wage payment. B's own complete day and the return leg did not run.

After A-to-B handoff, the new per-call validator rejected one completed `Campaign.LateAITickAux` call on each node under the intermediate paused Epoch4. Both captured boundaries have dt0, CurrentTickCount5833 and unchanged world ticks78699883737596. The source/target business histories, installed scheduler phase and final critical world/player comparisons remained unchanged; authority NPC/player AI deltas were zero.

Fresh inspection of the actual game DLL (SHA256 `1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae`) shows `LateAITickAux` calls `PartiesThink` only when dt is positive or CurrentTickCount is below three. This strongly supports an empty native branch, but the saved run has no same-call branch probe or caller stack. Boundary values alone cannot exclude an intervening concurrent change. The whole scenario remains FAIL; no historical evidence is reclassified.

The next diagnostic must distinguish a proven empty call from actual simulation using same-call `PartiesThink` entry evidence, while retaining native total counts, strict business histories, role/clock checks and original exception identity. No AI permission or native business patch is justified by this failure alone.

Raw evidence: `.mcp-local/p2p-modify-natural-m16-02.jsonl` and companions. Fresh native source/receipt: `.git/codex-coordination/evidence/modify-natural-m16-02-native-source/`. Actual exit1; both trees stopped and cleanup confirmed. Integration receipt `7e062943-08b7-4c03-8b23-df36af529388`, SHA256 `777bf4246900b3b948d514785bfc74fea84f3d82b9704d3043bc55d2fc2ba451`. Mission01/02 and Natural01 FAIL evidence remains intact.
