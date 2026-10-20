# 自然跨日交接验收边界（2026-10-20）

仅改 QA 编排，不修改生产世界、移动或暂停协议。原 Natural03 FAIL 及其原始日志、截图、SHA 保留；本次 Node 测试不能替代新的双进程自然跨日验收。

## 修改前四项依据

1. 原实现已有自然每 writer 至少 24h、严格 world/player/semantic 快照、完整周期分页、周期窗口及 scheduler 交接检查；cold-save 使用完整语义持久化比较。
2. Natural03 在普通 Pause 后等待完整预测位置相等失败。普通 Pause 不发送交接的最终移动基线；仅需在已有真实交接屏障两侧分阶段取证，无需改生产核心或增加普通暂停协议。
3. 保留所有既有断言。交接前严格比较 world/player 以及语义对象除显式 `.npcs[i].x/y` 外的全部字段，保留有限坐标及完整差异证据；原始位置不改写。交接后原完整语义比较仍包含精确 NPC x/y。金币、兵力、经验、身份和未知字段不豁免。
4. 补充阶段顺序、单次变更、pre 资源失败不得交接、post 坐标失败、周期失败/溢出/遗漏、暂停与时钟不变、尾窗与 scheduler phase 的函数级测试；保持 cold 路径原样，集成后仍须实际游戏验证。

## 编排

每个 writer 自然推进至少 24h 后：原普通 Pause → 交接前严格关键状态与预测坐标差取证 → 原周期窗口验证 → 原真实交接一次 → 确认新 writer → 暂停状态下原完整精确快照 → 再读完整 collector 封旧 writer 异步尾窗 → 原 scheduler 交接比较。

A 日末仅执行一次原 `yield` 交给 B；B 日末仅执行一次原 `auto` 交回 A。读轮询不重试变更，不增加超时，不以 checkpoint 修复掩盖交接前资源差异。不删除或改写历史 FAIL。

旧 Natural03 run `c6ffbe2d2fcd4a5a8739e7d9bf0fec0a` 实际自然推进 **94.3929334722h**，不是仅 24h：日志采集期间游戏仍运行，超过最低 24h 阈值。本次不扩展日志性能改造，保留实际起止时钟及超跑值。独审取证证明 27 组同 Tick 比较均有相同 20 个 NPC 的 40 个 x/y 差异，且两端语义文档各自在 42.774s 内不变；最早活动帧差异原因未观测，不能将本次编排修正称为预测精度修复。

原始失败 RAW SHA256：`b48d0e6b7fc668b57e9b5f937b17f310dfb76c07c8fbbaf5ff256367b343b72c`。依据为只读任务 `p2p-natural-pause-position-r5` 的封存 `audit.md`、`extracted.json`、`verify04.log`；原 ordinary Pause 断言失败不回写成 PASS。新实机仍须完成两个 writer 的自然 >=24h、完整周期及 A→B→A。

## 自动化与实机边界

本任务生命周期测试 25/25、十份相关 Node 场景套件 148/148 通过。新增七组覆盖 pre 资源/未知字段拒绝、完整坐标取证、A/B 两种旧 writer、唯一交接、确认/变更异常不重试、post 精确坐标/时钟/Pause/房间/Epoch、尾窗周期异常/重复/缺失及 scheduler phase。原 18 个生命周期测试均保留。

源码核验确认 `assertSemantic`、`assertPersistent`、`assertCycles`、`assertCycleWindow`、`assertSchedulerHandoff`、`assertColdLease`、原严格 `snapshot` 及 cold-save 分支正文未变。共享命令能力清单新增现有只读 `coop.debug.get_time_mode`，使必要的 Pause gate 在 preflight 明确可用；未创建新命令。

barrier 后重读 collector 仍运行原完整历史断言和原日窗口，包括原 observer 的权威模拟计数不得增长。不会预先豁免新 writer 的零 delta Tick；如果实机失败，须按日志/原生调用链归因。没有 C# 编译、部署或游戏运行，不宣称新的自然跨日或整体联机验收通过。
