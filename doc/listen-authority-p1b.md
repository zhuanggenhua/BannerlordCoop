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

## 当前实测进度（尚未通过 P1-B）

- 已确认两个游戏进程可以加入同一战役；两端均登记 A/B，A 保持原生 MainHero/MainParty，A 没有本地 NetPeer。
- 已修复 B 继承 A 城镇菜单导致的空引用，后续加入未再观察到该错误。
- A 原生招募实测：1 → 11 人，1000 → 800 金币，B 端一致。
- A 原生交易提交修复后实测：买入 777 金币的剑，1000 → 223 金币，两端均持有一把剑。
  这证明本地玩家操作没有因预览重置丢失，不再扩展为商店全量一致性工作。
- B 地图移动已在两端观察到一致终点。A 离城修复后的完整移动、NPC 持续推进、B 招募、重连与保存恢复仍需实测。
- 最新 GameInterface：2572 通过。曾出现新增测试抢占全局线程标记造成测试主机中止，已修复并全套重跑通过；保留失败日志。
- Coop：1274 通过 / 195 失败，与本次基线 1270 / 195 的失败名单逐项比较无差异。
- 四轮已拥有的测试运行均已清理。最新源码提交为 `d4fa6cafe`；实机功能构建来自 `75352a86e`，后者之后仅修改测试。

## Work and acceptance ledger

- [x] Fresh Coop baseline: 1270 pass / 195 fail, `artifacts/p1b/tests/core-baseline.trx`.
- [ ] Restore direct-IP admission, protect the local actor identity, validate remote movement ownership.
- [ ] Verify snapshot/queue catch-up and A/B actor registration; fix actual registry blockers.
- [ ] Compare time, player identity, party IDs, positions, troops, gold and registration counts on both nodes.
- [ ] Live: join, both move, NPC simulation, recruitment and economy.
- [ ] Live: disconnect/rejoin, save/cold restore, sustained run; compare errors and IDs.
- [ ] Final suites and exact failed-test-name comparison, review, commits and evidence index.

Keep the single Campaign, native A actor and authority composition. No local client loopback,
Mission work or authority migration. Live runs use the repository MCP and must finish with `stop_run`.
