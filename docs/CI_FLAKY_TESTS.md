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
tests with `continue-on-error: true` so that the signal stays visible in
the workflow summary without failing the build.

The workflow also runs tests tagged `[Trait("Category", "RequiresDesktop")]`
in a separate non-blocking informational step with a timeout. These tests
exercise desktop UIAutomation smoke paths and require a reliable interactive
Windows desktop. They pass in a normal developer session, but can hang or
flake on the GitHub Actions Windows runner because the runner desktop and
UIAutomation latency are not deterministic. They stay out of the blocking
coverage pass by design while remaining visible in CI.

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
