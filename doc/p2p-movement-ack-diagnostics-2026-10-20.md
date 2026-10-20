# 最终移动基线 ACK 诊断（2026-10-20）

## 修改前四项依据

1. 原 `AuthorityLeaseSnapshotHandler` 安装最终移动基线、确认 paused lease，然后通过原 `SendAll` 与 `FlushPendingMessages` 发 ACK；房主处理器把 ACK 排入游戏线程，并严格核对真实 NetPeer、同步状态、Controller、room、epoch、ticks 和 SHA256，再解除成员屏障。原协议及可靠传输继续复用。
2. 7d 实机 UDP run `3341b17a260d47aaba51e52a29803092` 中 paused epoch 6 已在 B 安装，但未观测 A 的成员确认，首个缺证区间位于 B 的发送/flush 至 A 入站/游戏线程/guard 之间。现有日志不能证明包已交付，也不能把未知原因归咎网络。补 handler 诊断即可，不改 LiteNetLib、序列化、消息合同或成熟移动算法。
3. 保留每个原短路谓词、执行顺序、唯一 peer 引用与 epoch/hash/ticks 门禁，保留原 SendAll/Flush 次数及异常传播。不新增重试、等待或超时，不以日志的 send-returned/flush-returned 冒充交付或应用成功。
4. 原 Protocol/Acknowledgement 回归保持；补真实处理器路径的发送/flush 故障、调用顺序与次数、错误成员/基线和同编号不同引用拒绝反例。测试检查实际消息、身份和副作用，不写日志字符串镜像断言。最终原因需总控双进程复验，本补丁不宣称修复此次交接失败。

## 观测含义

客户端记录 snapshot ingress、enqueue 返回、游戏线程、原 send-call/send-returned/flush-returned 和未满足的可选依赖。所有发送返回仍为 delivery-unconfirmed；快照 sender 的 NetPeer 队列只是该对象的 outbound channel-0 ReliableOrdered 包数，并非实际 SendAll 所有接收者或交付凭据。复用既有 `DescribeRoomPeers` 区分 handler/network/transport scope；读取失败显式记录异常与 Unavailable，不使诊断阻止原应用或发送。

服务器记录 ACK ingress、enqueue 返回、游戏线程、原短路链第一个拒绝原因或 accepted，附 incoming/pending/current lease、ticks/hash、expected/current peer 引用。`queued` 只表示 EnqueueSafe 返回，可能与游戏线程日志交错；不代表已应用或网络交付。没有协议命令号，重复 ACK 依靠原相同 room/epoch/hash/ticks/controller/真实 peer 门禁判定，诊断引用 hash 也不替代 ReferenceEquals。

旧 run `3341b17a260d47aaba51e52a29803092` 的 FAIL 原样保留。此前同一 B 收到多次 freeze request 并发出完整 FreezeACK，说明存在 channel-0 队列阻塞的证据，但最终成员 ACK 的首未观察区间仍待新实机追踪；不能由 send-returned、房主队列为空或一次旧 ACK 成功推断此次已交付。

## 自动化与限制

原 Protocol/Acknowledgement 15 项均通过；新增 15 项实际消息/状态/调用顺序反例后 30/30，通过相关 Session 回归共 91/91。没有改断言阈值或删除原测试，也没有为重复 freeze request 增加去重（该行为需单独证据与授权范围）。仅此候选的自动化通过，不代表原 UDP 失败已修复，更不代替最终双进程故障复验。
