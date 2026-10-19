# Final reliability evidence snapshot08

Overall P2P: **NOT COMPLETE**. Mission full critical-state acceptance: **FAIL / NEEDS_FIX**. Original5stage Mission assertions remainPASS; actual supplemental NPC coordinate criterion exits1/FAIL.

| Run | Original driver | A/B PID | Harness / actual game | Stage PASS/FAIL/NOT_RUN |
|---|---|---|---|---|
| p2p-sale-29 | PASS_BOUNDED_SCENARIO | 19312/12100 | 72c4b68e / 6b177722 | 9/0/0 |
| p2p-natural-r2-01 | FAIL | 22872 | 6750baa1 / 6750baa1 | 0/0/4 |
| p2p-natural-r2-02 | FAIL | 25960/15444 | 53e169d7 / 53e169d7 | 1/1/2 |
| p2p-mission-r2-01 | FAIL | 16588/20168 | 53e169d7 / 53e169d7 | 1/1/3 |
| p2p-sale-30 | PASS_BOUNDED_SCENARIO | 20676/19216 | 53e169d7 / 53e169d7 | 9/0/0 |
| p2p-udp-active-22 | FAIL | 22364/13424 | 53e169d7 / 53e169d7 | 2/1/2 |
| p2p-mission-r2-02 | FAIL | 21484/4032 | 7809a3cd / 53e169d7 | 3/1/1 |
| p2p-natural-r2-03 | FAIL | 24828/25364 | 9d38062f / 9d38062f | 1/1/2 |
| p2p-natural-r2-04 | FAIL | 15352/11744 | 1cb8a532 / 1cb8a532 | 1/1/2 |
| p2p-mission-r2-03 | FAIL | 25680/8752 | 1cb8a532 / 1cb8a532 | 3/1/1 |
| p2p-mission-r2-04 | FAIL | 19736/23240 | 5a3bc8f4 / 5a3bc8f4 | 0/0/5 |
| p2p-mission-r2-05 | PASS_BOUNDED_SCENARIO | 14468/5544 | 7b07de4b / 5a3bc8f4 | 5/0/0 |
| p2p-udp-active-23 | PASS_BOUNDED_SCENARIO | 17008/19048 | 7b07de4b / 5a3bc8f4 | 5/0/0 |
| p2p-udp-candidate-24 | PASS_BOUNDED_SCENARIO | 24632/26240 | 7b07de4b / 5a3bc8f4 | 2/0/4 |

Reg25 GI3058/0,Coop1442/195,Common423/0,selectedE2E61/10,build0 matches exact original195/10 failure names. Root108/108Node; completed coordination72/72. This is not a fully green suite.

Mission05 A14468/B5544: original5tavern stagesPASS; B HP100samevalue originalRanfalse, Atrue; exact pause worldTick/clock0 and gameUDP103/32. Strict world7/player16 are bounded. Supplementary same-tick NPC coordinate acceptanceFAILs: epoch6,4/24 samples differ in both paused snapshots6.779s apart,6042distance7.832806; final epoch9distance13.140798. No whole-world consistency or actual changedHP/battle-damage acceptance.

Active23 A17008/B19048: three original full faultedA-B-A rounds plus pre-request writerblackout safe freeze/refusalPASS5/5,76060ms/59samples. Its queued-barrier verdict remainsNOT_RUN. Candidate24 A24632/B26240: original candidate-only2stagesPASS, queued final barrierREACHED, sequence1/epoch4;394helddatagrams then394dropped,75707ms/60samples freeze, a fresh barrier required for rejoin. Candidate-only excludes activeNPCsimulation and lost-writer uncheckpointed recovery. Final-packet duplication stillNOT_RUN.

All14endedruns, 127 actualBMPs and 277 raw inputs are indexed; previous267inputs rehashed unchanged. Original large JSONL/log/BMP remainlocal with hash/bytes/PID/runToken bindings. Compact/inputmanifest plus small supplemental proof can be published; original failure data and all previous snapshots remain unchanged. Root will add retained-location mappings after worker cleanup.
