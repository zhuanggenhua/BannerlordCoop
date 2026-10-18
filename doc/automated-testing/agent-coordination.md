2026-10-18，单总控、多代理工作流。

用户只与当前总控对话。总控在本对话中分配原生子代理、接收结果、处理冲突和向用户汇报。子代理不创建需要用户维护的开发聊天。本机共有四个并发槽位，因此最多同时运行三个工作代理，给总控保留一个槽位。

已经完成的双人大地图 A→B→A、真实酒馆 Mission 交接和全员离开地图暂停恢复是基线。它们的提交、证据和七条工作流记录在 [p2p-workstreams.json](../../tools/coordination/p2p-workstreams.json)。总控先检查当前 HEAD 与这些证据，再选择新工作，不能重新实现已通过的功能，也不能把未覆盖的招募、城镇交互、断线恢复或冷存档恢复算作完成。

七条工作流分别负责战役权威、世界检查点、AI 调度、暂停恢复、E2E、故障测试和独立审查。每次派工还必须指定更窄的文件 scope，宽泛的工作流范围不表示可以同时修改其中所有代码。存在 scope 重叠或依赖的任务排队，互不依赖的任务并行。

原生子代理默认继承总控当前模型及推理设置。用户要求使用相同模型，因此不会为了代码审查另选模型。当前应用的只读会话元数据未暴露实际模型名，不能把全局默认配置当成当前会话模型。独立 CLI 入口要求显式核验的 `verifiedParentModel`，并检查它与传入的 model 一致；缺失或不一致时，在启动进程前拒绝调用。当前环境采用原生子代理，CLI 适配器仅接受运输、事件和进程生命周期自检，不把 fake CLI 测试声称为真实模型推理通过。

选择原生多代理是因为本轮已实际验证创建、发送消息、继续和收口能力，不需要额外安装 SDK。本机 Codex CLI 为 `0.163.0-alpha.10.2`，帮助中提供独立目录、JSONL、结构化结果及精确会话恢复参数。当前 CLI daemon 探测返回 socket 错误，未将它作为依赖，也没有重启桌面服务。官方 SDK 在已检查路径未安装，未为这项工作强制安装依赖或复制认证文件。

官方说明：[子代理及模型继承](https://learn.chatgpt.com/docs/agent-configuration/subagents)、[非交互执行、JSONL 和结果模式](https://learn.chatgpt.com/docs/non-interactive-mode)、[app-server 会话生命周期](https://learn.chatgpt.com/docs/app-server)。帮助存在、初始化成功、模型推理成功是三个不同的验收层次。

每个写入代理拥有一个独立 Git Worktree 和 `codex/` 分支，所有命令显式指定工作目录。总控保留集成检出的写权限，子代理不得在其中编辑文件。游戏引用和旧 NuGet 包缓存可通过 junction 读取，各工作树的 bin/obj、测试进程及源码独立。编译命令携带 `-p:ModName=`，避免项目的自动部署钩子覆盖测试模组。新建工作树前校验绝对目录和分支，已有目录不被覆盖。

`Deploy.targets` 还会识别 linked worktree 的 `.git` 文件并禁用自动部署，因此 worker 即使漏传空 ModName 也不会通过该构建钩子写入共享游戏目录。主检出的既有构建行为保留，总控编译仍显式禁用自动部署，部署单独经过实机锁。四项真实 MSBuild 自检只向临时假游戏目录操作。

任务板保存在 Git common-dir 下的 `codex-coordination`，所有工作树共享同一份登记和证据索引，但不共享源码输出目录。状态更新本身加排他保护。子代理交付使用 [handoff.schema.json](../../tools/coordination/handoff.schema.json)，包含原始 base、最终 commit、实际变更文件、测试命令、退出码、日志路径及 SHA256。提交后不得继续修改已交付工作树；需要修复时由总控重新分派。

总控检查提交的祖先关系、实际 Git diff、scope、工作树是否干净，以及测试日志内容和哈希。独立审查通过后才能 cherry-pick 到协调分支，集成后运行实际测试并采集新日志。发生冲突就记录为待修复，由总控或指定修复代理处理。不能仅依据子代理的“完成”、退出码零或一份结果文件把功能标记为验收通过。实机证据缺失时保留未验收状态。

实机环境是串行资源。部署和完整 MCP 场景都通过同一包装器执行：

```powershell
node tools\coordination\with-game-lock.mjs --repo . -- node .mcp-local\map-run-16.mjs
node tools\coordination\with-game-lock.mjs --repo . -- powershell.exe -NoProfile -File .mcp-local\deploy-authority-build.ps1
```

上述是对已有脚本的调用方式，不能为了基础设施自检就执行这两条实机命令。包装器根据 `mb2` 的物理安装目录计算资源身份，所有别名工作树和指向同一游戏安装的独立检出竞争同一把锁。锁覆盖整个子进程生命周期；MCP 脚本必须先 `stop_run` 并确认游戏进程树清理，再结束并释放锁。活 owner、身份不明的 PID、残留游戏和未知清理状态不会因为超时被当成空闲。恢复失效锁需要核对 owner、token 和实际进程状态。

这是编排入口的机械互斥，不能阻止人在入口外直接启动 exe；总控和所有子代理均不得绕过入口。自检使用临时 Node 进程竞争同一资源，不启动游戏或覆盖部署。实机测试结果仍由原有 Coop MCP 工具、原生日志和双端状态判断。

独立 CLI 适配器使用参数数组和 stdin 传递提示，不拼接 shell 命令。保存 thread ID、PID、JSONL、最后结果和终态。恢复必须指定确切会话 ID，不使用 `--last`。`turn.failed`、error、缺失 `turn.completed`、不完整交付、超时或取消均不会算作成功。停止操作只针对该适配器创建的进程树，不能结束共享桌面服务或其他用户任务。

每个功能的顺序为子代理测试与提交、总控审查、集成、验证和证据采集，再启动下一项相关工作。已验证的子任务收口，失效任务中断。只清理本工具登记创建且已验证集成的干净工作树和分支，保留既有实验、未审提交和失败现场的可追溯证据。当前流程不创建 PR，也不推送；需要发布时再按会话授权执行。

总控可运行基础设施自检：

```powershell
node tools\coordination\self-check.mjs
node tools\coordination\orchestrator.mjs --help
node tools\coordination\orchestrator.mjs --root . status
node tools\coordination\orchestrator.mjs --root . next
```

自检检查已验收基线、七条工作流及模型策略，然后实际运行任务板、锁、会话适配器和部署保护测试。报告和逐项 TAP 日志位于 Git common-dir 的 `codex-coordination/self-check`，源码中不保存机器状态。该自检不代替新的骑砍实机验收。

原生调度由活动的总控对话执行，任务板允许下次从准确状态继续。它不伪装成关闭聊天后仍无限运行的后台服务。已核验父模型后才可启用独立 CLI 长任务；用户始终只需要给总控下达任务，不需要管理子代理聊天。
