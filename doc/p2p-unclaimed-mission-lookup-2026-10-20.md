# 未声明 MapEvent 的 Mission 查询短路（2026-10-20）

## 修改前四项依据

1. 原 `ServerBattleModeArbiter` 已仲裁 Mission/Simulation 声明，`MapEventPatches.HasOccupiedBattleMission` 只应在事件有 Mission 声明且真实成员表判为 occupied 时阻止慢袭村更新。这些成熟仲裁和成员语义保持。
2. UDP01 的 B 135 条及 UDP02 的 B 129 条 ERR 暴露远端动态 writer 在 `MapEvent.PrefixUpdate` 的慢袭村分支中，未声明事件也先解析只在 ServerModule 注册的 `IMissionMembershipRegistry`。`Encounter.IsAwaiting…` 的 IsClient 提前返回不能保护这一实际 caller。对原布尔表达式做短路适配已足够，不更改 Mission NPC、MissionManager、AI、会话身份或 Registry 主体。
3. 保持对象注册 ID 必须先可用、claimed 事件依旧解析真实成员表并调用原 occupied 判断；缺依赖原日志和真实解析异常不可吞。unclaimed/simulation 事件先返回 false，避免无意义依赖解析。不注册假空成员表，也不声称补齐远端 occupied Mission 或跨进程全局状态。
4. 使用实际 Autofac container、可计数/抛错成员表工厂和原私有 helper 的反射调用，验证 unclaimed 零解析、claimed occupied/empty 原结果、缺 ID 提前拒绝及 claimed 真缺依赖/工厂异常原样保留。旧 ERR 原样留档，只有总控新双进程 0 ERR 复验后才可验收此范围。

claimed Mission 的全局远端成员状态仍未实现；远端 writer 如进入该路径，缺真实服务仍须明确失败/报错。本补丁仅消除未声明 NPC MapEvent 的无效查询，不是新战斗玩法支持。

## 自动化及验收状态

原实现的 unclaimed/simulation 两例 RED 均实际进入并触发不应运行的 Autofac 成员表工厂。仅重排布尔短路后，新增 7 项、仲裁/角色相关 29 项、MapEvents 全相关 197 项通过；没有改动原测试或放宽断言。claimed 缺成员表仍返回原 false 并输出缺依赖日志，真实工厂异常仍原引用穿透；不以默认空数据静音。

以上仅自动化，不代替新版本双进程 0 ERR 验收。此前 UDP01/02 原始 FAIL 与全部错误不修改、不追认为历史允许项；claimed/global 远端 Mission 占用仍为未解决边界。
