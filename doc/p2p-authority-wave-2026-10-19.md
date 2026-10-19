# 2026-10-19 P2P 战役权威改造进展

本轮实测通过了观察端重连后的 A→B→A→B、大地图旧权威失联后的安全停住，以及原生地图/Mission 的暂停显示和返回恢复。普通 NPC 原生日训练回调及经验接管已通过独立实测；随后在限定原生 QA 入城场景完成了四次招募及双端严格状态对比。完整 P1-B 仍有未解决项。不能把这些结果称为全部 P2P 玩法完成或测试全绿。

源码范围为 `a6d1ebcc19d9370ab156f6aa8a729e7a447f7aa3..0f678f94b0750916541c321f1f2230e43067a512`，文档提交另列于 Git 记录。下表分别标明各次真实构建：故障/UI 使用 `a62d7cd9`，NPC 独立轮使用 `5d827e9b`，最终招募轮使用 `0f678f94`；不能将旧轮次冒称为最终 HEAD 的重新验收。A 是房间连接/转发节点，也保留本地玩家；战役写入权可以属于 B。工具中的 `server` 是 A 的单个 ListenHost 游戏进程，`client1` 是 B，没有额外 Dedicated Server 或 A 的本地回环客户端。

| 已通过场景 | 构建、run、双进程 PID（A / B） | 实测范围 |
| --- | --- | --- |
| 重连、交接、旧权威失联 | `a62d7cd9`，`d8ddbb344ebd4670aa192bde76a1b64f`，27816 / 17616 | B 作为观察端断线重连后，再完成 A epoch 3 → B 5 → A 7 → B 9；B 作为写入端失联后 A 不擅自接棒。[证据](../artifacts/p2p-authority/wave-2026-10-19/fault-accepted.json) |
| 原生地图及双 Mission 暂停/恢复 | `a62d7cd9`，`0f863e7a383a450d92a22f7a8f75163c`，23580 / 4336 | 地图暂停显示、A 进 Mission/B 推进、两人都进 Mission 暂停、B 先回地图恢复、A 返回后优先接棒。[证据](../artifacts/p2p-authority/wave-2026-10-19/ui-accepted-final.json) |
| 普通 NPC 训练及经验接管 | `5d827e9b`，`f6166a9d789d44089cd9b95522a9f998`，35568 / 6684 | 9 个阶段：A/B 各自唯一世界 Tick、A→B→A、B/A 各一次原生日训练、错误 writer 拒绝、经验/人数/编号连续性、两端关键哈希和精确暂停时钟一致。[证据](../artifacts/p2p-authority/wave-2026-10-19/npc-accepted.json) |
| 四次原生招募及状态接管 | `0f678f94`，`06505b46b82940bebb836c41854708df`，8548 / 280 | 13 个阶段；四种购买归属、A→B→A、NPC 经验、编号、精确时钟与16项玩家字段一致。限定 QA 入城准备。[证据](../artifacts/p2p-authority/wave-2026-10-19/recruit-training-accepted.json) |

旧权威失联场景曾失败：A 的世界 Tick 已停止，观察时钟仍继续外推。修复把“等待旧权威最终检查点”单独投射为本地停表信号，保留原 lease 和 ACK 屏障，不通过超时或断线伪造交接成功。最终两次取样 UTC `15:50:36.487` 与 `15:50:41.498` 相隔 **5.011 秒**，A 的 `worldTicks=7413`、世界小时 `2186093.878769889`、B writer epoch 9 均完全不变，`dt=0`。UI 保留选定的 `Play_1x`，有效显示为 `Pause`。这是安全停住，不代表已实现 writer-loss 恢复。[原失败](../artifacts/p2p-authority/wave-2026-10-19/writer-loss-observer-clock-failed.json)

暂停时钟还曾出现两端各自停住却不相等：run `5fcc975280d84d558156883a29ca0171` 的原生 ticks 为 A `78699051568698`、B `78699055449732`。平滑纠正依赖正的地图 delta，在 Stop 下无法收敛；现在暂停心跳在原有 lease 校验内精确应用，早于加入基线的样本不能倒退时钟。最终 UI 场景暂停取样的两端 ticks 都为 **78699276075732**，`partyIdentityHash`、`rosterCountsHash`、`rosterXpHash`、`volunteerHash`、`heroGoldHash` 和 `nextHandle` 六项均无差异，`nextHandle=45298`。[原失败](../artifacts/p2p-authority/wave-2026-10-19/recruit-training-clock-failed.json)

UI 场景使用真实 `MissionState`。两人都离开地图后，在 epoch 12 的两次五秒取样中，两端 `worldTicks` 都保持 **5820**，世界小时都保持 **2186086.7146859444**；两个 Mission HUD 都显示 `Map Time: Paused`。B 返回地图后在 epoch 13 推进至 Tick 6218，A 仍在 Mission 且停在 5820；A 返回后在 epoch 15 恢复优先权。地图暂停时原生时间栏 `mapTimeFlowState=0`、`mapPaused=true`；Mission 内以实际覆盖层文本判断，不把已隐藏的地图栏缓存当作 Mission UI。选定速度未被暂停显示覆盖。

上述两轮中 A 始终保留 `main_hero` / `player_party`，B 保留 `Player` / `Player`。故障轮重连后抽检两人的英雄、家族、部队注册各为一份。两轮的双方日志中 RealTick failure、重复对象编号、AI batch failure 三项计数都为零；这不表示所有历史日志问题均不存在。每轮均执行 `stop_run`，双方 `cleanupComplete=true`、`processAlive=false`、`processTreeAlive=false`。截图、日志 SHA256 和原始命令索引保留在对应证据中。操作通过原生语义调试命令驱动，不替代物理鼠标输入验收。

本轮主要修改集中在五个模块，以下是便于继续开发的入口；完整范围另见 [文件清单](../artifacts/p2p-authority/wave-2026-10-19/changed-files.json)。

| 模块 | 文件入口 | 本轮修复 |
| --- | --- | --- |
| 权威等待、世界时钟与原生 UI | `CampaignSimulationAvailabilityServerHandler.cs`；`Services/Time/Interaces/MapTimeTrackerInterface.cs`；`CampaignTimeDisplay.cs`、`MapTimeDisplayPatches.cs`、`MissionMapTimeVM.cs`、`MissionMapTimeView.cs`；`PlayerLocationEntryPatches.cs` | 等待最终检查点时冻结观察时钟；合法提交或取消才解除，单独应用 handoff 基线不能解除；Join/Dispose 清理会话残留；地图和 Mission 显示同一有效暂停，保留原生输入和选定速度；ListenHost 本地 Mission 补挂时间覆盖层。 |
| 重连后的原生周期调度 | `Common/CampaignSchedulerCheckpoint.cs`；`GameInterface/Services/CampaignAuthority/CampaignSchedulerCheckpointService.cs` | 原生加载重建 Army timer 的 InitialWait 曾导致合法交接被拒。完整校验 callback 所属对象、方法与顺序后恢复源 timer phase；无法解析或身份不匹配仍拒绝，未放松最终屏障。[失败证据](../artifacts/p2p-authority/wave-2026-10-19/reconnect-periodic-event-rejection-failed.json) |
| 招募操作和共享库存 | `RecruitmentVMPatches.cs`、`RecruitmentAttempted.cs`；`ServerTroopRosterHandler.cs`、`ClientTroopRosterHandler.cs`；`RecruitmentDebugCommands.cs` | 以当前 campaign writer 路由。权威本地原生操作执行一次后发布绝对库存结果；观察端请求经身份、归属、任期及序号校验，避免回放同一次购买；检查真实库存、招募权限和预算。 |
| NPC 训练 | `GameInterface/Services/MobileParties/Patches/Disable/DisableMobilePartyTrainingBehavior.cs` | 潜在接棒者各保留一次原生事件注册，Hourly/Daily/升级回调只允许当前未冻结 writer 执行；增加调用一次原生 `OnDailyTickParty` 的调试入口。注册和门禁已有自动化覆盖，自然日周期与实机结果另述。 |
| 故障与原生玩法验收工具 | `tools/mcp/scenarios/p2p-recruit-training.mjs`；`tools/coordination/orchestrator.mjs` 及对应测试 | 增加真实招募/训练流程、MCP 启停响应检查和逐功能证据；只对托管工作树移除命令启用 `git -c core.longpaths=true`，不改全局配置。 |

历史招募 run02 是**部分通过**。run02 `62c18ddb7535452288a669c3367edcb3` 共通过六个 feature，其中 A 为 writer 时原生购买一次（20 金币、兵力 1→2），A 为观察端时向 B 请求购买一次；观察端提交时本地金币/兵力不先行变化，最终断言检查两端结果。随后 B 自己购买前找不到真实可负担、可招募槽位，测试停止，没有伪造库存或补钱。在该轮中，B 自己购买、返回 A 后购买及后续 NPC 阶段均未覆盖，后续 run08 结果另述。[部分结果](../artifacts/p2p-authority/wave-2026-10-19/recruit-stock-precondition-partial.json)

`f6f2690c` 的目录查询改用现存库存选择真实城镇。run03 `74fb3eb53bb54f7d8fdf080319b677f9` 选中 `town_A1` 的 **10 个**双方可招槽位，未再被原库存条件阻塞；但 B 作为观察端入城时原生 Mission 未退出，随后 `authorityPartySettlement=null`，入城前置条件未收敛，只完成前两个 feature。

后续脚本修正 `968fb76` 已集成为 `d74ea6c5997a41bbb4b70ed69cc404975bb3d03d`，未改变本次部署的 `f6f2690c` Mod 源码构建。该脚本的 run04 `7b6b073bf6944d69bda61473d3edfb16` 再次找到相同十个槽位，但 `location.leave` 后 B 仍有原生 Mission，精确的 MapState 检查失败；仍仅通过前两个 feature，已确认清理。在 run04 当时，B writer 自己招募等后续路径仍未覆盖；run08 的最终结果另述。商店货物允许本地差异，不因此放宽共享兵种库存、扣款、兵力和经验的一致性要求。

NPC 独立 run01 `af8351dc69914977a42711aaeb850da6` 未通过，已确认清理。B 接棒后，A 的手动训练命令正确被拒绝，B 的原生回调执行；随后读取的双方实际兵种 XP 一致，例如 `sturgian_recruit` 从 242 增至 565，但调试回执的 `after` 仍为 242，脚本据此报错。定位为 `CaptureXp` 使用 `GetTroopRoster` 缓存，而原生 `SetElementXp` 不更新该缓存版本；最小修复 `5d827e9b` 改为逐项读取当前数据；先红后绿用例证明原生实际 XP128、缓存100，读取后仍不修改原生缓存或版本。随后独立 run02 通过全部9个阶段，B、返回的A各一次回调的非英雄兵种XP均正增长，人数不变，并同步到观察端；最终7项关键世界数据（含精确ticks）一致，三个分配探针handle和序号递增，角色与队伍未切换，双方英雄/家族/部队注册数各为1。不能把最初的回执缓存错误当作同步丢失。`coop.debug.authority.training_daily` 只手动执行一次原生 `OnDailyTickParty`，不能证明自然跨日调度已通过；普通 NPC 兵种经验变化也不能证明所有玩家 Clan 的训练/经济模型已经公平。

早期 `a62d7cd9` 的自动化结果记录于 [final-regression.json](../artifacts/p2p-authority/wave-2026-10-19/final-regression.json)，最终 `0f678f94` 的结果见下文 latest-regression.json。文件的 `passed=true` 表示新增失败对比检查通过，**不表示各测试集全部通过**。

| 检查 | `a62d7cd9` 实际结果 | 结论 |
| --- | --- | --- |
| GameInterface 全量 | 2811 通过 / 0 失败 | 本轮新增测试通过。 |
| Common 全量 | 423 / 0 | 通过。 |
| Coop 全量 | 1370 / 195 | 原始退出码 1；与独立基线的失败名称逐项相同，新增/消失失败均为零，195 项仍未修复。 |
| E2E 指定子集 | 60 / 10，共 70 | 原始退出码 1；失败名称与基线逐项相同；不是全部 E2E。 |
| 主 Mod Debug | 构建退出码 0 | 构建成功不替代实机验收。 |

`f6f2690c` 在增加招募库存目录和脚本选择后，再次运行 GameInterface **2811/0**，主 Mod Debug **0 错误、8 警告**。`5d827e9b` 的经验回执修复新增1项回归测试，worker实际GI全量 **2812/0**，root集成复跑训练类 **15/0** 并构建退出0，再部署完成上述NPC02实测。构建均显式使用空 `-p:ModName=` 禁止构建钩子自动部署；真正部署和完整双进程场景由总控持有游戏独占锁执行。一次完整 GI 曾有 Mission diagnostics 测试受其他测试清理全局 BattleSpawnGate 干扰，已用跨线程探针确定性复现，最终只给该测试类增加已有禁并行 Collection，原断言及 Battle 生产逻辑未改，红灯证据仍保留。

最新原生招募验收 run08 `06505b46b82940bebb836c41854708df` 使用最终 Mod 源码 `0f678f94b0750916541c321f1f2230e43067a512`，验收脚本 HEAD 为 `91826a0d487d5f5b052254553d2587a78cb07303`，两个游戏 PID 为 8548 / 280，全部 **13 个阶段通过**。A writer、A observer→B、B writer、返回后的 A writer 共提交4次真实 RecruitmentVM 购买，变更命令没有重试；每次两端库存槽位、扣款和兵力只变化一次，最终金币 LocalA=940，testclient1=900。B 通过本地原生菜单 consequence 离城并发布世界结果，双方确认离城后 B 单独推进世界，再交回 A。最终精确时钟和6项世界哈希/编号一致，4行原生玩家状态按 Controller 对照16项关键字段（含 x/y、城镇、金币、人数、身份及注册数）完全相同，位置没有容差；商店 items 不纳入关键字段。[实机证据](../artifacts/p2p-authority/wave-2026-10-19/recruit-training-accepted.json)

此前 run05 `8731675bd5f74eab8d6042e57ba98f66` 已通过 B writer 入城及真实 Mission 返回，但在3阶段后双方玩家队伍随交接自动离城，招募前因此没有当前城镇。位置确已是 town_A1 gate(300.6186,261.9282)，距离假说被实测反证。原版调用链为 Campaign.Tick → CampaignTickCacheDataStore.Tick → MobileParty.CheckExitingSettlementParallel → LeaveSettlementAction；原生候选检查只豁免本进程 MainParty。最小补丁将该豁免扩展到稳定注册玩家（含断线保留角色），不拦直接离城或NPC；真实原生红测4失败、绿测8/8。A旧GoToPoint已采样，B本地Hold不能证明对端副本也Hold，此历史细节仍未观测。[调查与边界](../artifacts/p2p-authority/wave-2026-10-19/native-player-settlement-investigation.json)

run06 的4次购买已实际扣款并同步，但第4次 afterpoll 将城外 B 的当前城镇列表 null 当作目标城镇库存而报错；该轮整体未通过，也没有第4次后的全局库存哈希。最终脚本只检查仍在目标城镇的原生UI库存，并严格保留双端全世界 volunteerHash 校验及玩家城镇不变断言，城外观察端以全局哈希证明同一兵源状态，不要求其打开目标城镇UI。

本地 writer 离城还修正了旧 Listen 身份分流：A/B 当前未冻结 writer 执行原生 consequence 一次，暂停、冻结 owner 和 A observer 不误发请求。DEBUG B writer 明确执行原生动作并检查 CurrentSettlement 已清空，不用 AllowedThread 绕开结果发布。原生门禁红测6失败，修复后13/13；参数未读取有安装版本原生139字节IL证据。完整远端 observer 离城协议的 Epoch、序号、正确 writer 路由尚未修复，A observer 离城按钮当前被安全阻挡，不能将该场景通过称为所有入离城玩法完成。

最终 `0f678f94` 回归：GameInterface **2861/0**、Common **423/0**，Coop **1370/195**、指定 E2E **60/10**，两个失败集与最初基线逐项同名，新增/消失失败均0；Coop/E2E原始退出码仍1。Mod Debug构建退出0。[最终回归](../artifacts/p2p-authority/wave-2026-10-19/latest-regression.json)、[逐项失败名](../artifacts/p2p-authority/wave-2026-10-19/latest-baseline-failure-names.json)。四套检查与全部实机场景均由总控持独占门禁执行，功能工作树独立，子代理继承总控模型并已结束。

本轮未验收完整远端离城协议、完整玩家导航意图与随机数状态交接、自然跨日调度、冷存档/重启恢复、writer-loss 后恢复、物理传输丢包注入、场景 NPC/战斗同步及千人战场。延迟、重复或错误 ACK 的自动化覆盖不等于真实网络故障注入。当前实测没有发现阻止这些**已列明场景**的根本引擎障碍，尚未执行的玩法仍属未验证。

本机原始证据位于 `.git/codex-coordination/evidence/p2p-wave-20261018/`，原始故障/UI 命令记录分别是 `.mcp-local/p2p-fault-run-03.jsonl`、`.mcp-local/p2p-ui-run-03.jsonl`，原生日志和 BMP 位于 `.mcp-local/runs/` 下对应 runId。招募原始记录是 `.mcp-local/p2p-recruit-training-run-02.jsonl`、`p2p-recruit-training-run-03.jsonl`、`p2p-recruit-training-run-04.jsonl` 及各自 summary；NPC01/02 是 `.mcp-local/p2p-npc-run-01.jsonl`、`p2p-npc-run-02.jsonl` 及 summary。最终招募记录为 `.mcp-local/p2p-recruit-training-run-08.jsonl` 及 summary。所有失败保留于[失败实机摘要](../artifacts/p2p-authority/wave-2026-10-19/failed-live-cases.json)。原始 BMP、JSONL 和 TRX 不提交，只提交可移植摘要及哈希。旧 cold-audit 工作树残留的递归删除被工具政策拒绝后仍保留 `cleanup_required` 登记，没有换手段绕过。

run08 的 RealTick、重复编号和 AI batch failure 三项计数为0，但不是零错误日志：A 仍有 Clan 编号查询诊断758条、NPC MapEvent 的 TroopUpgradeTracker 诊断11条，与先前 NPC02 相同；B 的 ItemRoster 写入诊断120条，NPC02为110条。这些未修复，日志计数不等于已证明无害，也不把商店数据降级解释为可忽略全部物品写入错误。[剩余日志](../artifacts/p2p-authority/wave-2026-10-19/remaining-log-diagnostics.json)

run07 因会话中断，没有完整结束回执，也没有执行招募提交，不计 PASS 或产品失败。恢复时确认原验证 owner 和两个游戏进程都不存在，再用现有恢复 API 释放旧 lease/游戏锁；首次 run08 因旧锁拒绝启动，未运行游戏，恢复后才启动本次有效 run08。[中断记录](../artifacts/p2p-authority/wave-2026-10-19/interrupted-run07.json)

总控实际查看了 run08 最后两张原生 BMP：A 仍在城镇菜单，显示暂停、940金币和4人；B 在地图，显示暂停、900金币和6人。两帧未见明显地图拖影；这是两帧的视觉抽检，不能证明长时间运行无渲染异常。[截图与人工观察](../artifacts/p2p-authority/wave-2026-10-19/final-visual-review.json)。本轮9个功能工作树已验证、关闭和清理；唯一保留的旧审计残留仍登记为 cleanup_required。[收口记录](../artifacts/p2p-authority/wave-2026-10-19/final-task-closure.json)

更新任务看板后的编排自检：25项检查、69项自动测试通过，Codex CLI只读版本探针正常；模型策略仍为 inherit-parent、并发上限3，未启动独立模型推理。[编排自检](../artifacts/p2p-authority/wave-2026-10-19/orchestration-self-check.json)
