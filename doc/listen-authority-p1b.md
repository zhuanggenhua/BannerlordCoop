# P1-B: remote campaign player (in progress)

Baseline: `91818b35ff2891fe608931a06107164ae6597f9c`, verified P1-A.
P1-B is not accepted until the two-process live checks below pass.

## Work and acceptance ledger

- [x] Fresh Coop baseline: 1270 pass / 195 fail, `artifacts/p1b/tests/core-baseline.trx`.
- [ ] Restore direct-IP admission, protect the local actor identity, validate remote movement ownership.
- [ ] Verify snapshot/queue catch-up and A/B actor registration; fix actual registry blockers.
- [ ] Compare time, player identity, party IDs, positions, troops, gold and registration counts on both nodes.
- [ ] Live: join, both move, NPC simulation, recruitment and economy.
- [ ] Live: disconnect/rejoin, save/cold restore, sustained run; compare errors and IDs.
- [ ] Final suites and exact failed-test-name comparison, review, commits and evidence index.

Keep the single Campaign, native A actor and authority composition. No local client loopback,
Mission work or authority migration. Live runs use the repository MCP and must finish with `stop_run`.
