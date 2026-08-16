<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

# CI flaky tests - `Category=CIUnstable`

A small set of tests that are stable on developer machines turn red
intermittently on the GitHub Actions Windows runner. They are tagged
`[Trait("Category", "CIUnstable")]` and excluded from the bloquant CI
test pass via `dotnet test --filter "Category!=CIUnstable"`.

A second non-blocking step in `.github/workflows/ci.yml` runs the same
tests with `continue-on-error: true` so the lane keeps running without
failing the build. Read its result from the raw log, not from the step or
check status: see *Reading an informational lane's real result* below.

The workflow also runs tests tagged `[Trait("Category", "RequiresDesktop")]`
in a separate non-blocking informational step with a timeout. These tests
exercise desktop UIAutomation smoke paths and require a reliable interactive
Windows desktop. They pass in a normal developer session, but can hang or
flake on the GitHub Actions Windows runner because the runner desktop and
UIAutomation latency are not deterministic. They stay out of the blocking
coverage pass by design, and their result is read the same way.

## Reading an informational lane's real result

`continue-on-error: true` lets the job and the commit check rollup stay
successful when the lane's command exits non-zero. The consequence to
internalise:

**A machine-readable step conclusion of `success` does not prove that the
underlying command exited successfully.**

On a run where the `RequiresDesktop` lane really failed, every one of these
reported success: the step's `conclusion` in `gh run view --json jobs`, the
job's `conclusion`, the run's `conclusion`, the commit check-run, `gh pr
checks`, and the pull request's `statusCheckRollup`.

The failure is not erased. An error annotation or a red log line may still be
visible in some GitHub views: the run carries one annotation at
`annotation_level: "failure"`, which `gh run view` prints without `--log`.
But that annotation reads only `Process completed with exit code 1.`, is
attributed to the job rather than to the step, and carries an empty `title`
and `raw_details`. It names neither the lane, nor the failing test, nor the
counts. With three `continue-on-error` steps in the workflow, it does not say
which one failed.

So the verdict for an informational lane has to be derived from the raw log,
its test totals and the process exit marker:

```bash
gh run view <run-id> --repo VBlackJack/Heimdall --log
```

Markers to look for:

- `Test Run Failed.` - the lane's test run itself, as opposed to the step status.
- The `Total tests: / Passed: / Failed:` triple that follows it, which gives the
  counts the annotation omits.
- `##[error]Process completed with exit code 1.` - the non-zero exit that
  `continue-on-error` absorbed.

Do **not** filter that log by its step-name column. `gh` renders every line of
some runs with `UNKNOWN STEP` in that column: run `31967767508` shows it on all
10533 lines, while runs `31971172553` and `31972881070` do not, and the trigger
is unidentified. The underlying API data is intact (`--json jobs` returns all
20 steps for the same run), so this is a rendering defect, not missing data.
Bound a lane positionally instead, between its own `##[group]Run dotnet test
... --filter "Category=RequiresDesktop"` header and the next step's
`##[group]Run` header.

### A worked example, and what it does and does not prove

Two runs were read this way while delivering PR #140:

| Run | Event | Head SHA | `RequiresDesktop` lane |
|---|---|---|---|
| `31967767508` | push | `748508c0` | 105 total / 104 passed / 1 failed |
| `31971172553` | pull_request | `b6e04d32` | 105 total / 104 passed / 1 failed |

Both failed on the same test,
`SessionTreeSelectionAutomationTests.SessionTree_MultiSelection_IsVisibleThroughRealUiAutomation`.
The stack frame points at `SessionTreeSelectionAutomationTests.cs:297`, which is
the failing assertion; the test method itself is declared at line 272.

`748508c0` is the parent of the commit proposed by PR #140, so the pair
establishes that this failure predates that pull request. That is the whole of
what it establishes. It is a bounded observation about two runs, not a standing
guarantee: a later run showing the same lane red must be measured again before
it is called pre-existing, and it says nothing about whether the failure is
intermittent or permanent.

## Why these tests are flaky on the runner only

Four distinct root causes share the same symptom (`TaskCanceledException`,
`OperationCanceledException`, or `WaitUntil` timeouts):

1. **Named-pipe handshake latency** - `OpenSshPipeAgentTests` create a
   per-test named pipe and race a server-side `WaitForConnectionAsync`
   against the client-side connect. On the GitHub Actions runner, the
   handshake routinely exceeds 10 seconds even with generous
   `availabilityTimeoutMs` and server-side
   `CancellationTokenSource(TimeSpan.FromSeconds(10))`. Suspect: Defender
   / runner I/O contention scanning the pipe.
2. **WPF + UIAutomation binding propagation latency** - `Pilots/*SmokeTests`
   wait for a value to propagate through a `Binding` / `INotifyPropertyChanged`
   chain. On a slow runner, the propagation outlasts even a 10-second
   `WaitHelpers.DefaultTimeout`. Bumping the timeout further only delays
   the failure window and slows down genuinely hung tests.
3. **ConPTY process startup race** - `ConPtySessionTests` start
   `powershell.exe -NoLogo -NoProfile` inside a pseudo-console and assert
   `IsRunning` immediately after the first `DataReceived` callback fires. On
   a slow runner, PowerShell can print its banner and exit (or the ConPTY
   attachment can drop) before the assert reads `IsRunning`, causing the
   check to fail. The `NotEmpty(text)` assertion that precedes it still
   covers the core contract (ConPTY delivers output); the lifecycle property
   is independently exercised by `Dispose_TerminatesPseudoConsoleAndProcess`.
4. **ViewModel polling timeout** - `TcpPingViewModelTests` and similar
   ViewModel tests use a file-local `WaitUntilAsync(condition, timeoutMs)`
   helper to observe property/collection updates that happen on background
   tasks. On a busy GitHub Actions Windows runner the polled condition can
   take longer than the test's timeout to become true (the wait loop polls
   every 10 ms and throws `TimeoutException` past the deadline). Bumping the
   timeout further only delays the failure window without eliminating it.

## Currently tagged `CIUnstable`

There are 11 `[Trait("Category", "CIUnstable")]` sites in the solution: 8 on
individual test methods and 3 on whole classes. Every site is listed below.

| Test | File | Categories |
|---|---|---|
| `OpenSshPipeAgentTests.GetIdentities_ReadsResponseFromNamedPipe` | `tests/Heimdall.Ssh.Tests/OpenSshPipeAgentTests.cs` | `CIUnstable` |
| `OpenSshPipeAgentTests.GetIdentities_WhenPipeClosesAfterConnect_ReturnsEmpty` | `tests/Heimdall.Ssh.Tests/OpenSshPipeAgentTests.cs` | `CIUnstable` |
| `OpenSshPipeAgentTests.AgentKeySign_SendsFlagsAndReturnsSignature` | `tests/Heimdall.Ssh.Tests/OpenSshPipeAgentTests.cs` | `CIUnstable` |
| `ConPtySessionTests.StartAsync_LaunchesShell_DeliversInitialTerminalOutput` | `tests/Heimdall.Terminal.Tests/ConPtySessionTests.cs` | `CIUnstable` |
| `ConPtySessionTests.DataReceived_SubscriberAddedAfterBootstrapOutput_ReplaysBufferedOutput` | `tests/Heimdall.Terminal.Tests/ConPtySessionTests.cs` | `CIUnstable` |
| `DnsLookupViewModelTests.CancelCommand_UserCancellation_ClearsStatusWithoutError` | `tests/Heimdall.App.Tests/DnsLookupViewModelTests.cs` | `CIUnstable` |
| `WhoisLookupViewModelTests.CancelCommand_UserCancellation_ClearsStatusWithoutError` | `tests/Heimdall.App.Tests/WhoisLookupViewModelTests.cs` | `CIUnstable` |
| `TcpPingViewModelTests.StartCommand_MixedResults_PreservesFailedLineAndSummary` | `tests/Heimdall.App.Tests/TcpPingViewModelTests.cs` | `CIUnstable` |
| `HmacGeneratorSmokeTests` (class tag, 6 tests) | `tests/Heimdall.App.UiTests/Pilots/HmacGeneratorSmokeTests.cs` | `CIUnstable` + `RequiresDesktop` |
| `TextDiffSmokeTests` (class tag, 7 tests) | `tests/Heimdall.App.UiTests/Pilots/TextDiffSmokeTests.cs` | `CIUnstable` + `RequiresDesktop` |
| `HashGeneratorSmokeTests` (class tag, 5 tests) | `tests/Heimdall.App.UiTests/Pilots/HashGeneratorSmokeTests.cs` | `CIUnstable` + `RequiresDesktop` |

The three smoke classes carry `CIUnstable` at class level while each of their
test methods additionally carries `RequiresDesktop`, so they are excluded by
either half of the blocking filter. The other `Pilots/*SmokeTests` classes carry
only `RequiresDesktop` and are not part of this inventory.

`OpenSshPipeAgentTests.IsAvailable_NoServer_ReturnsFalse` is intentionally
NOT tagged: it is a negative-path test that asserts a 25 ms
availability probe fires when no server is listening, and that path is
not affected by the runner latency.

## Flakiness fixed by rewriting instead of tagging

Not every runner-only failure earns a tag. Two SSH tests failed the blocking
lane through thread-pool starvation on the two-core runner and were repaired
rather than excluded:

- `SshShellSessionTeardownTests.Disconnect_StuckReadLoop_DoesNotBlockCallerForFinalWait`
  waited on `SpinWait.SpinUntil`, a busy-spin that occupied the pool worker
  running the test while the notification it waited for was produced by a
  pool-queued continuation. It now awaits a `TaskCompletionSource`, and
  `SshShellSession` takes an optional `TimeProvider` so the test drives the
  final teardown wait from a `FakeTimeProvider` instead of wall-clock time.
- The three `TunnelManagerTests` lock-contention tests raced a `Task.Run` probe
  against a two-second `Task.Delay`. Winning that race required both that no
  lock was held and that the pool scheduled the probe promptly, so a saturated
  pool produced failures that read as lock contention. Both sides now run on
  dedicated threads and the proof is a `Thread.Join` with a timeout.

Prefer this route when the cause is the test's own scheduling assumptions
rather than genuine environment latency: a tag hides the test, a rewrite keeps
the coverage in the blocking lane.

## Reading terminal wait latency in a CI log

`tests/Heimdall.Terminal.Tests` bounds its child-process waits with a shared
60-second backstop. That value was raised from 10 seconds after a run timed out
with the child still alive and `receivedBytes=0`. Raising it stopped the
timeouts, and stopped the evidence with them: a wait that used to fail at 10
seconds now completes at 45 and the run is green with no trace. A green run
under the wider bound therefore cannot distinguish "the stall is gone" from
"the same stall now finishes inside the wider bound".

Every wait bounded by that backstop is routed through
`TerminalWaitObservation`, which publishes one line to standard output when a
wait **ends** having outlived 10 seconds, **including when it ends by
succeeding**:

```
TERMINAL_WAIT_OVER_LEGACY_BOUND caller=Write_InputReachesProcessStdin awaited=ProcessExited elapsedMs=12500.000 legacyBoundMs=10000.000 outcome=completed
```

The line is emitted from a `finally`, so it marks a wait that has finished, not
the moment the threshold was crossed. A wait still blocked when the job is
killed publishes nothing at all.

Console output from a passing test reaches the `dotnet test --verbosity normal`
log, so these lines accumulate in the workflow log with no collector and no
artifact upload. To read a run:

```bash
gh run view <run-id> --log | grep TERMINAL_WAIT_OVER_LEGACY_BOUND
```

- No lines, **in a run whose test step reached its end**: no wait outlived the
  old bound. Read it only under that condition. In a run killed by the job
  timeout the absence proves nothing, because the wait that was still blocked is
  exactly the one that never got to publish.
- Lines with `outcome=completed`: a wait crossed the former boundary and the
  wider bound absorbed it. The run is green, and that green is not evidence the
  cause is gone. It is also not proof that the same wait would have failed under
  the former implementation.
- Lines with `outcome=unfinished`: the wait ended without its event; the
  accompanying `TimeoutException` carries the full process snapshot.

### First observations, run 31896183632

The first CI run reported four completed waits above the former 10-second
observation threshold. This proves that the 60-second backstop can still absorb
waits which cross the former boundary. It does not replay the
completion-versus-timeout race of the former `WaitAsync(10 seconds)`, so it
cannot establish that every observation would have failed under that
implementation.

| caller | awaited | elapsedMs |
|---|---|---|
| `ProcessExited_ProcessEndsWithoutConsoleOutput_RaisesExitCode` | `ProcessExited` | 10040.230 |
| `PipeModeSession_DataReceivedSubscriberException_DoesNotStopReadLoop` | `ProcessExited` | 10390.218 |
| `ProcessExited_SubscriberAddedAfterFastExit_ReplaysExitCode` | `SessionStopped` | 10547.798 |
| `Write_InputReachesProcessStdin` | `ProcessExited` | 10564.036 |

Only waits above the threshold are reported. This threshold-filtered sample of
four observations is therefore insufficient to distinguish a fixed timer,
scheduling delay, output buffering, or resource contention.

The one further fact worth recording is that these four occurred with
`System.Threading.ThreadPool.MinThreads` at 64.

### Do not reconstruct events from GitHub Actions line timestamps

The timestamp on a workflow log line dates the capture and multiplexing of
standard output, not the call that produced the text. In this very run a
`TERMINAL_WAIT` marker appears concatenated into a line of
`TwinShell.Infrastructure.Tests` output, which shows directly that line
boundaries and line times are not those of the emitting call.

Nothing may be derived from those timestamps: not start instants, not
simultaneous endings, not episodes separated by mechanism, not a contended
resource, and not a named synchronization context as a probable cause. An
earlier reading of this page did exactly that and was wrong.

A future pass that wants to analyse overlap must first emit monotonic start and
end instants at the source, with the process id and a sequence identifier, and
read those.

`TerminalWaitInstrumentationGuardTests` refuses any new wait that reaches the
backstop constant directly, because instrumentation that is bypassed measures
nothing while the suite stays green either way. Use
`TerminalTestHelpers.AwaitProcessEventAsync`, `SpinUntilProcessEvent` or
`PollUntilProcessEventAsync`.

This is measurement, not a fix. It exists so the cause can be found from the
real distribution instead of guessed at.

## Running locally

`Test.bat` and `dotnet test Heimdall.slnx` (without filter) run the full
suite, tagged tests included. Expect them to pass.

To reproduce the CI behavior locally:

```powershell
dotnet test Heimdall.slnx --filter "Category!=CIUnstable&Category!=RequiresDesktop"
dotnet test Heimdall.slnx --filter "Category=CIUnstable"
dotnet test Heimdall.slnx --filter "Category=RequiresDesktop"
```

## When to remove a tag

Lift the `CIUnstable` trait once one of the following is true:

- The runner image (or its Defender exclusion list) is updated and the
  tests pass three CI runs in a row without retries.
- The test is rewritten to no longer depend on cross-process I/O timing
  (e.g. by replacing the named pipe with an in-memory transport, or by
  driving the WPF binding update synchronously from the test thread).
- The test is deleted as obsolete.

Removing the trait without one of those changes will re-introduce the
intermittent CI redness.
