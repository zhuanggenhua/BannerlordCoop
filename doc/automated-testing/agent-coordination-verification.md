2026-10-18，单总控编排环境的原生代理交付核验。

本记录由任务 `orchestration-acceptance` 的原生工作代理编写。任务板实际登记了专属工作树、`codex/agent-orchestration-acceptance` 分支、base `16ac5d6a8823fef9b81ad2738e62f55214102401` 和本文件的唯一写入 scope。代理没有在总控检出或旧 session 工作树中写代码。本任务 `independent=true`，属于基础设施验收，不代替任何玩法任务。

代理在上述 base 实际执行 `node tools/coordination/self-check.mjs`，进程退出码为 0：25 项检查通过，四套测试共 61 项通过、0 项失败。

| 基线测试套件 | 实际通过数 | 直接覆盖的行为 |
| --- | ---: | --- |
| `orchestrator.test.mjs` | 17 | 真实临时 Git 工作树、scope/槽位、handoff、审查和集成门禁、冲突回滚、失败修复链、关闭及清理 |
| `resource-lock.test.mjs` | 12 | 多进程竞争、owner/token、失效锁显式恢复、物理游戏目录别名、超时和中断、遗留后代保留锁 |
| `codex-session.test.mjs` | 28 | 实际 spawn 的 Node fake CLI：参数、模型拒绝、精确会话 ID、JSONL/schema、失败/取消/超时和后代进程清理 |
| `deployment-guard.test.mjs` | 4 | 真实 MSBuild 对临时假游戏目录的部署条件；linked worktree 禁部署、主检出行为、空 ModName 和缺失 Modules |

证据保存在 Git common-dir，以下均为已经存在的文件，不是预计的后续输出：

- `codex-coordination/self-check/1792318183865-6a10cde3/report.json`：本次 manifest，包含 HEAD、四套真实命令、退出码、逐套日志及 SHA256。manifest SHA256 为 `9e2dd1985e7bb06dec906f3de29ba8767402b063b0cf72bea5cfd062c0891733`。
- 同目录的 `orchestrator.tap.log`、`resource-lock.tap.log`、`codex-session.tap.log`、`deployment-guard.tap.log`：四套测试的实际 TAP 输出。
- `codex-coordination/native-acceptance/self-check-baseline.log`：外层实际命令输出，SHA256 为 `f221da1c2d02a915b720aa3770138a4383462a6cba146cf73229078cbc7b8bc4`。
- `codex-coordination/native-acceptance/board-state-before-handoff.json` 和 `workflow-queue-before-handoff.json`：交付前的真实任务登记与七条队列快照。

本代理独立审查了 [任务板](../../tools/coordination/orchestrator.mjs)、[资源锁](../../tools/coordination/resource-lock.mjs)、[实机包装器](../../tools/coordination/with-game-lock.mjs) 和 [部署目标](../../Deploy.targets)，并对照上述测试。未发现需要扩大本任务 scope 才能处理的阻塞问题。

随后通过只读 `git show` 审查了作者的短锁增量 `2f9175004c34e99baa54321f41477fc165a1c451`，没有将其搬入本工作树。新实现只在登记和收取验证结果时持 state lock；实际测试在锁外执行，以 verification token 和原始 HEAD 验收结果。验证期间其他进程仍能 status/create/assign，但不能再次 verify 或 integrate 改变总控 HEAD；结果返回时 token、任务状态、HEAD 或工作树不符均不记为验证通过。

该增量作者的 `codex-coordination/bootstrap-evidence/board-lease-19.log` 实际记录 19/19，通过日志与交付 SHA256 `71baaafcd44fc7d43f7d881d77a3502a9fc8ebf62ee90c4aec604ba8cfe8fd70` 一致。本代理只读审了增量与两项新增真实进程测试，没有在本工作树运行新版本，因此不能把作者的 19 项说成本代理已跑。静态审查输出另存 `codex-coordination/native-acceptance/verification-lease-static-review.log`。总控集成后还必须在最终 HEAD 重跑完整自检；新组合应包含 19 + 12 + 28 + 4 = 63 项，其是否通过取决于新的实际报告，不能用本次基线 61 项替代。

任务板核对的是实际 worker HEAD、base 祖先关系、Git diff、scope、工作树清洁状态和测试日志哈希。独立 reviewer 不能使用 worker 自己的 agent ID；审查通过后才能集成。集成后的失败测试进入 `needs_fix`，通过新 follow-up 工作树修复，保留原始交付及失败记录。`close` 要求当前总控 HEAD 的成功验证和未变化的日志；`cleanup` 仅移除本工具登记、已关闭且仍匹配交付提交的干净工作树/分支。临时 Git fixture 已实际覆盖整条机械流程；本次真实原生任务的后续 gate 仍由总控执行。

恢复验证租约有一项需要总控执行的前置条件：`recover-verification` 只机械确认 owner PID 已死，不证明实际测试命令及其后代已经停止。新增 abandoned 测试在 recover 前由 fixture 明确清理了 commandPid；它没有证明所有故障都可自动恢复。总控必须先检查、清理旧验收命令及其进程树并记录证据，再恢复任务板；不得仅据 owner dead 重新运行测试。无法确认时保留未决状态，不恢复、不宣称完成。恢复本身把结果记为未知并进入 `needs_fix`，不会推导通过。真实部署/MCP 仍另受物理安装锁和残留游戏检查保护。

实机锁按 `mb2` 的物理目录计算资源身份，覆盖部署或完整 MCP 脚本的生命周期。活 owner、未知身份、token 变化、残留游戏或后代进程都不会被当成空闲；正常父进程退出后仍有 detached 后代时，包装器保留 `recovery_required`，没有误放锁。只读测试也核对了锁路径替换时不会递归删除无关目录。锁是规定入口的机械互斥，不能阻止人在入口外直接启动游戏，代理必须遵守入口约束。

部署保护识别 linked worktree 的 `.git` 文件并跳过 `DeployToGame`。总控的编译验证另外要求 `-p:ModName=`；部署应单独走实机锁。四项 MSBuild fixture 只写临时假安装目录，没有覆盖本机测试模组，也没有编译或启动骑砍。

本代理是 session 适配器的原作者，因此本次运行该套测试和检查接口边界属于回归核对，不能充当该模块的独立代码审查。该模块仍须采用总控或另一位非作者的审查结论。

模型策略有明确边界：原生代理由总控使用继承模型的工具启动，本任务没有模型 override。公开会话元数据没有暴露活动模型名称，不能拿全局默认配置当作实证。CLI 缺少显式已核验的 `verifiedParentModel`，或传入的 model 不完全相等时，启动前拒绝；默认显式只读，写任务另需匹配实际工作树的 Git common-dir。CLI 的 fake 运输测试和 `codex --version` 成功不等于真实推理、精确恢复或 Windows 写沙箱已验证。本轮没有调用真实 CLI 模型、重启桌面 daemon 或修改全局设置。

交付前的实际队列如下。基础设施自检和本次原生验收均不会把这些 next 标记为完成：

| 工作流 | 尚待实施或验收的 next |
| --- | --- |
| authority | 调查迁移期间招募和城镇操作的传输身份耦合 |
| checkpoint | 验证动态权威下冷存档重启的连续性 |
| ai | 清点仍按传输角色限制的 AI 和经济路径 |
| pause | 使有效租约暂停状态在 UI 中可见 |
| e2e | 为下一项独立功能建立前后世界状态对比 |
| fault | 按断线、重复消息和延迟 ACK 分别建立故障证据 |
| review | 逐项复核子代理提交及证据后允许集成 |

自检读取并校验了既有 A→B→A、真实酒馆 Mission、全员离图暂停恢复的验收记录及旧失败名单对比，未重新运行这些骑砍场景。本次 61 项通过仅说明所列编排基础设施测试通过，不改变 [已有玩法验收边界](../p2p-authority-live-acceptance-2026-10-18.md)，也不能宣称整个游戏测试全绿。

本文件交付时，真实原生任务仍等待总控校验 handoff、非作者审查、cherry-pick、集成 HEAD 上的实际测试，以及关闭和清理。只有这些 gate 的后续真实记录可以证明完整收口；本代理不自行集成、关闭或删除工作树。Node 任务板持久化状态，原生派工与中断由活动总控执行，并未伪装成关闭聊天后仍无限运行的后台服务。
