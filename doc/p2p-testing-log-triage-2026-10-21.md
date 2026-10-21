# Native failure triage, 2026-10-21

This is an offline evidence adapter, not a new native harness or an acceptance test.

Before implementation, the four core-change checks were recorded:

1. Existing native scenarios already archive raw JSONL, assembled actor logs, summaries, screenshots and SHA256 receipts. `p2p-udp-reliability.mjs::logDiagnostics` defines actual error-level classification.
2. An external read-only adapter suffices. No production, network, movement, AI, Registry, serializer or scenario assertion changes are needed. Map05's 117 MB JSONL and interleaved DEBUG stack traces make whole-file console inspection unreliable.
3. Preserve original bytes, line numbers, feature verdicts, original FAIL, run/build/PID provenance and cleanup receipts. An error label does not establish its root cause. A DEBUG stack is not an error. The first error's stack ends at the next log header.
4. Test real-level classification, entry boundaries, context, partial/malformed sources, metadata and cleanup provenance, source hashes, existing-output refusal and bounded-memory scanning. Reprocess the already completed map05 evidence, without running a game.

## Interface

`node tools/mcp/scenarios/native-failure-triage.mjs --repo <evidence checkout> --record <JSONL relative to repo, or absolute> --out <new directory>`

All relative evidence paths resolve against `--repo`, regardless of the tool's worktree. The output directory must not exist. No source is opened for writing. The only outputs are `report.json` and `report.md`, both exclusively created. The tool uses existing `logDiagnostics` for error classification. It scans every actor log, counts all error entries and records bounded exact signature groups, first-error context and preceding handoff/navigation/lease headers. AI quarantine warnings are separate.

Each original raw/summary/log/screenshot is stream-hashed before analysis and again before reporting. Stat changes, hash changes, missing files, malformed/oversized records, partial final lines and receipt disagreements are explicit incompleteness issues. JSONL lines and summary size have fixed caps; retained log lines, groups and context are bounded. Full original errors remain in hashed sources; bounded text includes its original byte length and truncation marker. The report never changes the original native verdict. Exit 0 means a complete diagnostic artifact, even when the recorded native verdict is FAIL; exit 2 means an incomplete diagnostic artifact; exit 1 means argument/output failure.

Performance numbers are elapsed milliseconds and sampled V8 heap high-water mark, not native gameplay performance or an OS peak-memory guarantee.

## Recorded validation

The added tests and the unchanged native-log-archive/UDP-reliability tests passed together: 37/37. This includes a generated log over 96 MiB scanned by a child process with `--max-old-space-size=64`, and a concurrently appended source that must be reported INCOMPLETE. No game was launched.

The completed map05 run `59f762d5c7884ffaa42557f874443195` was reprocessed read-only. Its recorded runtime remains `9001867decee7c22814c879486a649a976956a87`, A PID 23140, B PID 5452, overall native FAIL and cleanup confirmed. The adapter found A 0 / B 1020 error entries. B's first error is physical line 61655; its own continuation is empty. The retained prior timeline includes barrier application at 61649 and B's epoch5 lease at 61653. This establishes order, not a root cause. Join, transparent movement and one-way 50 ms remain recorded PASS; A-to-B remains recorded FAIL.

The sealed analysis took 1322 ms with sampled V8 heap high-water 23,964,208 bytes. Raw JSONL, summary, both logs and all 12 screenshots were hashed before and after analysis and matched the prior RCA manifest. These are offline diagnostic measurements, not an end-to-end game latency or native performance claim. The raw JSONL SHA remains `ef3c4efd08502343d074d7021740d365f92f65976c3db4565e4f13add3a50df3`; B log SHA remains `b1e423bfcc53cf5eb107a8a490bb05973456f56419bec5edf4223d7a5401f796`.
