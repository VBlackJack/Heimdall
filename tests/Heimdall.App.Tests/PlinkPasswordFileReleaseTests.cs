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

using System.IO;
using System.Linq;
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

    private const bool Attested = true;
    private const bool NotAttested = false;

    [Fact]
    public void Attested_BeforeAnySignal_DeletesNothing()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];

        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add, Attested);

        // Arming alone must not touch the file: the launcher may not have opened it yet.
        Assert.Empty(deleted);
        Assert.Equal(1, session.DataSubscribers);
        Assert.Equal(1, session.ExitSubscribers);
    }

    [Fact]
    public void Attested_FirstByte_DeletesImmediately()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];
        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add, Attested);

        session.RaiseData([0x24]);

        Assert.Equal([PasswordFile], deleted);
    }

    [Fact]
    public void Unattested_OutputDeletesNothing_ExitDoes()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];
        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add, NotAttested);

        // A launcher whose -pwfile timing was never measured may print before it reads the file, so
        // its output proves nothing and must not be listened to at all.
        Assert.Equal(0, session.DataSubscribers);

        session.RaiseData([0x24]);
        session.RaiseData([0x20, 0x0A]);
        Assert.Empty(deleted);

        session.RaiseExit(0);
        Assert.Equal([PasswordFile], deleted);
    }

    [Fact]
    public void Attested_FurtherOutputAndExit_DoNotDeleteAgain()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];
        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add, Attested);

        session.RaiseData([0x24]);
        session.RaiseData([0x20, 0x0A]);
        session.RaiseExit(0);

        Assert.Equal([PasswordFile], deleted);
    }

    [Fact]
    public void Attested_ButSilent_StillDeletesAtExit()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];
        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add, Attested);

        // A session that never printed anything: the backstop carries this case, which is why
        // removing the exit subscription would be a regression rather than a cleanup, and why
        // SSH-013 is not closed.
        session.RaiseExit(1);

        Assert.Equal([PasswordFile], deleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AfterRelease_NoSubscriptionSurvives(bool attested)
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];
        PlinkPasswordFileReleaseHandle handle =
            PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add, attested);

        handle.Release();

        Assert.Equal(0, session.DataSubscribers);
        Assert.Equal(0, session.ExitSubscribers);
        Assert.Equal([PasswordFile], deleted);
    }

    [Fact]
    public void DisposeRaisingExitThenAnExplicitRelease_DeletesExactlyOnce()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];
        PlinkPasswordFileReleaseHandle handle =
            PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add, Attested);

        // This is the launch-failure sequence in SshHandler: the session is disposed, which can
        // itself raise ProcessExited, and the catch then releases. A catch that deleted directly
        // instead of going through the handle would produce a second invocation here.
        session.RaiseExit(-1);
        handle.Release();

        Assert.Equal([PasswordFile], deleted);
    }

    [Fact]
    public void ExplicitReleaseBeforeAnySignal_DeletesOnceAndSilencesTheSession()
    {
        FakeTerminalSession session = new();
        List<string?> deleted = [];
        PlinkPasswordFileReleaseHandle handle =
            PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Add, Attested);

        // The cancellation path: nothing was ever started, so no signal will arrive.
        handle.Release();
        session.RaiseData([0x24]);
        session.RaiseExit(0);

        Assert.Equal([PasswordFile], deleted);
    }

    [Fact]
    public async Task DataAndExitRacing_DeleteExactlyOnce()
    {
        FakeTerminalSession session = new();
        System.Collections.Concurrent.ConcurrentQueue<string?> deleted = new();
        PlinkPasswordFileRelease.Arm(session, PasswordFile, deleted.Enqueue, Attested);

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
            () => PlinkPasswordFileRelease.Arm(null!, PasswordFile, _ => { }, Attested));
        Assert.Throws<ArgumentNullException>(
            () => PlinkPasswordFileRelease.Arm(session, PasswordFile, null!, Attested));
    }

    // The shared gate is a contract, and this pins it where behaviour currently cannot.
    // SshHandler's launch-failure and cancellation catches must release through the handle rather
    // than deleting directly, because disposing the session can raise ProcessExited and a direct
    // delete would then be a second invocation. Today that double call is masked: PipeModeSession
    // .Dispose detaches its Exited handler before disposing the process, so nothing observable
    // happens. The contract exists precisely so the catches do not depend on that internal detail,
    // which is why it is checked on the source of those two blocks and not on a global count - a
    // count is invariant under relocation and would not notice a direct delete moving elsewhere.
    [Fact]
    public void SshHandlerLaunchCatches_ReleaseThroughTheSharedGate()
    {
        string body = ExtractMethodBody(
            ReadAppSource("Services/Handlers/SshHandler.cs"),
            "internal async Task<ConnectionResult> ConnectSshViaPlinkAsync(");

        string[] catches = ExtractCatchBlocks(body);
        Assert.NotEmpty(catches);

        string[] launchCatches = [.. catches.Where(c => c.Contains("terminalSession.Dispose()", StringComparison.Ordinal))];
        Assert.Equal(2, launchCatches.Length);

        foreach (string block in launchCatches)
        {
            Assert.Contains("passwordFileRelease?.Release();", block, StringComparison.Ordinal);
            Assert.DoesNotContain("_deletePlinkPasswordFile(", block, StringComparison.Ordinal);
        }
    }

    // The password file's path names a file that holds a secret, and its content is the secret.
    // Neither may reach the log.
    //
    // The first attempt at this guard was vacuous and an independent review caught it: it filtered
    // source LINES containing "FileLogger." and asserted on those, but this codebase wraps a log
    // call across two lines, so the interpolated message - the only part that can carry a secret -
    // was never inspected, and for a file with no logging at all the loop body never ran. Both
    // cases are now split and each one can fail.
    [Fact]
    public void TheComponentHoldingTheSecretPath_DoesNotLogAtAll()
    {
        string source = ReadAppSource("Services/Handlers/PlinkPasswordFileRelease.cs");

        // This type is the one that knows the password file's path. The simplest contract it can
        // carry is that it never logs, and unlike a filter over log statements this cannot pass by
        // finding nothing to check.
        Assert.DoesNotContain("FileLogger.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAttestationLogStatement_CarriesNoSecret()
    {
        string[] statements = ExtractLogStatements(
            ReadAppSource("Services/Handlers/PlinkCompatibilityAttestation.cs"));

        // Proves the scan actually found the logging it is meant to police, so a future refactor
        // that hides it cannot turn this test green by emptiness.
        Assert.NotEmpty(statements);

        foreach (string statement in statements)
        {
            Assert.DoesNotContain("password", statement, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", statement, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Whole statements, from the call to its terminating semicolon, because a log call here spans
    // several lines and a line-based scan reads only the opening token.
    private static string[] ExtractLogStatements(string source)
    {
        List<string> statements = [];
        int index = 0;
        while (true)
        {
            int found = source.IndexOf("FileLogger.", index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            int terminator = source.IndexOf(';', found);
            if (terminator < 0)
            {
                statements.Add(source[found..]);
                break;
            }

            statements.Add(source[found..(terminator + 1)]);
            index = terminator + 1;
        }

        return [.. statements];
    }

    private static string ReadAppSource(string relativePath)
    {
        // Anchored on the solution file, not on .git: in a worktree .git is a FILE, so a
        // Directory.Exists walk climbs past this checkout into the main one and reads the wrong
        // source. This test caught that on itself.
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string full = Path.Combine(
            directory!.FullName,
            "src",
            "Heimdall.App",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Signature not found: {signature}");

        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"No body for: {signature}");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(i + 1)];
                }
            }
        }

        Assert.Fail($"Unbalanced body for: {signature}");
        return string.Empty;
    }

    private static string[] ExtractCatchBlocks(string body)
    {
        List<string> blocks = [];
        int index = 0;
        while (true)
        {
            int found = body.IndexOf("catch", index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            int open = body.IndexOf('{', found);
            if (open < 0)
            {
                break;
            }

            int depth = 0;
            for (int i = open; i < body.Length; i++)
            {
                if (body[i] == '{')
                {
                    depth++;
                }
                else if (body[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        blocks.Add(body[open..(i + 1)]);
                        index = i + 1;
                        break;
                    }
                }
            }

            if (depth != 0)
            {
                break;
            }
        }

        return [.. blocks];
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
