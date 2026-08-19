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

using Heimdall.App.Services.Handlers;
using Heimdall.Terminal;

namespace Heimdall.App.Tests;

/// <summary>
/// SSH-013. The plink password file used to survive until the session ended. It is deleted on the
/// first proof that plink read it, with process exit kept as a backstop.
/// </summary>
/// <remarks>
/// <para>The proof is the first byte plink writes: measured against PuTTY 0.83, <c>-pwfile</c> is
/// read and closed while the command line is parsed, before any network activity, so any output at
/// all comes after the read. These oracles pin the arming contract, not the timing.</para>
/// <para>Deliberately not asserted, because the code does not do it: a session that connects and
/// then stays silent still waits for exit. That gap is why SSH-013 is not closed.</para>
/// </remarks>
public sealed class PlinkPasswordFileReleaseTests
{
    private const string PasswordFile = @"C:\Temp\heimdall-plink-pw.tmp";

    [Fact]
    public void Arm_BeforeAnySignal_DeletesNothing()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];

        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add);

        // Arming alone must not touch the file: plink may not have opened it yet.
        Assert.Empty(deleted);
        Assert.Equal(1, session.DataSubscribers);
        Assert.Equal(1, session.ExitSubscribers);
    }

    [Fact]
    public void FirstByte_DeletesTheFileOnce()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];
        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add);

        session.RaiseData([0x24]);

        Assert.Equal([PasswordFile], deleted);
    }

    [Fact]
    public void FurtherOutputAndExit_DoNotDeleteAgain()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];
        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add);

        session.RaiseData([0x24]);
        session.RaiseData([0x20, 0x0A]);
        session.RaiseExit(0);

        // Exactly once, whatever follows.
        Assert.Equal([PasswordFile], deleted);
    }

    [Fact]
    public void ProcessExitWithoutAnyOutput_StillDeletes()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];
        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add);

        // A session that never printed anything: the backstop is what carries this case, and it is
        // the reason removing the exit subscription would be a regression rather than a cleanup.
        session.RaiseExit(1);

        Assert.Equal([PasswordFile], deleted);
    }

    [Fact]
    public void AfterRelease_NoSubscriptionSurvives()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];
        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add);

        session.RaiseData([0x24]);

        Assert.Equal(0, session.DataSubscribers);
        Assert.Equal(0, session.ExitSubscribers);
    }

    [Fact]
    public async Task DataAndExitRacing_DeleteExactlyOnce()
    {
        FakeTerminalSession session = new();
        System.Collections.Concurrent.ConcurrentQueue<string?> deleted = new();
        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Enqueue);

        using System.Threading.Barrier gate = new(2);
        Task data = Task.Run(() =>
        {
            gate.SignalAndWait();
            session.RaiseData([0x24]);
        });
        Task exit = Task.Run(() =>
        {
            gate.SignalAndWait();
            session.RaiseExit(0);
        });

        await Task.WhenAll(data, exit);

        Assert.Single(deleted);
    }

    [Fact]
    public void Arm_RejectsAMissingSessionOrDeleter()
    {
        FakeTerminalSession session = new();

        Assert.Throws<ArgumentNullException>(
            () => PlinkPasswordFileRelease.Arm(null!, PasswordFile, _ => { }));
        Assert.Throws<ArgumentNullException>(
            () => PlinkPasswordFileRelease.Arm(session, PasswordFile, null!));
    }

    /// <summary>
    /// Counts live subscribers so the oracles can see the unsubscription, which a handler that only
    /// guards on a flag would leave in place.
    /// </summary>
    private sealed class FakeTerminalSession : ITerminalSession
    {
        private readonly object _sync = new();
        private Action<ReadOnlyMemory<byte>>? _data;
        private Action<int>? _exit;

        public event Action<ReadOnlyMemory<byte>>? DataReceived
        {
            add { lock (_sync) { _data += value; } }
            remove { lock (_sync) { _data -= value; } }
        }

        public event Action<int>? ProcessExited
        {
            add { lock (_sync) { _exit += value; } }
            remove { lock (_sync) { _exit -= value; } }
        }

        public int DataSubscribers
        {
            get { lock (_sync) { return _data?.GetInvocationList().Length ?? 0; } }
        }

        public int ExitSubscribers
        {
            get { lock (_sync) { return _exit?.GetInvocationList().Length ?? 0; } }
        }

        public bool IsRunning => true;

        public int? ProcessId => 4242;

        public Dictionary<string, string>? EnvironmentVariables { get; set; }

        public void RaiseData(byte[] chunk)
        {
            Action<ReadOnlyMemory<byte>>? handler;
            lock (_sync)
            {
                handler = _data;
            }

            handler?.Invoke(chunk.AsMemory());
        }

        public void RaiseExit(int exitCode)
        {
            Action<int>? handler;
            lock (_sync)
            {
                handler = _exit;
            }

            handler?.Invoke(exitCode);
        }

        public Task StartAsync(
            string executable,
            string arguments,
            int columns = 80,
            int rows = 24,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Write(string text)
        {
        }

        public void Resize(int columns, int rows)
        {
        }

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }
}
