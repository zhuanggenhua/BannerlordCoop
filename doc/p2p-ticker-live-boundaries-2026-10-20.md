# 原生 ticker 回调边界诊断（2026-10-20）

仅适配 DEBUG 诊断，不修改原生 ticker、其 `_action`、AI、Registry、移动或 QA 周期断言。Native04 原始 FAIL 保留；Hero 生命周期的具体触发原因未证实，不能先将旧失配全部认定误报。

## 修改前四项依据

1. 原诊断已保留原始完整有序列表、Index、TickDebt、严格静态预测、完成序列和首失败。闭合泛型 ticker 本体不打补丁，由五个 caller 的 20 个原调用点观察。
2. 已有原生栈证明 Party 删除发生于 `Deserters.MergeParties → HourlyTickParty → PeriodicTicker`；Hero 也有确切选取序列与列表变化，但死亡原因未证实。需要完整观察回调入口/出口后才可独立预测下一选择，无需重写原生算法。另真实 CLR4 parent-first 安装漏 quarter，class-end 用原 owner 重建 parent 的 identity transpiler 已有独立夹具证据，无需改 Installer。
3. 保护初始 count 一次加债、每个成功返回回调一次减债、引用级对象与有序 ID 选择、完整列表变化、游标及精确 debt；保留手工 Begin/Completed 静态拒绝测试。仅完整回调内部变化可进入下一预测，回调外、缺证、漏重乱序、异常、溢出仍失败。passing 变化也保留有界首个完整证据，原异常引用不变。
4. 测试实际原生 mutable-list 的删除/添加/wrap/多轮；同 ID 不同引用、未观测变动、游标/debt、缺重乱序、空表/嵌套、异常原引用；真实 CLR4 验证 production callback 补丁和 parent-first 20 记录/原调用次数。所有编译用 `-p:ModName=`；本任务不部署或运行游戏，后续实机才决定诊断修复是否有效。

## 实现边界

`BeginNativeBatch` 才启用 live 选择审计。每次 production callback Prefix 从上次完整 post 列表独立推进 Index/wrap，核对实际参数引用、StringId、原生 Index 和尚未递减的 debt；Finalizer 读取完整 post 列表，成功返回才预测原生随后的一次减债。初始 count 决定的总回调次数和最终 debt 仍以原 `Expected` 结果独立核对，不能用已观测回调自己证明完整。原异常从 Finalizer 原引用返回，原 wrapper 裸 `throw`。

仅完整观测到的 callback 内部 membership 变化能用于下一选择。回调外修改、同 ID 错引用、缺失/多余/乱序、未完成边界、跳过原回调、未消费的 due debt、游标/debt 错误、异常和证据超限均拒绝。没有真实合法实例支持豁免的同 ticker 递归回调仍明确 incomplete；嵌套真实 ticker 使用独立 batch 并恢复外层。

存储有界：每 batch 保留初始及最近完整列表、当前回调 before/after，以及最多 32768 个独立选择/完成记录；不累存每次回调的整张历史列表。membership 变化累计数量和滚动 hash，保留首个完整变化证据，包括有序 ID、进程内引用指纹、回调/时刻/lease/Index/debt。指纹仅用于解释证据，主谓词仍是 `ReferenceEquals`。passing 变化也保留该首证据；后续失败仍保留完整失败向量。

class-end `[HarmonyCleanup]` 只在 `original == null && exception == null` 使用注入的实际 Harmony owner 对 `TickPeriodicEvents` 安装 identity transpiler，重建 CLR4 父调用点。五个原 group、20 个 typed 调用点、parent 原始 IL 及既有资格 Prefix/异常链不改；不依赖 Harmony 内部 Dictionary 的 target 顺序。

## 验证与保留问题

原静态手工 Begin/Completed 的拒绝测试保留。新增选择测试执行真实 `PeriodicTickSome`，但明确通过托管 callback 边界 seam 测预测器；独立 CLR4 夹具另外执行真正原生 Dispatcher 的 production Prefix/Finalizer，并对照旧版 RED、新候选及 parent-first/after/真实资格补丁共存。最终提交、实际命令、日志 SHA、候选 DLL SHA 和各轮结果以交接回执为准，初始测试编译错误也保留原日志。

Native04 的日 Hero 具体死亡/移除因果仍未证实；不能凭夹具合法列表变化追认旧 FAIL。DEBUG 每回调完整取证有成本，本次未作实机性能或完整自然日/AI/工资/迁移验收，不改变相关门槛。
