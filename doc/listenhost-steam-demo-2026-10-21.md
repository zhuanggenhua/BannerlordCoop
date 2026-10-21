# ListenHost Steam 演示包：启动、身份与诊断

## 修改依据

1. 原 `ControllerIdProvider` 负责 DEBUG 显式身份覆盖与平台身份。无 `/platformId` 时 `FindIndex` 返回 -1，随后误取 argv[0]，产生可执行路径身份。此处必须修复解析入口，无法由网络适配层修复已经错误注册的身份；保持显式合法测试身份，缺省使用真实平台身份，无效参数和缺失平台身份明确拒绝。回归覆盖带空格可执行路径、大小写、引号、缺参、重复参数、空值和非法路径。
2. 原 Esc Steam 邀请入口以 `IsServer` 排除所有房主。改为现有 Dedicated 角色排除，其余仍受原 `ISessionAdvertiser.CanInviteFriends` 控制；不修改大厅、隧道或同步算法。回归覆盖 Listen、Remote、Dedicated 和无邀请能力分支。
3. 启动包沿用原主菜单 Host Co-op Sandbox 新建流程，只提供 `/listenhost` 或 `/client`，不创建新启动协议，不带旧 LocalA 存档。显式续档只加载用户提供的同身份存档，不改 JSON。
4. 中文诊断采用按需导出适配层，保留原始日志与 SHA，不修改日志接收与原异常。运行中按每文件打开时长度有限复制，保留采集前后元数据并标记非原子快照。版本/MVID 来自包清单，PID 为采集时观察值，不混称实际加载 DLL 的验证结果。

## 验证边界

工作树只进行自动化测试和假目录脚本测试，不部署、不启动游戏。Steam 邀请、跨网连通、真实 Steam 身份注册、加入后动态权威迁移仍须由总控在最终集成版本实机验收。历史 FAIL 不因本入口修复变为 PASS。

`tools/demo` 为可版本化模板，打包时复制到新交付目录并生成 manifest.json：`codeCommit`、`gameVersion`、`requiredCampaignDllSha256`、`actualModuleIdentities[{file,sha256,mvid}]`、`files[{path,sha256}]`。所有运行脚本及 Mod 文件均应列入 files（manifest 自身除外）。旧已封存 7d5edd42 ZIP 和安装不由该任务修改。
