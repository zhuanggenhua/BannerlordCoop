# BannerlordCoop P2P 可靠性收口报告（2026-10-19）

14次原生运行均已结束并留证。总控审查、集成并执行最终验收；本报告随最后的文档和证据提交保存。**整体P2P未完成，Mission完整一致性验收FAIL/needs_fix。**

目前未达到整个 P2P 完成标准。QA 分页归档修复及离线回归已有证据；自然完整跨日、完整 NPC 周期、异常失联无损恢复仍未通过。Mission05原五阶段断言通过，但真实NPC坐标差异未解决，不能标完整Mission世界一致验收完成。

## 版本与证据边界

- 用户基线：`78ac921691bb74838c42a90dc4edff08db726fb9`。
- 当前代码集成：`7b07de4b79216efbfeab33e87e625388095b6f26`。
- 已编译、reg25 验证的游戏源码：`5a3bc8f4cc032171da8577411b605dff607f02fc`；后续 `7b07de4b` 仅改 QA JavaScript/测试/文档，游戏仍运行 `5a3bc8f4` 的 DLL。
- 本报告及证据的最后提交仅归档结果；该提交 SHA 见 Git history，不能将其当作新的游戏运行版本。
- 本次已入 Git：发布摘要、compact Markdown索引、input-manifest、精确失败名单、补充NPC审计及报告。完整结构化compact JSON约2.8MB，原样保全本机，未放入Git；发布摘要明确链接原文件及SHA。原始大 JSONL、完整游戏日志、BMP 保留本机证据目录并用 SHA 关联，不复制进 Git。
- snapshot08 已审计14次结束运行、127张BMP、277个原始输入；原snapshot07的267输入SHA/字节数不变，前序snapshot04/05/06/07均保留。独立审计及NPC补充FAIL另有小型补充manifest。

## 本轮四项收口状态

| 目标 | 当前真实结论 |
|---|---|
| QA归档与诊断可靠性 | 限定PASS：分页/并发归档修复、原档字节与SHA/PID关联、Sale29/30四次账本复验；内部late-review保全事故登记，后续整树ignored保全已补齐。 |
| 自然24h与完整NPC AI周期 | FAIL/未完成：Natural04仍Army3+PartyBase1真实ERR，A只15.367h，B自然日与完整周期/实际工资支付未完成。 |
| Mission切换/暂停恢复及完整关键状态 | 原五阶段酒馆断言PASS；额外真实NPC坐标验收FAIL，暂停同ticks持续差异，Mission父任务needs_fix。 |
| 异常失联与无损恢复 | Active23安全冻结/拒绝过期重连限定PASS；Candidate24已提交暂停检查点后新candidate屏障故障/重试限定PASS。活动旧writer最终状态丢失的无损恢复NOT_IMPLEMENTED；最终协议包重复注入NOT_RUN。 |

NPC位置是关键权威数据，不能套用用户允许的本地商店物品差异。当前没有证据表明单进程架构遭遇不可绕过的引擎限制；已经确认的是位置接管/退役生命周期/恢复状态覆盖不足，修复仍须优先成熟适配层。

## 实际代码改动

游戏行为修改限定为两处适配：

1. `BannerCampaignBehaviorInterface.OnCollectLootItems` 先检查 `winnerParty.IsMobile`，合法静态驻军获胜时不再把空 MobileParty 传给玩家判定。其余奖励、RNG、物品主体保持原样。原生 Natural04 中旧 null-MobileParty ERR 未再出现，但仍有 Army3/PartyBase1 ERR，故整个场景仍 FAIL。
2. 新增 `MissionHeroHealthNoopPatch`，只在真实已初始化游戏线程、当前 Mission、远端副本本地 MainHero、已确认非本地权威租约、原始写入未获许可及既有健康归属成立时，跳过**精确同整数** HP 赋值。原版1.4.8的该分支在第一段 IL 直接 ret；不写值、不改权限、不发请求、不放开真实 HP 改变。先校验 Campaign/Game/PlayerTroop，避免危险 MainHero getter；实际生成 ref prefix、原请求、诊断 finalizer 的顺序已离线测试。Mission05已原生观察B的HP100=100、originalRan=false/exception=null，A仍originalRan=true；原生ERR0。只证明此同值路径，不证明真实changedHP或完整世界一致。

其余新增主要是 DEBUG 观察器、QA 驱动与测试：非泛型原生周期钩子替代实际 Desktop CLR 无法安装的闭合泛型诊断钩子；NPC 对象退役调用链、HP setter 前后观察；完整日志分页归档；Mission/地图准备、UDP活性、原生元数据只读收集；两个测试夹具隔离与真实游戏线程执行。未重写 AutoSync、注册主体、快照/增量追赶、LiteNetLib/Protobuf/LZ4、Owner/Puppet、移动和伤害路由。

## 测试与基线

| 检查 | 结果 | 证据边界 |
|---|---|---|
| reg25 gi | 3058 PASS / 0 FAIL | 无失败 |
| reg25 coop | 1442 PASS / 195 FAIL | 与原基线失败全名逐项相同；仍是实际失败 |
| reg25 common | 423 PASS / 0 FAIL | 无失败 |
| reg25 e2e | 61 PASS / 10 FAIL | 与原基线失败全名逐项相同；仍是实际失败 |
| reg25 编译 | exit0 | 游戏源码5a3bc8f4 |
| root QA scenario | 108/108 PASS | 实际7b07de4b日志；独立复制目录复验最终作者268096同样108/108 |
| 额外 coordination | 72/72 PASS | 运行自然结束，最终作者handoff及日志SHA核对；旧INCOMPLETE不是最终结果 |
| 新HP适配专项独立复验 | 22/22 PASS | 实际最终4346作者DLL、真实GameThread pump；实际加载5DLL路径/MVID/SHA前后稳定 |
| 补充NPC位置验收 | **FAIL，exit1** | 稳定暂停同ticks两份快照各4/24不等，独立重跑仍FAIL |
| 原生场景 | 见下表 | 自动测试不替代实机；原5阶段断言通过但NPC位置差异保留，不升级整体验收 |

完整195条Coop及10条选定E2E失败全名、原TRX与SHA见同目录 `final-reg25-failure-names.json`，其余两个suite失败名单为空。reg19/23各出现过一个新增夹具失败，原FAIL保留；经隔离队列/独立LongRunning worker修复后reg20/21/22/24/25按原全名比较恢复基线。不得写“整个方案测试全绿”。reg23历史具体内因未采到，受控线程池饥饿可重现测试调度混淆，不等于已证明历史故障同因。

## 十四次已结束原生运行

| 运行 | 结果 | A/B PID | 工具/实际游戏提交 | PASS/FAIL/NOT_RUN | 实际范围 |
|---|---|---|---|---|---|
| p2p-sale-29 | PASS_BOUNDED_SCENARIO | 19312/12100 | 72c4b68e / 6b177722 | 9/0/0 | 售俘四路/各一次/A→B→A限定通过 |
| p2p-natural-r2-01 | FAIL | 22872 | 6750baa1 / 6750baa1 | 0/0/4 | 按原始阶段记录；不得扩展为战斗验收 |
| p2p-natural-r2-02 | FAIL | 25960/15444 | 53e169d7 / 53e169d7 | 1/1/2 | A自然推进15.63026186125353h；未满24h |
| p2p-mission-r2-01 | FAIL | 16588/20168 | 53e169d7 / 53e169d7 | 1/1/3 | 按原始阶段记录；不得扩展为战斗验收 |
| p2p-sale-30 | PASS_BOUNDED_SCENARIO | 20676/19216 | 53e169d7 / 53e169d7 | 9/0/0 | 售俘四路/各一次/A→B→A限定通过 |
| p2p-udp-active-22 | FAIL | 22364/13424 | 53e169d7 / 53e169d7 | 2/1/2 | 真实丢包延迟乱序；第2轮兵员表ERR后中止 |
| p2p-mission-r2-02 | FAIL | 21484/4032 | 7809a3cd / 53e169d7 | 3/1/1 | 按原始阶段记录；不得扩展为战斗验收 |
| p2p-natural-r2-03 | FAIL | 24828/25364 | 9d38062f / 9d38062f | 1/1/2 | A自然推进15.802507027983665h；未满24h |
| p2p-natural-r2-04 | FAIL | 15352/11744 | 1cb8a532 / 1cb8a532 | 1/1/2 | A自然推进15.367131194565445h；未满24h |
| p2p-mission-r2-03 | FAIL | 25680/8752 | 1cb8a532 / 1cb8a532 | 3/1/1 | 按原始阶段记录；不得扩展为战斗验收 |
| p2p-mission-r2-04 | FAIL | 19736/23240 | 5a3bc8f4 / 5a3bc8f4 | 0/0/5 | 元数据采集失败；0 feature，HP未观测 |
| p2p-mission-r2-05 | 原5断言PASS；NPC补充FAIL | 14468/5544 | 7b07de4b / 5a3bc8f4 | 5/0/0 | 按原始阶段记录；不得扩展为战斗验收 |
| p2p-udp-active-23 | PASS_BOUNDED_SCENARIO | 17008/19048 | 7b07de4b / 5a3bc8f4 | 5/0/0 | 3轮原断言PASS；请求前切断writer通信，未获ACK时安全冻结/拒绝重连 |
| p2p-udp-candidate-24 | PASS_BOUNDED_SCENARIO | 24632/26240 | 7b07de4b / 5a3bc8f4 | 2/0/4（活动/无损恢复未运行） | 已排队barrier hold/drop与新屏障重连限定通过；非lost-writer无损恢复 |

每次原始 stop_run 均记录已拥有进程树退出；日志严格失败与物理 cleanup 分开。Natural01 只启动A，B NOT_STARTED；不冒充双进程测试。其余 PID、启动时间、runToken、运行程序集 MVID、快照、网络条件、原始日志和 BMP SHA 位于 compact/full JSON。BMP 审计只证明原始字节/格式/PID关联，不能替代场景肉眼检查或连续渲染验收。

## QA归档与故障证据

原归档重置分页曾把完整前缀再次追加。现先缓存本轮全部页、验证前缀再整段替换；保留合法重复日志，不按文本去重。Sale29/30 原始归档、分页重放与sidecar逐字节一致，四次应用记录完整，最终停进程后仍核对账本。此结论绑定各自实际游戏版本，不替代新版自然周期/故障恢复验收。

Active22真实UDP实验 dropEvery11、delayEvery5/250ms，实际 dropped626/delayed1250/reordered1244；第1轮A→B→A通过，第2轮B的WorldState采集遇到新looter的TroopRoster缺handle ERR，随后约2.272s采样关键世界恢复一致。没有通用对象图“创建已提交”凭证，不能跳过该对象、降低断言或静默吞错把场景改成PASS。第3轮及最终ACK失联步骤未运行。

## 自然周期阻挡及未实现修复

Native03诊断把null-MobileParty定位到Banner静态获胜方，已用单个调用方guard修复并在Natural04限域复现确认。剩余路径：

- Army：第一次 `DisperseInternal` 结束后 **DestroyPostfix** 已移除注册，外层 `OnRemovePartyInternal` / `set_Army` 继续；其后的 `AiBehaviorObject` 写入缺ID，`FinishArmyObjective → Disband →` 第二次 `DisperseInternal` 仍进入并写 `Kingdom=null`，第二次 DestroyPostfix 又请求移除。这不是一个可用“重复调用直接早退”掩盖的日志问题。尚未实现基于终态的幂等退役适配，也未重写Registry。
- PartyBase：`SiegeCompleted/ChangeOwner` 路径实际先移除驻军PartyBase，MapEvent最终化随后临时LeaderParty仍引用它。Native03具体对象 `PartyBase_garrison_party_castle_A8_clan_aserai_8_1`，handle32519/ref01ceb645。需要有因果证据的每次finalization生命周期适配及客户端对象图顺序验证；未实现。不能提前detach或忽略LeaderParty同步。

Natural04只推进15.367131194565445h，虽跨日历界线，仍不是完整自然24h；B自然一日、完整A→B→A、日工资实际扣款与全周期唯一结算未验收。经济模型/工资计算次数不证明工资已支付；不得用手调时间或短时Tick补票。

## 历史问题边界

- Active11旧兵员表缺号：当时无足够对象ID/调用者；Active22的新采集窗口给出更具体证据，但不能断言两者同因或已修复。
- Candidate19中间视觉类型计数A2902/B3421，下一handle均45029，最终又一致。没有该样本真实ID冲突证据，也不是所有中间阶段完全一致的证明；保留Math.Max合并不回退ID。旧超时UI提示仍有限制。
- Sale20真实 `UsableMachine.OnMissionEnded → Mission.set_MissionEnded → EndMission → LeaveLocationCommand` NullReference未修复。后来正确等待/绕过多余准备未再复现，不等于修复Mission核心。
- M04本次是get_run3秒只读状态取消后驱动空引用，原生ERR0；全部Mission feature未运行，不能证明HP适配实机有效。QA适配只在原30秒预算重读完整fresh pair，身份/版本/MVID错误即停止，不用历史ready缓存。

## 失联：安全冻结不等于无损恢复

当前冻结/最终屏障复用现有ReliableOrdered、Epoch/房间/权威/交接序号、当前peer校验和游戏线程顺序。缺旧权威最终ACK时不提交新租约，房主不以过期检查点强行接管。若旧权威在收到freeze前断线，不能仅凭发出请求就声称其也已冻结；安全性在于不提升第二个writer。旧活动remote租约未解决时重连仍可能被CampaignCheckpointUnavailable拒绝。

已导出的五部分是编号下界/类型计数、兵员表顺序和XP、共享CoopSession、周期调度器/20ticker、世界时钟。它们要求现有对象图与计数/伤员等先已一致，并不是完整Campaign快照。缺：完整对象绑定/创建删除图、原生Campaign/完整NPC AI内部状态、RNG、未完成异步结果、断线可持久重放的最终delta序列/已执行指令及结果水位、完整检查点提交证明。

`CampaignAuthorityHandoffBarrierClientHandler` 依次apply allocator→roster→session→scheduler→clock，后项拒绝并不回滚已合并的前项；未激活新租约不能被描述为已恢复一致状态。普通join的LZ4原存档/Replay/增量尾部应继续复用，但不是跨断线的持久WAL。无损恢复协议本轮未实现，不冒险扩大为无证据重写。

## 审计与清理事项

旧 `p2p-cold-checkpoint-audit` 清理曾被策略阻止，继续登记cleanup_required；不绕过，亦不影响已留档成果。其他任务只在限定范围完成、证据留档后收口，未通过功能保持未完成。总控已确认游戏退出及实机锁释放；本轮已完成的临时工作树均在全部ignored artifacts保全后清理，失败的自然日/Mission父任务仍由总控负责。旧安全清理拒绝原因为 unknown cleanup residue: source/GameInterface，未绕过。

另有一次本轮内部证据保全事故：清理Active22只读工作树时漏保全两份后到的Mission fd8独立review附件，原始游戏日志/FAIL/截图和已提交证据均完整。旧附件不能恢复，旧SHA登记；后来重新独立执行17/17并明确是新复验，不伪造旧文件。后续每次cleanup前用唯一目录保全全部ignored artifacts及字节/SHA manifest，包括late review。incident原收据 `.git/codex-coordination/evidence/p2p-reliability-r2-20261019/late-review-retention-incident.json`。

## 下一优先级

1. 修复已确认的NPC权威切换坐标一致性缺口，复用成熟移动同步/适配层；不能以采样时差解释暂停同ticks持续差距，也不能靠降低断言掩盖。
2. 最小适配修复Army/PartyBase退役顺序，保留旧FAIL，重新完成真实24hA、24hB、A返回与完整周期/经济验收。
3. 为新对象图采集建立可验证就绪边界，复验Active22多轮真实网络故障；不得跳过新增ERR。
4. 在明确状态清单、提交证书及执行结果水位基础上设计无损恢复；仍无完整依据时保持安全冻结。实际changedHP路径另行按原请求/权限适配，禁止把exact-no-change扩成权限放行。

## Mission05与Active23的实际验收限界

Mission05原酒馆五阶段断言PASS，独立复验六份实际summary快照的正式WORLD_KEYS七项（partyIdentityHash、rosterCountsHash、rosterXpHash、volunteerHash、heroGoldHash、nextHandle、ticks）及PLAYER_KEYS十六项通过。这些断言没有覆盖全部NPC当前位置。

**补充NPC位置实际状态验收为FAIL（exit1）**：在epoch6全员Mission、暂停同ticks78699089256620，两对原始calls323/324与341/342相隔6.779秒，6042的A441.231049/512.885559与B433.513184/514.2225保持固定7.832806差距；每份24个样本均4个位置不等。不是普通RPC采样时差。最终epoch9 calls458/459，6042为A437.913849/513.4601、B424.965973/515.703552，差13.140798；6050、6087、6305也不同。样本兵力/伤兵相同。没有逐包位置及激活瞬间证据，不能断言捕获某一次具体“瞬时回退”，但**Mission关键世界位置一致性明确未通过**。额外allocationCounters/hash差异本身不证明实际handle冲突。

独立NPC报告JSON SHA0a41f021aa7d053f7e02b62ffe0ad183dc9cbaef334084ac3cc3afe8a57dd5b4；原FAIL日志SHA598bbfa752666c75ae93dd605cb7699f9d06c934e3378349863365b34eeeaec7。总审查器在自有复制目录重跑同一未修改的补充断言仍exit1/FAIL，日志SHA4d15525cd2bde28160aaa5cd17144724ca828f196c6c836a70accec5eb189dfd，收据SHA2663aaefd1d2ce89244aaa98225a8cca63d39ebfe742d937fcc14ef6ebdb7890。不能把证据提取器exit0混称为位置验收PASS。

已证实的适配接缝是ListenHost观察者仍IsServer，不进入普通IsClient移动位置纠偏；常规行为快照复用目标/导航而不写当前Position，最终五类handoff检查点没有NPC位置/完整导航接管基线。建议新增交接专用最终移动基线适配，复用成熟Snapshot/JoinState字段和Epoch屏障；尚未实施，不能直接把普通移动包改为强制瞬移，也不重写移动核心或Registry。

全员离图窗口两端世界Tick/时钟差0，酒馆Mission逻辑时间继续，双向UDP103/32；root已查看双方all-away-after BMP确实酒馆与MapTimePaused。B原生HP100同值originalRan=false、exception=null，A仍true；只证明此无变化路径。上述不构成战斗伤害/OwnerPuppet或完整世界一致验收。

Active23 A17008/B19048，run871ce9137b304d53bdb380b4694c77e3，基线+三轮真实faultedA-B-A+安全失联原断言PASS5/5。drop3545/delay1880/reorder1866包含blackout。实际故障边界是确认B为writer后，**房主auto交接请求之前**切断双向通信；76060ms、59个样本保持原Epoch/时钟，房主不提升，之后显式拒绝过期重连。barrierFaultPointVerdict=NOT_RUN；已排队最终barrier丢失、最终包重复均不能借此宣称实机通过。独立安全审计收据SHAd82a2b2040bc17af1443fb3d9afc8ab1e24814248259ecc3bd92d50ada840e4a。

Candidate24已结束，A24632/B26240，run d65f5f5fd53b4fbfb7c31132f8f4dbb2，原candidate-only两阶段PASS，queuedbarrier故障点REACHED：暂停epoch4中新barrier sequence1发送日志与单向UDP hold关联，394个实际A→B报文被扣留后394个记录丢弃，B→A仍有通信；随后75707ms、60个冻结样本，重连必须得到更高序号新barrier后才成为下一任，再正常返回A。实际上覆盖“已提交暂停检查点后的candidate barrier故障”，没有恢复活动旧writer未提交的进度；没有解密逐个Protobuf载荷，也没有最终协议包重复注入。独立审计收据SHA a4e2adaaa1080956772c62a3e566e124b9c0cbc39d45a88f9053710b6ff32c4b。Candidate PASS不改变Mission父任务needs_fix，也不改变无损旧writer恢复NOT_IMPLEMENTED。

## 本次集成提交

- `ef2f52405874a0802a3985bc43fd401e2248c1b2` Restore inherited model policy for reliability workers
- `4d0d59fdf9b8ab4cfc93310b287d897b57673173` Reconcile complete native log archives by byte prefix
- `3fbeb0041c6747978f330c148f02edba4c20e736` Keep diagnostic prefixes intact and verify final sale ledger
- `72c4b68e6357fccf95cee69cd8f3c1c506a625be` Record bounded native diagnostic findings and remaining evidence
- `ce9bdc4f6ea1e6b10592e95e32d0a43dcfde5419` Observe native due slots and complete campaign cycle receipts
- `7116fe9b5013c02b3266224dba20ac70dd846584` Add native Mission authority pause acceptance scenario
- `63ade6331fb9639f0a17391392af671c0732d377` State Mission control liveness evidence boundary
- `77cdcd974474b89a030471ed371e77f159af9593` Observe game UDP during all-away Mission pause
- `0134bb18e06893f9efc926d4f83b25335800c9fc` Reject incomplete paused lease evidence
- `6750baa176b3a687150560845f2c84256c98a710` Isolate queued player presence test actions before peer changes
- `53810c631a106b572a55336ccd97a6fb2de02653` Capture native startup identities before readiness and separate log failures
- `53e169d7338a8c3d6c4fc320634132de2c03833a` Observe native ticker groups without closed generic detours
- `7809a3cda92e106977f33e2a3d75a819c7231ef4` Establish free-map observer before Mission clock checks
- `6463d15e12828761c327db0bbe6fca4f1d7090f3` diagnostics: trace native party and army registry lifecycles
- `9d38062f3f3aabb8b573a24d82c1e1a67453c5ce` diagnostics: preserve original exceptions when formatting fails
- `e773b42b918597d0eb24b24e26803f4fbe6ea2bf` Observe native Mission hero health setter without changing writes
- `6e254b823fc99067f6259dd0d6264bfbc8aeb435` fix: skip static winners before player banner loot checks
- `1cb8a5329dfcce95b1a794f198fbecf78bafb95f` Run freeze fixture worker independently of the test thread pool
- `5a3bc8f4cc032171da8577411b605dff607f02fc` Adapt redundant local Mission health assignments without widening authority
- `7b07de4b79216efbfeab33e87e625388095b6f26` Bound Mission runtime metadata retries to fresh same-process evidence

## 完整修改文件（78ac9216 → 7b07de4b）

- A [doc/p2p-diagnostic-followup-2026-10-19.md](<E:/code/code home/BannerlordCoop/doc/p2p-diagnostic-followup-2026-10-19.md>)
- A [doc/p2p-freeze-worker-fixture-2026-10-19.md](<E:/code/code home/BannerlordCoop/doc/p2p-freeze-worker-fixture-2026-10-19.md>)
- A [doc/p2p-mission-health-diagnostics-2026-10-19.md](<E:/code/code home/BannerlordCoop/doc/p2p-mission-health-diagnostics-2026-10-19.md>)
- A [doc/p2p-mission-health-noop-2026-10-19.md](<E:/code/code home/BannerlordCoop/doc/p2p-mission-health-noop-2026-10-19.md>)
- A [doc/p2p-mission-lifecycle-acceptance-2026-10-19.md](<E:/code/code home/BannerlordCoop/doc/p2p-mission-lifecycle-acceptance-2026-10-19.md>)
- A [doc/p2p-mission-runtime-metadata-2026-10-19.md](<E:/code/code home/BannerlordCoop/doc/p2p-mission-runtime-metadata-2026-10-19.md>)
- A [doc/p2p-native-lifecycle-diagnostics-2026-10-19.md](<E:/code/code home/BannerlordCoop/doc/p2p-native-lifecycle-diagnostics-2026-10-19.md>)
- A [doc/p2p-natural-cycle-acceptance-2026-10-19.md](<E:/code/code home/BannerlordCoop/doc/p2p-natural-cycle-acceptance-2026-10-19.md>)
- A [doc/p2p-static-winner-banner-2026-10-19.md](<E:/code/code home/BannerlordCoop/doc/p2p-static-winner-banner-2026-10-19.md>)
- M [source/Coop.Tests/Client/Services/Session/CampaignAuthorityFreezeClientHandlerTests.cs](<E:/code/code home/BannerlordCoop/source/Coop.Tests/Client/Services/Session/CampaignAuthorityFreezeClientHandlerTests.cs>)
- M [source/Coop.Tests/Server/Services/Players/PlayerPartyVisibilityLeaseTests.cs](<E:/code/code home/BannerlordCoop/source/Coop.Tests/Server/Services/Players/PlayerPartyVisibilityLeaseTests.cs>)
- A [source/GameInterface.Tests/Services/Banners/BannerCampaignBehaviorInterfaceTests.cs](<E:/code/code home/BannerlordCoop/source/GameInterface.Tests/Services/Banners/BannerCampaignBehaviorInterfaceTests.cs>)
- M [source/GameInterface.Tests/Services/CampaignAuthority/CampaignAuthorityDebugCommandTests.cs](<E:/code/code home/BannerlordCoop/source/GameInterface.Tests/Services/CampaignAuthority/CampaignAuthorityDebugCommandTests.cs>)
- M [source/GameInterface.Tests/Services/CampaignAuthority/CampaignCycleDiagnosticsTests.cs](<E:/code/code home/BannerlordCoop/source/GameInterface.Tests/Services/CampaignAuthority/CampaignCycleDiagnosticsTests.cs>)
- A [source/GameInterface.Tests/Services/CampaignAuthority/CampaignMissionHealthDiagnosticsTests.cs](<E:/code/code home/BannerlordCoop/source/GameInterface.Tests/Services/CampaignAuthority/CampaignMissionHealthDiagnosticsTests.cs>)
- A [source/GameInterface.Tests/Services/CampaignAuthority/CampaignObjectLifecycleDiagnosticsTests.cs](<E:/code/code home/BannerlordCoop/source/GameInterface.Tests/Services/CampaignAuthority/CampaignObjectLifecycleDiagnosticsTests.cs>)
- A [source/GameInterface.Tests/Services/Heroes/Patches/MissionHeroHealthNoopPatchTests.cs](<E:/code/code home/BannerlordCoop/source/GameInterface.Tests/Services/Heroes/Patches/MissionHeroHealthNoopPatchTests.cs>)
- M [source/GameInterface/Services/Banners/Interfaces/BannerCampaignBehaviorInterface.cs](<E:/code/code home/BannerlordCoop/source/GameInterface/Services/Banners/Interfaces/BannerCampaignBehaviorInterface.cs>)
- M [source/GameInterface/Services/CampaignAuthority/CampaignAuthorityDebugCommands.cs](<E:/code/code home/BannerlordCoop/source/GameInterface/Services/CampaignAuthority/CampaignAuthorityDebugCommands.cs>)
- M [source/GameInterface/Services/CampaignAuthority/CampaignCycleDebugCommands.cs](<E:/code/code home/BannerlordCoop/source/GameInterface/Services/CampaignAuthority/CampaignCycleDebugCommands.cs>)
- M [source/GameInterface/Services/CampaignAuthority/CampaignCycleDiagnostics.cs](<E:/code/code home/BannerlordCoop/source/GameInterface/Services/CampaignAuthority/CampaignCycleDiagnostics.cs>)
- A [source/GameInterface/Services/CampaignAuthority/CampaignMissionHealthDiagnostics.cs](<E:/code/code home/BannerlordCoop/source/GameInterface/Services/CampaignAuthority/CampaignMissionHealthDiagnostics.cs>)
- A [source/GameInterface/Services/CampaignAuthority/CampaignObjectLifecycleDiagnostics.cs](<E:/code/code home/BannerlordCoop/source/GameInterface/Services/CampaignAuthority/CampaignObjectLifecycleDiagnostics.cs>)
- A [source/GameInterface/Services/Heroes/Patches/MissionHeroHealthNoopPatch.cs](<E:/code/code home/BannerlordCoop/source/GameInterface/Services/Heroes/Patches/MissionHeroHealthNoopPatch.cs>)
- M [tools/coordination/p2p-workstreams.json](<E:/code/code home/BannerlordCoop/tools/coordination/p2p-workstreams.json>)
- A [tools/mcp/scenarios/native-log-archive.mjs](<E:/code/code home/BannerlordCoop/tools/mcp/scenarios/native-log-archive.mjs>)
- A [tools/mcp/scenarios/native-log-archive.test.mjs](<E:/code/code home/BannerlordCoop/tools/mcp/scenarios/native-log-archive.test.mjs>)
- A [tools/mcp/scenarios/p2p-mission-lifecycle.mjs](<E:/code/code home/BannerlordCoop/tools/mcp/scenarios/p2p-mission-lifecycle.mjs>)
- A [tools/mcp/scenarios/p2p-mission-lifecycle.test.mjs](<E:/code/code home/BannerlordCoop/tools/mcp/scenarios/p2p-mission-lifecycle.test.mjs>)
- M [tools/mcp/scenarios/p2p-prisoner-sale.mjs](<E:/code/code home/BannerlordCoop/tools/mcp/scenarios/p2p-prisoner-sale.mjs>)
- M [tools/mcp/scenarios/p2p-udp-reliability.mjs](<E:/code/code home/BannerlordCoop/tools/mcp/scenarios/p2p-udp-reliability.mjs>)
- M [tools/mcp/scenarios/p2p-world-lifecycle.mjs](<E:/code/code home/BannerlordCoop/tools/mcp/scenarios/p2p-world-lifecycle.mjs>)
- M [tools/mcp/scenarios/p2p-world-lifecycle.test.mjs](<E:/code/code home/BannerlordCoop/tools/mcp/scenarios/p2p-world-lifecycle.test.mjs>)

## 最终证据索引（原始大文件不入Git）

- snapshot08 full：artifacts/reliability-index-r2/snapshot08/final/acceptance-index.json，SHA256 9ecfa84d66d3ca6851bb007619eed131c7983e853226cd618467111dabb9732b。
- 推荐发布compact：artifacts/reliability-index-r2/snapshot08/final/compact-index.json，SHA256 6839b4f81ec9d24b93bf0802c4f13ab9cd53ca4e8a9ac96a3ae93acb4fde6cb7。
- 原始277输入manifest：artifacts/reliability-index-r2/snapshot08/final/input-manifest.json，SHA256 7865136e0a00178299e56e0866fc194dc56bec4a899f85864323508bd2b4ad84。
- 独立/FAIL补充manifest：artifacts/reliability-index-r2/snapshot08/final/supplementary-evidence-manifest.json，SHA256 3d827d8435ea999a2a5bff08c7fe75390effb136541eb6144b2b5111a22e470f。
- 精确195/10失败名单：artifacts/review/final-reg25-failure-names.json，SHA256 f7890b34092cf3ec9d918595f9ebc7f65eaefed86338514b62d530f36ab5e39a。

上述SHA对应保全的审查原产物；正式副本位于 artifacts/p2p-authority/reliability-r2-2026-10-19/，removed-worktree旧路径按同目录retained-locations.json映射。不能把工作树清理后的旧路径当作仍存原位置，也不能篡改旧SHA。原始14场raw/log/BMP仍在主树.mcp-local，版本、PID、启动时间、网络策略和状态字段均可从manifest复核。root另直接从M05原JSONL重做NPC暂停同ticks比较exit1，verificationSHA51fe1ae996b4d2145b5c55ff527b6440346e02bf8f10912f31f16291dc1a8868；Mission父任务明确needs_fix，不因Candidate临时借用父任务的成功receipt而误关闭。

## 正式交付路径与复现

[完整PASS/FAIL索引](../artifacts/p2p-authority/reliability-r2-2026-10-19/compact-index.md)、[结构化发布摘要](../artifacts/p2p-authority/reliability-r2-2026-10-19/acceptance-summary.json)、[277个原始输入SHA](../artifacts/p2p-authority/reliability-r2-2026-10-19/input-manifest.json)、[195/10完整失败名单](../artifacts/p2p-authority/reliability-r2-2026-10-19/final-reg25-failure-names.json)、[20个实现提交与33文件清单](../artifacts/p2p-authority/reliability-r2-2026-10-19/source-change-list.json)、[保全位置映射](../artifacts/p2p-authority/reliability-r2-2026-10-19/retained-locations.json)。原始大日志和BMP仍在本机，不将不可恢复的旧附件假称已保存。

[NPC位置最小复现与调用链](p2p-mission-npc-position-audit-2026-10-19.md)、[Army/PartyBase后续适配论证，尚未实现](p2p-native-lifecycle-followup-2026-10-19.md)。在仓库根执行 `node artifacts/p2p-authority/reliability-r2-2026-10-19/root-mission-critical-acceptance.mjs` 应当真实退出1，复现M05位置一致性FAIL；它们读取已结束的实机证据，不会启动游戏。原五阶段断言和原失败证据保持原样。

最后自然日运行是1cb8，仍FAIL；最终5a3没有完成24h自然日验收。没有发现能够据此宣称P2P根本不可行的已证实引擎约束；本轮发现的是具体状态覆盖和生命周期缺口，修复建议不是已实现成果。
