# M05 Mission NPC 位置只读审计（2026-10-19）

结论：M05 原五阶段断言 PASS 保留，但 NPC 位置权威一致性未通过。既有 WORLD_KEYS 不覆盖坐标。至少 4/24 样本 NPC 在最终同一世界时钟下不同。全员 Mission 的稳定暂停窗口也已存在坐标差异，所以不能将观察解释为两个 RPC 恰好采到不同模拟时间。未采集交接激活瞬间与逐消息位置载荷，不能断言发生过某一具体瞬时“回退”，也不能将某一源代码分支直接当作此次唯一根因。

## 版本和原始证据

本树只读 HEAD 7b07de4b79216efbfeab33e87e625388095b6f26，无源码修改、无部署、无游戏操作。原运行 runId c9715d0f5e2b4ce5add01f9b2f1b6a25，A PID 14468，B PID 5544。JS 7b07de4b，C# 5a3bc8f4。原始文件来自根树 `.mcp-local/p2p-mission-r2-05.jsonl`、`.jsonl.summary.json`、`.jsonl.server.log`、`.jsonl.client1.log`。绝对路径、字节数、SHA256 见 input-manifest.json；从原始 JSONL 全量解析 24 条 world_state、全部 authority.state 及相关命令，产物 mission05-npc-position.json。不读取/封存 Active23。

可复现：在本树执行 `node artifacts/review/npc-position/extract.mjs`，然后 `node artifacts/review/npc-position/verify.mjs`。这些是原始证据提取/断言，并非重新启动游戏的验收或产品自动测试。两命令 exit 0。日志保留实际输入行号、callId、请求/返回 UTC、PID、世界 ticks、样本部队和租约状态。这里 UTC +8 为本地游戏日志时间。

## 6042 轨迹（lord_2_4_1_party_1）

| 取样 calls A/B | ticks A/B | A x,y | B x,y | 平面距离 |
|---|---|---|---|---|
| 15/16、23/24、31/32、39/40 | 相同 78699005337596 | 441.471649,512.8438 | 441.471649,512.8438 | 0 |
| 289/290，A Mission / B writer epoch5 | 相同 78699088115742 | 441.231049,512.885559 | 433.608521,514.206 | 7.736052 |
| 323/324，全员 Mission epoch6 | 相同 78699089256620 | 441.231049,512.885559 | 433.513184,514.2225 | 7.832806 |
| 341/342，全员 Mission epoch6 | 相同 78699089256620 | 同上 | 同上 | 7.832806 |
| 412/413，A Mission / B writer epoch7 | 相同 78699151159029 | 441.231049,512.885559 | 429.185333,514.972168 | 12.225106 |
| 458/459，A Map / A writer epoch9 | 相同 78699207782102 | 437.913849,513.4601 | 424.965973,515.703552 | 13.140798 |

该 NPC 每个上述样本 troops111、wounded0，两端 ID/handle 一致。Call323/324 同时请求 14:38:43.109Z，返回 A43.186/B43.168；call341/342 同时请求14:38:49.888Z，返回A49.975/B49.940。请求间隔6.779秒，两个完整暂停快照 NPC 坐标逐项相同，且 authority.state 321/322、339/340 均 epoch6 paused=true、MissionState、hours2186085.8126838887、worldTicks6454。不是仅响应到达差35ms的证据。

最终458/459请求14:39:19.263/19.264Z，返回19.342/19.320Z。此前state456/457均epoch9、LocalA写者、MapState，同hours2186089.105058389。样本4处差异：6042距离13.140798；6050距离3.677719；6087距离8.156676；6305距离11.958737。全部24个样本兵力/伤兵一致。最后7字段哈希仍相等（对应既有断言），不代表位置、完整AI内部状态或全世界其他数据相等。比较的是世界对象 party.Position，不是屏幕像素或视觉分配编号。visual TypeCounters 差异不被当作网络ID冲突。

## 静态已证实的接缝

1. `source/GameInterface/Services/MobileParties/Handlers/MobilePartyBehaviorHandler.cs:71` 复用原AI行为变化与受控玩家预测，当前世界写者发布结果；`:100` 将有Epoch envelope的权威行为消息转换为 `UpdatePartyBehavior(isAuthorityReplay:true)`。
2. 同文件`:129` 只有 ForcePosition 才在接收权威重放时直接应用位置。`:139`附近调用原 `TryApply` 完整目标/导航。`:169-178` 普通位置纠偏限于 `ModInformation.IsClient`，`:266` 又仅适用非自回声且 Hold 或陆海边界切换；移动中同陆地不强行跳回旧包坐标。A为ListenAuthority仍是IsServer，即使暂任观察者也不进入这段IsClient分支。这是具体角色适配缺口，不能简单把server条件全面替换而破坏移动预测。
3. `source/GameInterface/Services/MobileParties/Data/MobilePartyBehaviorSnapshot.cs:272-287` 的常规 TryApply 使用 `ApplyBehavior(resetPath:false)`，`:501-541` 写目标、默认/短期行为、导航模式，但不写当前Position，也不丢弃现路径。加入基线`:488-498`的ApplyJoinState确实写当前Position、Event/ArmyPositionAdder、Bearing、导航过渡字段，`:374-375`还resetPath=true。可复用这些成熟快照字段和准备/解析能力，不能把整个JoinBaseline无条件套到运行中每条移动消息。
4. `source/Coop.Core/Client/Services/Session/CampaignAuthorityFreezeClientHandler.cs:93-130` 排空异步任务、flush coalescer、冻结租约、采集五类检查点、发ACK；`source/Coop.Core/Server/Services/Session/CampaignSimulationAvailabilityServerHandler.cs:696-738`应用冻结ACK，`:548`附近构造候选barrier；`source/Coop.Core/Client/Services/Session/CampaignAuthorityHandoffBarrierClientHandler.cs:92-150`排空后依次应用 allocator/roster/session/scheduler/clock 再ACK。这些检查点不含全体NPC的当前位置与完整导航接管基线。session JSON是会话/玩家身份而非任意NPC世界快照。无证据显示存在隐藏的全体NPC handoff位置修正。
5. `source/Coop.Core/Server/Services/Session/CampaignAuthoritySyncRelayServerHandler.cs:68-113` 与 client同名EnvelopeHandler`:55-98`已有Room/Authority/Controller/Epoch及序号验证和游戏线程重放，不需要重写LiteNetLib或Protobuf。冻结与最终消息屏障应继续使用这些通道。
6. `source/GameInterface/Services/CampaignAuthority/CampaignObserverFrame.cs:16-28`是观察者地图表现链，不代表权威AI在观察者推进。原worldTicks证据仅当前持租约节点增长；本次没有观察到双权威。不能因为目标复制存在就假设NPC当前位置自动一致。
7. `source/GameInterface/Services/CampaignAuthority/CampaignAuthorityDebugCommands.cs:121-164`生成world_state，partyIdentityHash仅 handle/id/hero/clan，npcSample仅前24个active movingNpc；`tools/mcp/scenarios/p2p-udp-reliability.mjs:35` WORLD_KEYS无坐标，`:121`比较它们。`p2p-mission-lifecycle.mjs:275`全员暂停断言只要求各端自身before/after世界不变，没有要求跨端全部NPC坐标相等。原断言不修改，另立位置接管验收。

## 推断与未观察项

推断：A Mission期间保留旧NPC坐标，B持续推进位置；之后A重新获得租约时缺少明确最终位置接管基线，能解释持久差异和A后来从不同轨迹继续移动。它与源码接缝、4条轨迹相容，可信度高；但不能提升为“已记录激活瞬间采用旧位置”这一更强事实。

NOT_OBSERVED：确切epoch8冻结/epoch9激活游戏线程瞬间的6042位置；每条行为delta的捕获tick、ForcePosition、发送/应用位置；6042在交接瞬间的MoveMode/导航path；是否另有包修正后再次漂移；全部NPC而非24样本的完整坐标；战斗结果/真实战斗Mission；该差异直接造成碰撞、经济或伤害错误。原始日志没有针对6042的完整位置包timeline，不能仅由近似方向猜测哪一包遗漏。

## 最小后续适配建议（尚未实施）

优先新增 handoff 专用“最终移动状态基线”适配层，复用既有IMobilePartyBehaviorSnapshot/NetworkPartyBehaviorUpdateData与JoinState字段的解析和游戏线程应用。旧写者排空真实异步移动任务后、最终冻结确认前采集含完整身份与最终tick的全体相关party位置/导航状态；把它与既有Epoch/最终屏障绑定，候选预验证所有handle/目标/依赖完整，冻结中安装位置与必要导航状态，成功后ACK，激活前核对同tick关键位置。不完整/旧Epoch/缺依赖拒绝激活并保留冻结。不要把运行中的每条普通行为包改为强制坐标瞬移，也不要修改原移动、生成器、主Registry或Mission Owner/Puppet。

原实现保护：移动目标复制、玩家最新预测与self-echo、移动中不接受旧位置、ForcePosition显式语义、附属军团定位、同一游戏线程、AllowedThread仅接收应用、无接收回发循环、对象创建/handle分配只一次。未经回归不能把所有玩家接管成强制Hold或清除Mission/Encounter。

新增回归至少：A为观察者/B写者的现有行为目标/位置接缝；双方移动NPC与玩家self-echo；冻结后位置/导航全量基线且不生成新对象；旧Epoch、缺handle、重复基线、丢失最终ACK、延迟消息不提前激活；强制传送/陆海切换/军团附属party原行为；All-away暂停各端静止且跨端同基线；A→B→A在激活前后按冻结tick比较全体关键NPC坐标与导航；真实Mission停留足够时间形成可测位移。容许运行中表现插值的误差预算必须独立定义，不能放宽冻结交接准确状态。

本次只读交付，没有代码提交或修复声明。现有M05五阶段PASS不撤改，但更广的“Mission世界权威位置一致”明确未通过。
补充机器判定：`node artifacts/review/npc-position/accept-paused-npc-position.mjs` 已实际执行 exit1，日志 acceptance-fail.log 明确 FAIL；只比较M05已封存epoch6两个稳定paused/ticks快照，每次4/24位置不等。这是补充真实状态验收失败，不列为成功的产品测试。`verify.mjs` exit0只证明只读调查结论可复现及原证据SHA不变。当前未修复，不得给Mission父任务签完整一致性PASS。
