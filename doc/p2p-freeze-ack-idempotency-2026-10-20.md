# Freeze ACK 精确请求幂等（2026-10-20）

## 修改前四项依据

1. 原处理器完成异步清空、导航准备、旧写入冻结与可靠有序 flush，再捕获完整 allocation/roster/session/scheduler/clock/movement ACK；相同已冻结 lease/seq 的重复请求仍再次捕获并发送全量 ACK。旧测试明确要求两次 SendAll。
2. 独立旧 UDP run `3341b17a260d47aaba51e52a29803092` 证实同 handoff2/lease5/hash 的 18 次完整 ACK 发送，A 首次接受后仍长时间收到旧 ACK。会话层精确请求幂等可消除这一放大，无需重写 LiteNetLib、可靠有序、Protobuf、队列或移动算法；后续总控 480 原逻辑实机 run `42333e646e774453b64d2a6e0f5f1dcb` 闭合了该链：B 的同一 epoch6/hash 前缀 `085e237` Baseline ACK 于 17:14:18 原 SendAll/Flush 返回，payload peer ordered queue 2694→2695；A 于 17:14:41 ingress/queued/游戏线程/accepted，排队至到达约 23 秒。应用层重复大 ACK 与 FIFO 积压有实证，仍不改运输算法。
3. 新不变量是一个会话身份内同完整 lease/seq/candidate 最多尝试一次完整 ACK 入队；首 ACK 的完整冻结/检查点/flush 顺序不变。SendAll 为 void，逐 peer 发送且无原子成功/交付回执；抛错可能已部分排队，Flush 也非交付凭据。因此进入 SendAll 前记 attempted，任何发送阶段不确定均不重新排队并保持冻结，不能冒充无损恢复。未进入 SendAll 的 capture/前置 flush 失败可在同一精确请求下重试。
4. 原两次发送断言改为更强 ExactlyOnce，保留首次 ACK 内容/顺序校验；新增 18 次重复、身份/任期/seq/candidate、取消 tombstone、同会话 handler 重连、capture 失败、部分发送/Flush 异常和过期 ACK 反例。自动化后由总控用新诊断实机验证；本 worker 不游戏/部署。

## 生命周期

单条 ledger 以实际 ISessionIdentity 引用弱绑定，跨同一会话 handler 重建保留；新 confirmed lease 才切换记录。精确取消仍须当前有效 lease/本地 controller/匹配 candidate 和 seq，解除原 fence 后保留已取消序号，旧请求不可重新冻结；更高序号只在旧 fence 已解除后开始。新进程/替换 SessionIdentity 的无损恢复不在本补丁内，不能当作已实现。

普通 handler 实例字段会在同一 ISessionIdentity 的重连处理器重建时丢失 attempted 标记，重排可能已经部分发送的全量 ACK；弱绑定 ledger 仅保存一条小型请求状态，不缓存检查点、不引入通用管理器。取消仍由原 `TryCancelLocalAuthorityFreeze` 校验当前 lease 与精确冻结序号；适配层额外拒绝同序号不同 candidate，成功取消后才记 tombstone，不绕过 SessionIdentity 解除冻结。

## 本轮验证及剩余边界

原路径 RED 测试证实 18 个精确相同请求得到 18 个完整 ACK；补丁后首 ACK 的内容、冻结与 flush 顺序断言保留，重复发送改为更强 ExactlyOnce。关键 Freeze/FinalMovement 46/46、相关 Session 106/106 通过。不确定 SendAll（含部分排队）或最终 Flush 异常后，重复 18 次和 handler 重建均不重发；保持安全冻结，自动无损接管/重连恢复仍未实现。capture/前置 flush 失败尚无 ACK 尝试时可以重试，原有效 cancel 才解冻并留下序号 tombstone。

上述 480 诊断实机完成首轮 A→B→A（epochs3/5/7），但整体仍因 129 条 `IMissionMembershipRegistry` ERR 为 FAIL。本补丁没有把该运行改记 PASS，未修该异常，也没有执行新补丁双进程验收；最终证据由总控另行归档。
