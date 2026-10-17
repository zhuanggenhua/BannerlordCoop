# P1-B: remote campaign player (accepted with documented limits)

Baseline: `91818b35ff2891fe608931a06107164ae6597f9c`, verified P1-A.
P1-B live acceptance was completed on 2026-10-17 across Runs #6–#9; the current code is
`e71e75c7d`. The evidence and limits below are part of that acceptance.

## 用户确认的总目标与一致性边界

总目标：每个玩家维护自己的战役副本，关键世界结果一致，次要内容允许各自不同；
房主可承担模拟时优先使用房主，后续允许其他可用地图玩家接管，全部无人可模拟时暂停。

- 必须一致：玩家身份和部队归属、世界时间、NPC 战役行为、部队兵力、同一地点的招募池及招募结果。
  同一个志愿兵槽位只能招募成功一次。同步允许网络延迟，不能长期分叉或重复生效。
- 玩家金币、已获得的部队和物品需要有稳定的归属与变更结果；不要求各玩家看到相同的商品和价格。
- 允许不同：商店库存、商品陈列、本地 UI 与表现。不以商店逐项相同作为 P1-B 验收门槛。
- 分布式副本不等于对同一个关键对象并发写入。任何时刻只有一个生效的模拟权威，房主优先通过后续交接实现。
- P1-B 仍固定 A 为权威，先验证加入、移动、NPC、招募、重连和保存恢复；不把迁移或 Mission 混进当前阶段。

## 实测结果与验收边界

- 双进程 Run #6（A PID 25324、B PID 23808，构建 `0.1.7+75352a86e`）持续运行 15 分 58 秒。B 通过 Direct IP 加入后，A/B 使用同一 Campaign ID；A 保持原生 `main_hero` / `player_party`，A 没有本地 NetPeer。
- 两名玩家都能在大地图移动。Run #6 中 A 离开城镇后实际发出地图移动输入，A/B 最终都看到 A 部队到达 `(658.7773, 278.1635)`；B 的移动也同步至 A。A 的 MainHero/MainParty 未因 B 加入、断线或重连改变。
- B 在达努斯提卡完成原生招募：B 金币 `1000 → 920`、部队 `1 → 5`；A 金币和部队不变。五名城镇名人的志愿兵槽位在招募前后均逐项一致。Run #7 冷启动恢复后再次比较五名名人的全部槽位，结果仍一致。
- A 原生招募曾实测 `1 → 11` 人、`1000 → 800` 金币；A 原生交易提交测试买入 777 金币的剑，A 的金币 `1000 → 223`，A/B 都显示一把剑。商店商品表和库存逐项一致不再是目标；这项交易只验证 A 本地结算与结果广播。
- B 同进程断线重连恢复到原角色和部队，金币 920、5 名部队不变。随后 Run #7 用两个全新游戏进程加载 `P1B_DirectIP_Recruit`：B 的持久角色、`town_ES1`、金币 920、5 名部队恢复；A 仍为 `main_hero` / `player_party`。双方 Campaign ID 相同，玩家英雄/部队/家族注册数均为 1。Run #7 已确认 A/B 两个进程均正常清理。
- Run #6 的房主诊断记录到 `realTicks=40918`、`duplicateRealTicks=0`、`failedRealTicks=0`、`aiTicks=619400`；Run #7 运行时也记录到 `duplicateRealTicks=0`、`failedRealTicks=0`，房主 AI Tick 计数持续增长。代码侧 `PartiesThinkPatch` 在客户端跳过 NPC `PartiesThink`，保留本地玩家 Escort 的特例。
- 冷恢复后比较到两端都有 1477 个相同的 MobileParty ID。抽查三支 NPC 部队时，兵种人数和伤员数相同；行军中的 NPC 坐标有约 0.07–0.32 地图单位的偏差，暂停后偏差保持不变。Run #9 后续确认移动过程中的抽样差距不超过 0.323，并在两支被抽查部队停止移动后回到完全相同坐标。时间同步也保留受控的本地显示偏差：Run #9 最大差 0.095 游戏小时，期间客户端日志有减速校正；权威 NPC 时间线仍由 A 独占推进。
- 同一 NPC 的兵种经验值会不同。例如 Run #7 和 #9 均观察到 roster XP 差异。现有 `PlayerTroopXpRelevance` 有意只把 XP 发给玩家所属或相关家族的端，观察端只收到人数/伤员变化。固定 A 权威且 NPC AI 只在 A 推进时，B 的 NPC XP 属于非权威副本细节，不改变当前权威兵力结果；未来战斗和权威交接前仍必须同步/重建 NPC roster XP。
- Run #7 日志中有大量原生存档加载后的 ObjectManager 未登记引用错误，类别与 Run #6 一致；B 端另有一条 `MobilePartyVisual` 重复注册错误。两轮均未发现 `Campaign.RealTick` 异常或 `NullReferenceException`；Run #7 房主计数器的重复/失败 RealTick 均为 0。完整日志留在各自运行目录中，不能据此宣称错误日志为空。
- Run #8 用修复版从同一存档冷启动 A/B 并完成 Direct IP 加入（A PID 30028、B PID 21980，GameInterface MVID `753e833c-bd86-47ca-9844-3a9290659eb7`）。双方 Campaign ID 都是 `RVLgBrkkFApz`；A 仍是 `main_hero` / `player_party`，金 1000、1 名部队；B 是 `Player` 英雄/部队，金 920、5 名部队。两端都报告 A/B 英雄、部队、家族注册数各为 1。A 的 RealTick 从 17196 增至 19562，重复和失败均为 0；两端读到相同 CampaignTime。修复后 B 端没有再出现 `MobilePartyVisual` 重复注册错误；Run #8 日志未发现 `Campaign.RealTick` 异常或 `NullReferenceException`。该轮停留城镇，未用于 NPC AI 推进验收。
- Run #9 在当前修复版从 `P1B_DirectIP_Recruit` 冷启动并 Direct IP 加入（A PID 27904、B PID 12240，build `0.1.7+162a2e449063560a2a978c016fcaafa0c7e7e781`，GameInterface MVID `753e833c-bd86-47ca-9844-3a9290659eb7`）。Campaign ID 均为 `RVLgBrkkFApz`；A 为 `ListenAuthority` 且仍是 `main_hero` / `player_party`，没有本地 NetPeer；A/B 玩家各自保留自己的英雄和队伍。A 的本地英雄、队伍、家族注册数均为 1。A 在地图运行 120 秒，`realTicks=22868`、`duplicateRealTicks=0`、`failedRealTicks=0`、`aiTicks=174900`，MobileParty 数为 1489。该轮时间、位置、兵力及清理数据见 [Run #9 对比记录](../artifacts/p1b/nine-live-comparison.json)。
- Run #9 的世界时间在开始时相同；运行样本中的 A/B `CampaignTime.Now` 差值为 0.095、0.093、0.081、0.074 游戏小时，且客户端日志有 `Slowing` 后回到 `Normal` 的校正记录。当前实现每 250 ms 收到主机时钟，并按 `MaximumClientLeadSeconds=0.35` 控制客户端本地节奏；样本显示的是小幅本地时钟偏差，不是 NPC 模拟权威分叉。该值不代表战役事件由 B 推进。
- Run #9 抽查 `lord_2_4_1_party_1` 与 `lord_6_1_party_1`：前 60 秒最大坐标差为 0.323 地图单位；90 秒与 120 秒样本两端坐标相同。对兵力按 roster 栈多重集合比较（键为兵种 ID、人数、伤员数、是否英雄；保留重复 ID 栈）后，两支部队 A/B 完全一致。直接把兵种 ID 当唯一键会错误覆盖 `lord_6_1` 的重复栈并制造假差异。
- 同一 Run #9 中两端 NPC roster 的 XP 字段仍不同：A 的非相关观察端数据会按 `PlayerTroopXpRelevance` 策略收到去除 XP 的兵力增量，详见 `TroopRosterDeltaHandler.WithoutXp`；已有 `AiParty_XpMutationsAreSuppressedWhileCountsReachEveryClient` 测试锁定此策略。P1-B 固定 A 为唯一 NPC 模拟权威，B 的 NPC 升级事件被禁用，A 的升级后兵种变化仍按 roster 增量同步，所以 XP 不同不会让本阶段的兵力结果分叉。该例外不适用于未来权威迁移或战斗阶段：交接前必须同步/重建 NPC roster XP。
- Run #9 日志检查：A 的 `Coop_server.log` 158,284 行，B 的 `Coop_client.log` 5,989 行；针对 `Campaign.RealTick` 异常、`NullReferenceException`、PartyVisual 重复注册签名、非零 duplicate/failed tick 计数的检索均为 0。没有把普通原生/自动注册日志误报为空错误日志。两端通过 `stop_run` 完整清理，进程树均已退出。
- 可复核的 Run #6/#7 原始记录： [加入身份](../artifacts/p1b/six-joined-comparison.json)、[双向移动](../artifacts/p1b/six-movement-state-comparison.json)、[B 招募结算](../artifacts/p1b/six-B-recruitment-result-comparison.json)、[断线重连](../artifacts/p1b/six-after-reconnect-comparison.json)、[冷启动恢复](../artifacts/p1b/seven-after-cold-rejoin-comparison.json)、[MobileParty ID 差异](../artifacts/p1b/seven-mobileparty-list-diff.json)、[15 分钟运行与清理](../artifacts/p1b/six-final-run.json)。
- 最新 GameInterface：2574 通过，包含两项 PartyVisual 生命周期回归测试。Coop：1274 通过 / 195 失败，与本次基线 1270 / 195 的失败名单逐项比较无差异；失败项没有新增。MCP 部署工具测试：250 通过。曾出现新增测试抢占全局线程标记造成测试主机中止，已修复并全套重跑通过；保留失败日志。
- 最新源码提交为 `e71e75c7d`；实机验证版本的 GameInterface MVID 为 `753e833c-bd86-47ca-9844-3a9290659eb7`。Coop 仍有 195 个历史失败项，但 fresh baseline 与当前失败项名单一致，没有新增回归。P1-B 接受范围限定在固定 A 权威、大地图玩法；NPC XP 偏差在此范围内允许，战斗与权威迁移前必须另行闭合。

## Work and acceptance ledger

- [x] Fresh Coop baseline: 1270 pass / 195 fail, `artifacts/p1b/tests/core-baseline.trx`.
- [x] Restore direct-IP admission, protect the local actor identity, validate remote movement ownership.
- [x] Verify snapshot/queue catch-up and A/B actor registration; fix the TownMarketData registration gap needed for campaign graph coverage.
- [x] Live: join, both move, NPC simulation, recruitment and player economy.
- [x] Live: disconnect/rejoin, save/cold restore, 15+ minute sustained run; compare IDs and registration counts.
- [x] Run final suites and compare failed test names with the fresh baseline; no new failed test names.
- [x] Fix the observed duplicate local party-visual registration and verify it on a fresh A/B join; the previous error did not recur in Run #8.
- [x] Define measured NPC position drift and the fixed-authority XP boundary: Run #9 stayed within 0.323 map units in sampled moving-party positions and converged to exact sampled coordinates; troop count/wounded multisets matched. NPC XP remains intentionally observer-local in P1-B and must be reconciled before authority migration or battles.

Keep the single Campaign, native A actor and authority composition. No local client loopback,
Mission work or authority migration. Live runs use the repository MCP and must finish with `stop_run`.
