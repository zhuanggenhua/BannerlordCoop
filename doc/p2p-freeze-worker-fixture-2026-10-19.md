# Freeze acknowledgement worker fixture

Base `6e254b823fc99067f6259dd0d6264bfbc8aeb435`. Only the existing freeze test fixture and this document change. Production freeze, lease, async drain, transport and checkpoint code remain unchanged.

## Failure evidence and limits

Regression23 reported Coop1441 PASS/196 FAIL, one additional failure in `CurrentAuthorityFreezesBeforeSendingOrderedAcknowledgementAndCanBeCanceled`: its outer frozen assertion was false after2.028s. The original run did not capture the handler's inner exception or worker start state, so its precise historical cause remains unobserved. Do not reclassify it as a proven flaky test or ignore it because a retry passes.

The handler and test Git blobs were byte-identical between user baseline78ac9216 and this task base. Original unmodified focused suite passed9/9. The same Task.Run test with added observation passed9/9; this alone does not explain Regression23.

A separate private console process then occupied its only thread-pool worker and invoked the same instrumented test. Its real GameThread reached async drain, but Task.Run stayed WaitingToRun for the unchanged2000ms deadline. There was no worker-start-before-lease receipt. OutputSink captured `Failed to acknowledge campaign authority freeze44`, inner xUnit assertion `Lease lock blocked the finishing AI worker`, then the outer frozen=false failure. This demonstrates that this fixture can misreport queue starvation as a lease-lock failure. It does not prove the earlier uninstrumented Regression23 had that cause.

Private controlled reproduction logs are retained in ignored task evidence: Task.Run SHA256`5e8e492c3133c39438c199b45e7364d50448f3f1dccebef6fc2217c476f86db2`; independent-worker counterexample SHA256`5f72aa5504b3f62899966053f971276b310ab56f2561c6808ea49dced03b2bc3`. The first forensic runner exits0 because it verified the expected original test failure; its log explicitly says originalAssertionsPassed=False. It is not a passing freeze test.

## Minimal fixture adaptation

The finishing worker now uses Task.Factory.StartNew with LongRunning, CancellationToken.None and TaskScheduler.Default, matching the test's intended independently finishing engine worker. It asserts the worker is neither a thread-pool worker nor GameThread. It still must acquire the real SessionIdentity lease and finish within the original two-second deadline. This removes dependence on unrelated test-runner pool availability; it does not bypass lease locking.

Every original lease/frozen, worker result, coalescer game-thread/lease, checkpoint, flush/ACK ordering, duplicate request and cancellation assertion remains. The handler is untouched. A genuinely blocked or rejected worker still fails the same deadline/result checks. No timeouts increase or assertion reductions occurred.

Timestamped diagnostic receipts distinguish dispatch, actual worker start, lease-held callback, lease completion, wait result/status, coalescer, frozen result and cancellation. A scoped OutputSink callback records full handler exception text; it is always removed in finally, and receipts use a concurrent queue before writing xUnit output. No tests alter process pool settings: pool saturation occurs only inside the isolated ignored forensic executable, restores its pool, and never changes the real game environment.

## Verification

Under the identical occupied pool with availableWorkers=0, the independent thread started outside GameThread, acquired/completed the lease, and all original assertions passed; no handler ERR. Focused freeze suite passed9/9. All51 CampaignAuthority cases passed five separate runs,255/255, including repeated freeze acknowledgement and cancellation paths. Original Regression23 raw log/TRX, original focused9/9, observed Task.Run9/9 and deliberately failing controlled Task.Run evidence are all preserved with SHA256 in the handoff.

No game was deployed or launched. Coordinator integration and complete regression must still restore the exact historical Coop195/E2E10 failure sets; these focused results do not establish that full recovery or native P2P acceptance.
