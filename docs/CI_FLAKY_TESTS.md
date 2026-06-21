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

| Test class | File |
|---|---|
| `OpenSshPipeAgentTests` (3 of 4 tests) | `tests/Heimdall.Ssh.Tests/OpenSshPipeAgentTests.cs` |
| `HmacGeneratorSmokeTests` (whole class) | `tests/Heimdall.App.UiTests/Pilots/HmacGeneratorSmokeTests.cs` |
| `TextDiffSmokeTests` (whole class) | `tests/Heimdall.App.UiTests/Pilots/TextDiffSmokeTests.cs` |
| `HashGeneratorSmokeTests` (whole class) | `tests/Heimdall.App.UiTests/Pilots/HashGeneratorSmokeTests.cs` |
| `DnsLookupViewModelTests.CancelCommand_UserCancellation_ClearsStatusWithoutError` | `tests/Heimdall.App.Tests/DnsLookupViewModelTests.cs` |
| `WhoisLookupViewModelTests.CancelCommand_UserCancellation_ClearsStatusWithoutError` | `tests/Heimdall.App.Tests/WhoisLookupViewModelTests.cs` |
| `ConPtySessionTests.StartAsync_LaunchesShell_DeliversInitialTerminalOutput` | `tests/Heimdall.Terminal.Tests/ConPtySessionTests.cs` |
| `TcpPingViewModelTests.StartCommand_MixedResults_PreservesFailedLineAndSummary` | `tests/Heimdall.App.Tests/TcpPingViewModelTests.cs` |

`OpenSshPipeAgentTests.IsAvailable_NoServer_ReturnsFalse` is intentionally
NOT tagged: it is a negative-path test that asserts a 25 ms
availability probe fires when no server is listening, and that path is
not affected by the runner latency.

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
