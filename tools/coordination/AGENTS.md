# Coordinator tooling

This directory implements the user's authorized single-controller workflow. Keep game code out of infrastructure tasks.

- The root chat is the only user-facing coordinator. Native workers inherit its model by default. A nondefault native model requires an explicit user choice, validated config and task snapshot before assignment; existing tasks keep their recorded policy. CLI workers use the inherited model. An independent CLI inference requires an explicitly verified parent model, never a guessed/default model.
- A writing worker owns exactly one registered task worktree and branch. Use explicit absolute cwd for shell commands and absolute paths for edits. Do not edit the coordinator checkout or another worker's tree.
- Commit each verified change and hand off the base commit, final commit, changed paths, test commands, exit codes, log paths and SHA256 values using handoff.schema.json. A successful process exit alone is not completed acceptance.
- Never deploy, launch games or run a live MCP scenario from a worker checkout. The coordinator uses the game-lock wrapper around deployment and the entire MCP run, including stop_run cleanup. Do not bypass it by calling the live executable directly.
- Do not push, open draft PRs, merge development or remove pre-existing branches/worktrees. Only the coordinator integrates reviewed commits and removes clean worktrees that its own registry created, after their results are retained.
- Tests use Node's built-in node:test runner, no npm installation is required. Keep execution logs and task state in the Git common directory, outside tracked source.
- Do not import credentials or change global Codex settings. Treat CLI/app-server errors as capability evidence, not permission to restart the desktop service.
