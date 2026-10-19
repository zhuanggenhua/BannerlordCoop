# Static winner banner guard — 2026-10-19

Base: `9d38062f3f3aabb8b573a24d82c1e1a67453c5ce`. This is a narrow caller fix; it does not change Army/PartyBase retirement, registry, AutoSync, Tick, networking or Mission synchronization.

## Confirmed trigger and boundary

Natural03 run70d3abaf977c486ab86daa27a21c0234 (A24828/B25364) retains a null MobileParty ERR at server.log:79255. Its immediately preceding DEBUG receipt79225 identifies IsPlayerParty from BannerCampaignBehaviorInterface.OnCollectLootItems, through the original OnCollectLootItems event/MapEvent loot-result path. The adapter assumes every winning PartyBase has a MobileParty, although a legitimate static winner does not. It calls the null-input validation unnecessarily before returning false. The exact winning PartyBase id was not recorded; no unrelated null or object-lifecycle error is waived.

Required four-point review:

1. Original behavior handles player mobile-party banner rewards: model reward item, defeated hero banner selection, chance/RNG, transfer and item changes; nonplayer winners return early. Preserve that mature body.
2. The caller's existing PartyBase.IsMobile property supplies the required qualification. A short-circuit guard solves this case; changing global IsPlayerParty validation, logging, registration or game reward logic is unnecessary.
3. Add only `!winnerParty.IsMobile ||` before the original predicate. Keep the complete remaining source bytes, reward model invocation, RNG, hero/item statements, original null PartyBase error and mobile NPC/player behavior. Unknown null winners must not silently count as static winners.
4. Retain red→green evidence; test static winner never invokes the null predicate/log or reward/RNG/item path, mobile NPC retains original predicate/return, registered player receives optional banner reward once, and null PartyBase still errors. Run existing lifecycle/cycle tests and root's exact full-regression comparison, followed by exclusive native04.

## Actual changes

`source/GameInterface/Services/Banners/Interfaces/BannerCampaignBehaviorInterface.cs`: one conditional line only. Static winners return before invoking MobileParty.IsPlayerParty. For mobile winners, the original predicate and complete existing body execute unchanged. The extension and its original ERR stay unchanged.

`source/GameInterface.Tests/Services/Banners/BannerCampaignBehaviorInterfaceTests.cs`: uses native PartyBase.IsMobile and the real player marker lookup, original inventory operations, a strict reward-model mock, a scoped log callback and observation-only Harmony counters for the player predicate and MBRandom float/int entry points. It restores Campaign.Current, removes its temporary player markers/log callback and unpatches its own owner on disposal. Static/NPC cases assert no reward-model call, RNG call, gained item or null-party log; both player optional-reward paths verify the original model called once and exactly one item when present. Null winner still throws before reward work.

Production byte check compares the checkout-filtered baseline file to the entire current file with exactly one expected substring replacement; it confirms the remaining reward body is byte-identical. Git stores LF while the Windows checkout uses CRLF, so the checker uses original `git cat-file --filters` bytes rather than comparing raw Git-blob EOLs. The initial checker EOL mismatch is retained as a checker failure, not a production regression.

## Validation and remaining acceptance

Before production edit:4 banner cases ran against9d source, **1 expected static-winner FAIL /3 PASS**. Original red log/TRX retained. After edit and adding explicit null-winner rejection regression:5 banner cases +9 lifecycle-probe +16 cycle tests, **30 PASS /0 FAIL**. No baseline assertions were lowered. No test claims all game reward scenarios or the entire solution are green.

Root still must run full regression against historical Coop195/specified E2E10 failures, then native04. Native03 remainsFAIL with5 original ERRs and only15.802507h of A progression. Native04 should specifically verify this null-party error no longer occurs at the reproduced NPC loot result; remaining Army/PartyBase errors must still make the natural scenario FAIL. No worker deployment, game launch/control or natural-cycle/Mission/P2P functional PASS is claimed.
