2026-10-18 动态战役权威迁移实机验收记录

本轮通过了双人大地图 A→B→A、真实城镇 Mission 中的自然交接、全员离开地图暂停及返回恢复。验收只覆盖下述场景，不能据此宣布所有 P2P 玩法、战斗同步、断线恢复或存档重启已经完成。

工作分支为 `codex/p2p-listen-join`。本轮代码差异起点为 `c6d6fe47f59f1d9e76128216faf6d6b36949ad12`，最后一次代码提交为 `4938d5ee86681768a5661c2ee3a0e45da0d97001`，其后提交记录实机证据和报告。失败渲染实验 `23a67ec8` 不在当前 HEAD 的祖先中，原实验提交仍保留。本轮没有推送、创建 PR 或合并到 development。

A 始终是房间连接和转发节点，战役写入权可以交给 B。每个玩家只运行一个原生游戏进程，没有独立服务端，也没有 A 的本地 CoopClient 回环连接。A 与 B 保留各自的原生 MainHero/MainParty；权威交接不切换本地角色。

实机证据如下。表中的 server/client1 是测试工具的进程名称，不表示战役写入权始终属于 server。

| 场景 | 实际构建 | 进程 PID（A / B） | 结果及证据 |
| --- | --- | --- | --- |
| 双人大地图 A→B→A，移动、关键世界数据、经验及编号连续性 | `230af3ce4` | 24984 / 29976 | [Map Run16](../artifacts/p2p-authority/map-16-accepted.json)，run `f2b18040c3b34e7ab8745c512b788857`，证据提交 `1600cfed4` |
| A 进入真实 Mission，B 在地图推进，A 返回后优先恢复 | `4938d5ee8` | 29112 / 20608 | [Mission Run01](../artifacts/p2p-authority/mission-01-accepted.json)，run `03bf651923284d66a1d2686a15043497`，证据提交 `e1dfc72b7` |
| 两人均在真实 Mission 时暂停，B 先返回恢复，A 返回后取回优先权 | `4938d5ee8` | 23696 / 30188 | [Mission Run02](../artifacts/p2p-authority/mission-02-pause-resume-accepted.json)，run `a796e32dc297483f877bc5ef46a93b47`，证据提交 `ef8de00ce` |

大地图验收中，任期按 A epoch 3 → 暂停 4 → B 5 → 最终冻结 → 暂停 6 → A 7 变化。B 为权威时 B 的 Campaign.Tick 计数增长，A 的世界 Tick 计数停止；返回后相反。两人的原生角色及部队身份保持不变，英雄、家族、部队注册均为单份。移动通过原生队伍 point-order 命令驱动，最终双方观察到的位置误差小于 0.3 地图单位。这不替代物理鼠标和镜头输入验收。

在 B 权威和返回 A 两个阶段暂停取样，关键世界的部队身份、兵力和伤员、兵种经验、招募库存、英雄金币以及 next-handle 检查均一致，原生世界时钟 Tick 精确一致。NPC 位置样本采用 0.5 地图单位容差。先在 B 修改兵种经验，再在交接前暂停并记录 B 的最终值，A 接棒后必须精确保留该值；恢复时间后允许原生训练继续增加经验。不能把正常训练增长误判为交接丢失。

编号验证分别在 A、B、返回 A 创建原生空 TroopRoster，检查另一端的 ID/handle，再退役并要求明确的 `probe_absent` 结果。三次 ID 与 handle 严格递增，已退役编号不复用。观察端 AddExisting 会增长本地类型计数，因此观察期间类型计数哈希相同不是验收条件；交接采用高水位合并，原始差异保留在证据中。

Mission Run01 没有使用调试 availability override。A 实际进入 `MissionState` 后，其世界 Tick 保持 5252，地图上的 B 从 5308 推进到 5517，世界小时从 2186083.8773 增至 2186085.3897。A 原生离开 Mission 后按最终检查点流程恢复 epoch 7，A 的世界 Tick 从 5652 推进到 5859，B 停留在 5641。原生酒馆画面和 B 大地图画面均已检查。

Mission Run02 中，两人实际进入原生酒馆 Mission 后确认暂停 epoch 6。持续五秒，两端世界 Tick 均保持 5568，世界小时均保持 2186085.974084111。B 先退出 Mission 时 A 仍在 Mission，B 在 epoch 7 从 Tick 5568 推进到 5745，世界小时增至 2186087.4782303334。A 随后退出，在 epoch 9 恢复优先权。两端接收任期更新有传输延迟，判断依据包括本地 frozen 标志和实际 Tick，不能把瞬时不同任期直接认定为双权威。

上述三轮的两端日志中，RealTick failure、重复对象 ID 和 AI batch failure 三类检查均为零。每轮均显式调用 `stop_run`，确认双方 `cleanupComplete=true`、`processAlive=false`、`processTreeAlive=false`。完整消息时序、状态、关键操作、日志 SHA256 和清理结果在对应 JSON 内。该零计数只针对列出的日志检查，不表示日志中所有旧问题都不存在。

本轮实现和修复集中在以下职责：

- 最终屏障与 Epoch：旧权威在游戏线程完成正在执行的工作，等待 `Campaign.WaitAsyncTasks`，清空成功完成的 AI task 引用，再发送合并器中的最终增量并冻结写入。等待发生在任期锁之外，避免 AI 工作线程发布结果时死锁。候选者和 ACK 与相关增量走同一游戏线程 FIFO，校验连接身份及任期；最终检查点未确认时不提升候选者。超时或断线不被当作成功 ACK。
- 连续状态：屏障及冻结 ACK 同时携带对象编号高水位、兵种/俘虏经验和栈顺序、共享会话、最终世界时钟、原生周期调度检查点。调度检查点覆盖 20 个 ticker 的成员顺序、Index、TickDebt，以及当前存档的 19 个周期事件定义与下一触发时间。先验证完整检查点再应用；派生队伍列表只在成员完全一致时原地重排，保留原生后续增删能力。
- 世界推进与表现：保留原生 MapState 画面和输入调用，单独限制 Campaign.Tick 和权威 RealTick。观察端只执行本地时钟平滑、移动和可见性缓存路径。观察端自己的受控队伍可以处理本地导航，NPC 世界 AI 仍由唯一权威推进。冻结和有效会话暂停时都不推进世界。
- 身份及消息：修复房主队伍完整 ID 与 compact ID 的归一化，避免 A 在 B 任期的移动请求被静默拒绝；操作在游戏线程重新验证归属和 Epoch，重复或过期操作不再次执行。生产 MessageBroker 按消息实际类型发布，避免测试替身掩盖泛型分派差异。初次入场租约先于快照加载期的丢弃队列发送，后续租约保持有序增量通道。
- 原生对象及经验：动态会话向潜在接棒者广播完整兵种经验；原生 TroopRoster 工厂按当前未冻结的世界权威注册对象。组件替换调用原生 Before/After 维护派生 AI 列表，观察端也注册同一原生 MapTracks 周期事件，修复交接时的成员和事件定义缺口。

对应主类包括 `CampaignSimulationAvailabilityServerHandler`、`CampaignAuthorityFreezeClientHandler`、`CampaignAuthorityHandoffBarrierClientHandler`、`CampaignAuthoritySyncRelayServerHandler`、`CampaignAsyncTaskDrain`、`CampaignSchedulerCheckpointService`、`CampaignSimulationAuthorityPolicy`、`CampaignObserverFrame` 和 `RuntimeMessagePublisher`。详细演进和未通过的中间实机记录见 [架构决策记录](decisions/0001-distributed-campaign-authority.md)；中间失败没有被删除或改写为通过。

自动化结果如下，失败名单逐项对比，不能合并成“测试全绿”。最终结果及原始日志哈希见 [回归对比](../artifacts/p2p-authority/final-regression-comparison-2026-10-18.json)。

| 检查 | 本轮最终结果 | 基线及判断 |
| --- | --- | --- |
| 主 Mod Debug，禁止自动部署 | 0 错误，60 警告 | 构建成功，警告仍保留 |
| GameInterface 全量 | 2734 通过，0 失败 | 包含本轮调度、异步清空、路由、编号、经验和 Mission 入口测试 |
| Coop 全量 | 1360 通过，195 失败 | 与独立 `8d05c0d1d` 基线的 195 个失败名称逐项一致；基线 1333 通过 |
| TroopRoster / MobilePartyMovement E2E 子集 | 60 通过，10 失败，共 70 | 与独立构建 `84ddab7b6` 基线的 10 个失败名称逐项一致，不是整个 E2E 测试集 |

过程中另一次 Coop 全量曾出现 `SteamTunnelHostTests.Stop_RemovesEveryRelayEndpointIdentity`，当时为 196 失败；单独重跑和后续全量均通过该例。该间歇结果保留在决策记录，最终基线对比没有隐藏它。新增 scheduler wire-format 两例、交接/冻结/availability 协议 31 例及分阶段补充测试已在相应功能提交前运行。最终全量结果不代表所有测试项目都已执行。

可从仓库根目录运行以下命令。主 Mod 使用空 `ModName` 禁止构建钩子自动部署，其他两条同样显式携带该参数。

```powershell
dotnet build source\Coop\Coop.csproj -c Debug --no-restore -p:ModName= --verbosity quiet
dotnet test source\GameInterface.Tests\GameInterface.Tests.csproj -c Debug --no-restore -p:ModName= --verbosity quiet
dotnet test source\Coop.Tests\Coop.Tests.csproj -c Debug --no-restore -p:ModName= --verbosity quiet
dotnet test source\E2E.Tests\E2E.Tests.csproj -c Debug --no-restore -p:ModName= --filter "FullyQualifiedName~Services.TroopRosters|FullyQualifiedName~MobilePartyMovementTests" --verbosity quiet
```

实机使用已发布的 Coop MCP stdio API 和官方 MCP SDK，没有采用私有文件/管道命令绕过 API。顺序为 `start_run` 加载 `P1B_DirectIP_Recruit` → 等待 A campaign ready → `start_client` → 等待 B readyToJoin → `join_client` → 等待 B campaign ready 且其队伍在两端 active。地图测试命令在当前权威执行，另一端核对结果。Mission 使用实际查出的 `Location_town_ES1_tavern`，先 `coop.debug.location.enter` 建立 `town_ES1` encounter，再在必要时第二次 enter 打开原生 Mission，以 `coop.debug.location.leave` 退出；不是只修改 availability 标记。

本机原始脚本和证据位于 `.mcp-local/map-run-16.mjs`、`.mcp-local/mission-run-01.mjs`、`.mcp-local/mission-run-02.mjs` 及各自 JSONL。原生日志和图像位于 `.mcp-local/runs/<上表 runId>/`。这些机器相关内容未提交；可移植的状态、日志哈希和操作摘要已提交到 artifacts。MCP 的 PNG 传输超过图像限制时保留并检查原生 BMP，没有把图像返回失败当作截图成功。

本机已检查的画面可直接打开：

- [A 酒馆 Mission](../.mcp-local/runs/03bf651923284d66a1d2686a15043497/server-b889063e8b1848aca5b32b6019678a5b.bmp) 与 [B 大地图](../.mcp-local/runs/03bf651923284d66a1d2686a15043497/client1-3c4a21da55514a809ba2359d66b8a28b.bmp)。
- [全员离开地图时 A Mission](../.mcp-local/runs/a796e32dc297483f877bc5ef46a93b47/server-9392c023464e419cb6e2dc2bce2c8923.bmp) 与 [B Mission](../.mcp-local/runs/a796e32dc297483f877bc5ef46a93b47/client1-b2eb0c1895ea4a7eb41770b92439d786.bmp)。

仍需解决或单独验收的问题如下：

- 部分旧的城镇、招募、物品、军团、围城逻辑仍按 IsServer/IsClient 区分传输身份，尚未全部改为世界权威。尤其 `RecruitmentVMPatches.OnDone` 的旧路径、玩家进出城镇及 ItemRoster 路由需要独立的动态权威场景。招募库存哈希一致只证明本轮取样数据一致，不证明迁移期间的所有招募操作正确。商店物品按既定目标允许本地差异。
- 断线的旧权威如果不能提供最终 ACK，会安全暂停；尚未实现从此状态自动恢复。这里没有以不安全的强制提升冒充容错。
- 动态权威下的原生冷存档、重启和恢复尚未验收；原生加载会重建派生 ticker 列表，还需验证调度/共享状态的持久化连续性。全球 RNG 状态未迁移，未声称后续模拟是逐位确定性的。
- 真实 Mission 只验收城镇酒馆的生命周期和权威暂停恢复。B 酒馆画面中 NPC 为空，场景 NPC/玩家同步和战斗同步未验收。没有修改千人战场同步。
- B 在有效租约暂停时界面仍显示选定的 MapTime Normal，世界实际上已停止；有效暂停状态尚未反映到该 UI。
- 原生队伍命令通过不等于物理鼠标/镜头输入通过，动态角色下的物理输入仍需专项测试。

只读复核未发现阻止上述已验收场景的必修问题，复核没有另行运行游戏。当前实测也没有发现使该单进程迁移方案根本不可行的引擎约束；没有执行过的玩法仍属于未验证。

以下为相对本轮代码起点的完整 C# 文件清单，A/M/D 分别表示新增、修改、删除。另有本报告、决策记录和 artifacts 证据文件。

| 状态 | 文件 |
| --- | --- |
| A | `source/Common.Tests/Serialization/CampaignSchedulerCheckpointSerializationTests.cs` |
| A | `source/Common/CampaignSchedulerCheckpoint.cs` |
| A | `source/Common/Messaging/RuntimeMessagePublisher.cs` |
| M | `source/Common/Network/Session/NetworkCampaignAuthorityFreeze.cs` |
| M | `source/Common/Network/Session/NetworkCampaignAuthorityHandoffBarrier.cs` |
| M | `source/Coop.Core/Client/Services/MobileParties/Handlers/ClientMobilePartyBehaviorHandler.cs` |
| M | `source/Coop.Core/Client/Services/Session/CampaignAuthorityFreezeClientHandler.cs` |
| M | `source/Coop.Core/Client/Services/Session/CampaignAuthorityHandoffBarrierClientHandler.cs` |
| M | `source/Coop.Core/Client/Services/Session/CampaignAuthoritySyncEnvelopeClientHandler.cs` |
| M | `source/Coop.Core/Server/Services/MobileParties/PacketHandlers/PartyAIBehaviorPacketHandler.cs` |
| M | `source/Coop.Core/Server/Services/Session/AuthorityLeaseDistributionHandler.cs` |
| M | `source/Coop.Core/Server/Services/Session/CampaignAuthoritySyncRelayServerHandler.cs` |
| M | `source/Coop.Core/Server/Services/Session/CampaignSimulationAvailabilityServerHandler.cs` |
| M | `source/Coop.Tests/Client/Services/MobileParties/ClientCampaignAuthorityPartyBehaviorHandlerTests.cs` |
| M | `source/Coop.Tests/Client/Services/Session/CampaignAuthorityFreezeClientHandlerTests.cs` |
| M | `source/Coop.Tests/Client/Services/Session/CampaignAuthorityHandoffBarrierClientHandlerTests.cs` |
| M | `source/Coop.Tests/Client/Services/Session/CampaignAuthoritySyncEnvelopeClientHandlerTests.cs` |
| A | `source/Coop.Tests/Mocks/CampaignHandoffTestServices.cs` |
| M | `source/Coop.Tests/Server/Connections/RemoteMovementOwnershipTests.cs` |
| M | `source/Coop.Tests/Server/Services/Session/AuthorityLeaseDistributionHandlerTests.cs` |
| M | `source/Coop.Tests/Server/Services/Session/CampaignAuthoritySyncRelayServerHandlerTests.cs` |
| M | `source/Coop.Tests/Server/Services/Session/CampaignSimulationAvailabilityServerHandlerTests.cs` |
| A | `source/GameInterface.Tests/Services/CampaignAuthority/CampaignAsyncTaskDrainTests.cs` |
| A | `source/GameInterface.Tests/Services/CampaignAuthority/CampaignAuthorityDebugCommandTests.cs` |
| A | `source/GameInterface.Tests/Services/CampaignAuthority/CampaignFrameRoutingTests.cs` |
| A | `source/GameInterface.Tests/Services/CampaignAuthority/CampaignPartyAiRoutingTests.cs` |
| A | `source/GameInterface.Tests/Services/CampaignAuthority/CampaignSchedulerCheckpointServiceTests.cs` |
| A | `source/GameInterface.Tests/Services/Locations/LocationLocalPlayerCommandTests.cs` |
| A | `source/GameInterface.Tests/Services/MapTracks/MapTracksAuthorityTests.cs` |
| M | `source/GameInterface.Tests/Services/MobileParties/MobilePartyAIDisablePatchesTests.cs` |
| A | `source/GameInterface.Tests/Services/PartyComponents/PartyComponentMembershipTests.cs` |
| D | `source/GameInterface.Tests/Services/Time/Patches/MapStatePatchTests.cs` |
| A | `source/GameInterface.Tests/Services/TroopRosters/TroopRosterAuthorityCreationTests.cs` |
| A | `source/GameInterface.Tests/Services/TroopRosters/TroopRosterMigrationRoutingTests.cs` |
| M | `source/GameInterface/GameInterfaceModule.cs` |
| A | `source/GameInterface/Services/CampaignAuthority/CampaignAsyncTaskDrain.cs` |
| A | `source/GameInterface/Services/CampaignAuthority/CampaignAuthorityDebugCommands.cs` |
| A | `source/GameInterface/Services/CampaignAuthority/CampaignObserverFrame.cs` |
| A | `source/GameInterface/Services/CampaignAuthority/CampaignSchedulerCheckpointService.cs` |
| M | `source/GameInterface/Services/CampaignAuthority/CampaignSimulationAuthorityPolicy.cs` |
| M | `source/GameInterface/Services/CampaignService/Patches/CampaignRobustnessPatches.cs` |
| M | `source/GameInterface/Services/Locations/Commands/LocationDebugCommand.cs` |
| M | `source/GameInterface/Services/MapTracks/Patches/MapTracksCampaignBehaviorPatches.cs` |
| M | `source/GameInterface/Services/MobilePartyAIs/Patches/PartiesThinkPatch.cs` |
| M | `source/GameInterface/Services/Party/Commands/PartyCommands.cs` |
| M | `source/GameInterface/Services/PartyComponents/Handlers/PartyComponentHandler.cs` |
| M | `source/GameInterface/Services/Time/Patches/GameStateManagerPatches.cs` |
| M | `source/GameInterface/Services/Time/Patches/MapStatePatch.cs` |
| M | `source/GameInterface/Services/TroopRosters/Handlers/TroopRosterDeltaHandler.cs` |
| M | `source/GameInterface/Services/TroopRosters/Patches/TroopRosterCreateDummyPatch.cs` |
| M | `source/GameInterface/Services/TroopRosters/Patches/TroopRosterOwnerPartyRegistrationPatch.cs` |
