# ListenHost Steam 好友连接适配（2026-10-21）

本次仅恢复单进程房主的原有 Steam 好友大厅、邀请及中继连接入口，不改变动态 Campaign 权威或 Mission 算法。真正不同网络双人加入尚需实机验收。

## 修改前四项依据

1. 原项目已经提供 SteamBoot 用户初始化、SteamLobbyAdvertiser 好友大厅与 rich presence、SteamNetworkingTunnelTransport、SteamTunnelHost 数据报转发及远端 Steam 身份解析。独立服务器使用另一套 anonymous game-server 初始化和发布生命周期。
2. ListenHost 原先跳过 Steam 初始化，ServerModule 明确选择 Noop 服务。只需改变这两个组合入口，再增加随本地房间 ServerListening 启动的薄适配。无需改写原网络、Registry、AutoSync、移动及战斗实现。
3. 必须保留唯一原生 Campaign 和本地玩家、真实远端 NetPeer、一份 SteamTunnelHost 身份映射。房间发布不依赖当前 Campaign 权威；权威 Epoch 交接不能关闭房间中继。禁止额外 CoopClient 或额外游戏进程。
4. 回归覆盖启动顺序、重复启动、房主人数、canonical lobby 推送、新玩家加入、失败撤销、异步回调和销毁、非 Steam Direct IP 组合，以及原大厅、中继、成员服务、服务器 bind 与容器测试。

## 生命周期与实现

- `CoopMod.OnApplicationTick`：ListenHost 使用 `SteamIntegrationBoot.TryStartWithCallbackPump(false, ...)` 的用户身份路径。保留 DEBUG live-test 禁用 Steam 的原有分支，不新增第二个回调 pump。
- `InitialServerState.Handle_CampaignReady` 先注册原生本地玩家，再切换 `ServerRunningState`；后者启动 UDP 并在绑定成功后发布 `ServerListening`。
- `ServerModule.RegisterListenSteamSessionServices` 组合用户 Steam 大厅、中继、Mission bridge、`ConfiguredSessionJoinInfoSource`；同一 `SteamTunnelHost` 同时提供 `ISessionTunnelHost` 和 `ISessionTunnelIdentityResolver`。
- `ListenSessionAdvertisementHandler` 仅在 `ServerListening` 后先启动中继，再调用原好友大厅发布；不等待独立服务器登录，不订阅客户端 `NetworkConnected`。
- 已连接人数来自远端连接集合，加唯一房主本地玩家一次；房间 ID 变更广播，晚加入玩家收到当前房间 ID。
- `ListenSteamLobbyAdvertiser` 仅复用原有失败 hook 增加中文状态。大厅创建或元数据更新失败保留原错误日志；原大厅撤销，不断开已经建立的游戏中继。大厅成功回调前不宣称好友房间就绪。
- 会话销毁退订并关闭大厅及中继。已排队的启动和迟到回调不能复活旧会话。
- Steam 不可用仍选择 Noop 发布服务，Direct IP 主体保持可用；Dedicated 模式保持原 anonymous Steam 组合。

## 使用边界

DEBUG 包通过 `/listenhost` 启动，不使用 `/autoconnect`、`/platformId LocalA` 或旧示例 `/coopsave`。主菜单选择 `Host Co-op Sandbox` 创建新的原生战役；玩家真实 Steam 身份正常保留。旧示例存档绑定 LocalA，不应通过删除元数据或伪造身份强行复用。

进入大地图后应出现中文“Steam 好友房间已就绪”。通过原邀请入口或 Steam 好友列表加入。不同网络的登录、好友邀请、加入和 Mission 中继仍需两台独立机器实测；单机自动测试不替代外网验收。

## 验证状态

- 初始测试编译曾因新增测试缺少 NetworkConfig namespace 引入失败，原 tests-01.log 保留；修复为增加正确 using，没有改断言。
- tests-02：63 PASS，0 FAIL。
- tests-03：173 PASS，0 FAIL（其中新增 14 项）。此为相关测试集，不是整个 Coop 测试全绿。
- tests-04 / tests-05：177 PASS，0 FAIL（其中新增 18 项）；增加迟到 callback、排队销毁和元数据失败保持中继测试。tests-05 使用明确调用序列记录核对先监听、再发布。最终日志和 handoff.json 位于 artifacts/listen-steam-connect-m24。
- `dotnet build source/Coop/Coop.csproj -c Debug -t:Rebuild --no-restore -p:ModName= -p:NuGetAudit=false --verbosity quiet`：退出码 0，60 项警告、0 错误，完整原始日志保留；未声称已有警告全部解决。
- Worker 所有构建均 `-p:ModName=`，未部署、未重启或操作用户游戏。外网 Steam 和最终游戏版本实测未验收。
