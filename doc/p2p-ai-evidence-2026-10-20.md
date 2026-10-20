# AI 与异步等待诊断角色（2026-10-20）

仅 DEBUG 诊断及 QA 适配，不改 AI、权限、导航、任务生命周期或原生调用次数。原 Native04/Natural05 FAIL 保留，不由自动化或计数修正追认为 PASS。

## 修改前四项依据

1. 原诊断已有 `MobilePartyAi.Tick` 和 Campaign 调用完成/失败总计，但没有入口资格、玩家归属和接收线程分类。`CampaignObserverFrame.Tick` 原本合法调用自己 MainParty 的 Ai.Tick，raw 总计增长不等于观察者推进 NPC 世界模拟。
2. 可通过诊断入口快照及 QA 角色证据解决，无需重写生产 AI。独立真实 CLR4 又证明 RealTick 先编译、Wait 后 patch 会漏记真实 Wait；class-end identity 重建 parent 后原 Task.Wait 四次仍四次，计数恢复三成功/一异常，无需多调用 Wait 或改 ITask。
3. 保留 raw 总计和原异常；分类逐 completed/failed/skipped 与 raw 对账。只有有效 confirmed lease、未冻结、本地控制身份与实际 MainParty/Ai 引用一致才可记合法观察者导航。接收应用单列但不能隐藏越权 NPC；unknown、冻结、失效/变化的资格仍拒绝。身份与世界权威不由 IsServer 推断。
4. 补充真实入口角色、冻结/无效 lease、错误 party/controller/AI 实例、AllowedThread、原异常/跳过、分类总和/原子快照/有界首证据及 QA 严格反例。Wait 修复用实际 CLR4 父路径和独立 Task.Wait 次数验证；编译均 `-p:ModName=`，不部署/游戏。

## 证据边界

`aiSimulation.schemaVersion=1` 导出所有角色和三个 outcome 的显式计数（零值也存在），`MobilePartyAi.Tick.observed` 是实际入口累计数，不是编译常量。raw 与分区、正交 `unexpectedWorld` 在同一个 collector 锁中更新和读取；每类/outcome/unsafe 的首证据最多 36 条，含入口及退出 lease、身份、原因和异常。内部对象引用只用于 `ReferenceEquals` 核验，不进入 JSON。

已确认自然日窗口要求 writer NPC 和 observer 本人导航都有正向覆盖。窗口内新增 unknown/unqualified/unsafe 执行拒绝；窗口前启动/载入的未知计数保留且不被追认为窗口内新增越权。接收线程不能使观察者 NPC 调用变合法；失败与跳过分别记账，原异常对象返回不变。原始周期、债务、成员顺序、最终严格快照和 scheduler 断言不变，工资因果支付仍为 `NOT_OBSERVED`。

本候选的相关 C# 测试 69/69、七套 Node QA 113/113 通过，Release 构建 0 错误（27 个已有警告）；初次 Node 语法错误和 paused 夹具错误日志保留。CLR4 的候选 Wait 原路径独立复验与最终双进程自然日验收由总控另行记录，不能以这些自动化结果代替。现有 Natural05 的 Wait `1→1` FAIL 和此前全部原始失败证据不变；本补丁没有额外调用 Wait/Invoke、没有调整世界模拟资格或启动第二个 driver。
