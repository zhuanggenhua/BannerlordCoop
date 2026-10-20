# Ticker 捕获拒绝诊断（2026-10-20）

## 修改前四项依据

1. 原 SchedulerCheckpoint 的 Ticker.TryCapture 已严格检查 native ticker/list、并行 scratch、cursor/debt、对象 handle 和重复 handle；失败返回 false，由外层记录 ticker 名称。检查点主体、顺序及拒绝条件继续复用。
2. Native06 两次 Caravan 外层 warning 未含具体 predicate。只读调查发现原生 partition 的 prepublication Add 可能加入同引用，但不是此次失败已证根因；`SourceNoHandle` 也是候选而非确认。本轮仅补诊断适配即可，不修复/重建/去重任何列表，不改 AI、Registry 或原生算法。
3. 原 false、谓词顺序、native 调用次数和每对象 TryGetHandleWithLogging 一次保持。日志只读托管字段、StringId 与 RuntimeHelpers 引用指纹，不调用 Tick、GetName 或 Engine API，不新增全局列表查询。duplicate 诊断最多 12 条，引用样本来自原 foreach 的同一次观察；局部临时引用数组只存活于本次捕获，不进入检查点或持续历史。
4. 跑原有 Scheduler/Tombstone/Ticker 39 项，复核边界仍拒绝，不写日志字符串镜像单测。实际失败 predicate 需总控最终集成版本的新 Native 实证，不能由自动化或原生可能性推断为已修复。

## 验证边界

既有 39 项 Scheduler/Tombstone/Ticker 自动化通过，未修改测试或断言。新增日志包括原 debt 的 Int64 bits；重复输出最多 12 个原循环位置及同次观察引用指纹。引用 hash 仅诊断，不替代原 handle 判重或 ReferenceEquals。没有新增 `TryGetHandle` 查询，也没有回读全局 party 表。原始 Native06 FAIL 保留；Caravan 的实际拒绝条件及其生命周期根因仍待新 Native 日志，不能把此诊断提交当作修复或完整 NPC AI 验收。
