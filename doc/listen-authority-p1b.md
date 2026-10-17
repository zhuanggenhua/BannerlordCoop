# P1-B: remote campaign player (in progress)

Baseline: `91818b35ff2891fe608931a06107164ae6597f9c`, verified P1-A.
P1-B is not accepted until the two-process live checks below pass.

## 用户确认的总目标与一致性边界

总目标：每个玩家维护自己的战役副本，关键世界结果一致，次要内容允许各自不同；
房主可承担模拟时优先使用房主，后续允许其他可用地图玩家接管，全部无人可模拟时暂停。

- 必须一致：玩家身份和部队归属、世界时间、NPC 战役行为、部队兵力、同一地点的招募池及招募结果。
  同一个志愿兵槽位只能招募成功一次。同步允许网络延迟，不能长期分叉或重复生效。
- 玩家金币、已获得的部队和物品需要有稳定的归属与变更结果；不要求各玩家看到相同的商品和价格。
- 允许不同：商店库存、商品陈列、本地 UI 与表现。不以商店逐项相同作为 P1-B 验收门槛。
- 分布式副本不等于对同一个关键对象并发写入。任何时刻只有一个生效的模拟权威，房主优先通过后续交接实现。
- P1-B 仍固定 A 为权威，先验证加入、移动、NPC、招募、重连和保存恢复；不把迁移或 Mission 混进当前阶段。

## 当前实测进度（P1-B 仍未验收）

- 双进程 Run #6（A PID 25324、B PID 23808，构建 `0.1.7+75352a86e`）持续运行 15 分 58 秒。B 通过 Direct IP 加入后，A/B 使用同一 Campaign ID；A 保持原生 `main_hero` / `player_party`，A 没有本地 NetPeer。
- 两名玩家都能在大地图移动。Run #6 中 A 离开城镇后实际发出地图移动输入，A/B 最终都看到 A 部队到达 `(658.7773, 278.1635)`；B 的移动也同步至 A。A 的 MainHero/MainParty 未因 B 加入、断线或重连改变。
- B 在达努斯提卡完成原生招募：B 金币 `1000 → 920`、部队 `1 → 5`；A 金币和部队不变。五名城镇名人的志愿兵槽位在招募前后均逐项一致。Run #7 冷启动恢复后再次比较五名名人的全部槽位，结果仍一致。
- A 原生招募曾实测 `1 → 11` 人、`1000 → 800` 金币；A 原生交易提交测试买入 777 金币的剑，A 的金币 `1000 → 223`，A/B 都显示一把剑。商店商品表和库存逐项一致不再是目标；这项交易只验证 A 本地结算与结果广播。
- B 同进程断线重连恢复到原角色和部队，金币 920、5 名部队不变。随后 Run #7 用两个全新游戏进程加载 `P1B_DirectIP_Recruit`：B 的持久角色、`town_ES1`、金币 920、5 名部队恢复；A 仍为 `main_hero` / `player_party`。双方 Campaign ID 相同，玩家英雄/部队/家族注册数均为 1。Run #7 已确认 A/B 两个进程均正常清理。
- Run #6 的房主诊断记录到 `realTicks=40918`、`duplicateRealTicks=0`、`failedRealTicks=0`、`aiTicks=619400`；Run #7 运行时也记录到 `duplicateRealTicks=0`、`failedRealTicks=0`，房主 AI Tick 计数持续增长。代码侧 `PartiesThinkPatch` 在客户端跳过 NPC `PartiesThink`，保留本地玩家 Escort 的特例。
- 冷恢复后比较到两端都有 1477 个相同的 MobileParty ID。抽查三支 NPC 部队时，兵种人数和伤员数相同；行军中的 NPC 坐标有约 0.07–0.32 地图单位的偏差，暂停后偏差保持不变。时间同步也留有偏差：暂停后 A/B `CampaignTime.Now` 相差约 0.09 游戏小时，5 秒观察内双方都保持暂停，但没有将 B 硬校准到 A。差值可能来自本地移动预测/时间采样，目前还没有证明其收敛规则或确定可接受阈值。
- 同一 NPC 的兵种经验值会不同。例如 Run #7 中 `lord_2_4_1_party_1` 的 `sturgian_recruit` 经验 A/B 为 265/242，`lord_6_1_party_1` 的 `khuzait_nomad` 为 202/1558。现有 `PlayerTroopXpRelevance` 有意只把 XP 发给玩家所属或相关家族的端，观察端只收到人数/伤员变化。这不影响本次固定 A 权威、无战斗阶段的即时兵力数量，但可能影响后续晋升或权威交接，必须在同步战斗或权威迁移前明确纳入一致性规则。
- Run #7 日志中有大量原生存档加载后的 ObjectManager 未登记引用错误，类别与 Run #6 一致；B 端另有一条 `MobilePartyVisual` 重复注册错误。两轮均未发现 `Campaign.RealTick` 异常或 `NullReferenceException`；Run #7 房主计数器的重复/失败 RealTick 均为 0。完整日志留在各自运行目录中，不能据此宣称错误日志为空。
- 最新 GameInterface：2572 通过。曾出现新增测试抢占全局线程标记造成测试主机中止，已修复并全套重跑通过；保留失败日志。Coop：1274 通过 / 195 失败，与本次基线 1270 / 195 的失败名单逐项比较无差异。
- 最新源码提交为 `d4fa6cafe`；实机功能构建来自 `75352a86e`，后者之后仅修改测试和文档。当前 P1-B 仍未通过验收：需要先把 NPC 行军位置的同步/插值阈值说清并验证，并决定固定权威阶段是否要复制与未来晋升有关的 NPC 部队 XP。

## Work and acceptance ledger

- [x] Fresh Coop baseline: 1270 pass / 195 fail, `artifacts/p1b/tests/core-baseline.trx`.
- [x] Restore direct-IP admission, protect the local actor identity, validate remote movement ownership.
- [x] Verify snapshot/queue catch-up and A/B actor registration; fix the TownMarketData registration gap needed for campaign graph coverage.
- [x] Live: join, both move, NPC simulation, recruitment and player economy.
- [x] Live: disconnect/rejoin, save/cold restore, 15+ minute sustained run; compare IDs and registration counts.
- [x] Run final suites and compare failed test names with the fresh baseline; no new failed test names.
- [ ] Resolve/define acceptable NPC position drift and whether NPC troop XP must match before considering the shared world state complete.

Keep the single Campaign, native A actor and authority composition. No local client loopback,
Mission work or authority migration. Live runs use the repository MCP and must finish with `stop_run`.
