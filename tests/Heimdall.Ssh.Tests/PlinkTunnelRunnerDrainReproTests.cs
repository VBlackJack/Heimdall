/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Diagnostics;
using Heimdall.Core.Network;
using Heimdall.Ssh.Plink;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Pins finding C-04 of the SSH audit of 2026-09-06 against a real child process, so the
/// stderr pipe is the one <see cref="Process"/> hands out and not a stand-in: the stderr
/// drain must outlive the caller's connect token. Linked to it, the drain stopped reading
/// once the token was cancelled after the tunnel was up, and a chatty plink then blocked
/// on its next stderr line as soon as the pipe buffer was full, which silently ended the
/// forwarding. (The companion finding C-05, a Stop that would pay the drain join timeout,
/// was refuted on the same child: Stop takes about 50 ms, the pipe read observes cancellation.)
/// </summary>
/// <remarks>
/// The child is <c>cmd.exe /K</c> with its input redirected: the test types commands
/// into it, and <c>echo ... 1&gt;&amp;2</c> is how it is made to write to stderr. A child
/// blocked on a full stderr pipe never reads its next command, so "does it process
/// <c>exit</c>" is the observable.
/// </remarks>
public sealed class PlinkTunnelRunnerDrainReproTests
{
    private static readonly TimeSpan ExitVerdictWindow = TimeSpan.FromSeconds(4);
    private const int BurstLines = 200;

    [Fact]
    public async Task C04_Control_WhileTheConnectTokenIsLive_AStderrBurstIsDrainedAndTheChildKeepsRunningCommands()
    {
        using CommandProcessorStandin child = new();
        using CancellationTokenSource connectToken = new();
        using PlinkTunnelRunner runner = new(
            new PlinkTunnelRunnerOptions(1, 2000),
            new OwnedProbe(),
            _ => child);

        PlinkTunnelResult result = await runner.StartAsync(
            CommandProcessorPath,
            "gw.test", 22, "user", null, null,
            "remote", 22, 45150, "SHA256:test",
            connectToken.Token);
        Assert.True(result.Success, result.ErrorMessage);

        child.WriteStderrBurst(BurstLines);
        child.TypeExit();

        Assert.True(
            child.WaitForExit(ExitVerdictWindow),
            "control failed: the child did not reach its exit command although the drain was live");
    }

    [Fact]
    public async Task C04_CancellingTheConnectTokenAfterEstablishment_LeavesTheDrainReadingAndTheChildRunning()
    {
        using CommandProcessorStandin child = new();
        using CancellationTokenSource connectToken = new();
        using PlinkTunnelRunner runner = new(
            new PlinkTunnelRunnerOptions(1, 2000),
            new OwnedProbe(),
            _ => child);

        PlinkTunnelResult result = await runner.StartAsync(
            CommandProcessorPath,
            "gw.test", 22, "user", null, null,
            "remote", 22, 45151, "SHA256:test",
            connectToken.Token);
        Assert.True(result.Success, result.ErrorMessage);

        // The caller's connect token is cancelled after the tunnel is up. Nothing about the
        // tunnel changed; the child keeps running and keeps writing diagnostics to stderr.
        connectToken.Cancel();
        await Task.Delay(200);

        child.WriteStderrBurst(BurstLines);
        child.TypeExit();

        bool exited = child.WaitForExit(ExitVerdictWindow);
        Assert.True(
            exited,
            "after the connect token was cancelled the child never reached its exit command: "
            + "it is blocked writing to a stderr pipe nobody drains any more");
    }

    private static string CommandProcessorPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "cmd.exe");

    private sealed class OwnedProbe : ITcpListenerOwnershipProbe
    {
        public TcpListenerOwnership Probe(string bindHost, int port, int expectedProcessId)
            => TcpListenerOwnership.OwnedByExpectedProcess;
    }

    /// <summary>
    /// A real <c>cmd.exe /K</c> presented to the runner as the plink process. The runner
    /// reads the process's own stderr pipe; the test drives the child through its stdin.
    /// </summary>
    private sealed class CommandProcessorStandin : IPlinkProcess
    {
        private static readonly string Line = new string('x', 100);

        private readonly Process _process = new()
        {
            StartInfo = new ProcessStartInfo(CommandProcessorPath, "/K")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            },
            EnableRaisingEvents = true
        };

        public event EventHandler? Exited
        {
            add => _process.Exited += value;
            remove => _process.Exited -= value;
        }

        public int Id => _process.Id;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public StreamReader StandardError => _process.StandardError;

        public bool Start()
        {
            bool started = _process.Start();
            // Stdout is never read by the runner; discard it so it can never be the pipe that blocks.
            _process.BeginOutputReadLine();
            return started;
        }

        public void Kill()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
            => _process.WaitForExitAsync(cancellationToken);

        public bool WaitForExit(TimeSpan window) => _process.WaitForExit((int)window.TotalMilliseconds);

        /// <summary>Makes the child write <paramref name="lines"/> lines of 100 characters to stderr.</summary>
        public void WriteStderrBurst(int lines)
        {
            _process.StandardInput.WriteLine($"for /L %i in (1,1,{lines}) do @echo {Line} 1>&2");
            _process.StandardInput.Flush();
        }

        public void TypeExit()
        {
            _process.StandardInput.WriteLine("exit");
            _process.StandardInput.Flush();
        }

        public void Dispose()
        {
            try
            {
                Kill();
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }
    }
}
