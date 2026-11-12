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
using System.Text;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The staged privileged write against a real sudo, in a throwaway container. Opt-in: set
/// <c>HEIMDALL_SUDO_TEST_CONTAINER</c> to the container name and <c>HEIMDALL_SUDO_TEST_PASSWORD</c>
/// to the password of its <c>sudotest</c> user. Each test writes its own sudoers file, runs the
/// command the production builder produces the way a POSIX login shell would, and removes the
/// sudoers file again.
/// </summary>
public sealed class PrivilegedSudoRealHostTests : IAsyncLifetime
{
    internal const string ContainerVariable = "HEIMDALL_SUDO_TEST_CONTAINER";
    internal const string PasswordVariable = "HEIMDALL_SUDO_TEST_PASSWORD";

    private const string User = "sudotest";
    private const string TargetDirectory = "/srv/heimdall-test";
    private const string Target = TargetDirectory + "/target.conf";
    private const string SudoersFile = "/etc/sudoers.d/heimdall-test";
    private const string SudoLog = "/var/log/sudo-heimdall.log";
    private const string IoLogDirectory = "/var/log/sudo-io-heimdall";

    // Fixed owner and mode of the file being replaced: they must survive the replacement.
    private const string TargetOwnerAndMode = "1234:1235:640";
    private const string OriginalContent = "original content\n";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private static string Container => Environment.GetEnvironmentVariable(ContainerVariable)!;

    private static string RightPassword => Environment.GetEnvironmentVariable(PasswordVariable)!;

    private const string LogfileDefault = "Defaults logfile=" + SudoLog + "\n";

    private const string PasswordRule = User + " ALL=(ALL) ALL\n";
    private const string NoPasswordRule = User + " ALL=(ALL) NOPASSWD: ALL\n";

    // The shape the old script broke on: the command itself needs no password, validation does.
    private const string ShellWithoutPasswordRules =
        PasswordRule + User + " ALL=(ALL) NOPASSWD: /usr/bin/sh, /bin/sh\n";

    // An unrelated command without a password, then a password rule for everything else.
    private const string MixedRules = User + " ALL=(ALL) NOPASSWD: /usr/bin/id\n" + PasswordRule;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await RootAsync("rm -f " + SudoersFile + " /tmp/heimdall-test.sudoers").ConfigureAwait(false);
        await DockerAsync(["exec", "-u", User, Container, "sudo", "-k"], null).ConfigureAwait(false);
    }

    // --- a, b: a password is required ------------------------------------------------------------

    [DockerSudoFact]
    public async Task A_PasswordRequired_RightPasswordInstalls()
    {
        await PrepareAsync(PasswordRule);
        byte[] payload = "listen 8080\nroot /srv/www\n"u8.ToArray();

        WriteRun run = await WriteAsync(payload, RightPassword);

        await AssertInstalledAsync(run, payload);
        Assert.DoesNotContain(RightPassword, Encoding.UTF8.GetString(await ReadTargetAsync()), StringComparison.Ordinal);
    }

    [DockerSudoFact]
    public async Task B_TimestampTimeoutZero_StillInstalls()
    {
        await PrepareAsync("Defaults timestamp_timeout=0\n" + PasswordRule);
        byte[] payload = "no ticket is kept\n"u8.ToArray();

        WriteRun run = await WriteAsync(payload, RightPassword);

        await AssertInstalledAsync(run, payload);
    }

    // --- c, d, i: no password needed for the command itself -----------------------------------

    [DockerSudoFact]
    public async Task C_NoPasswordForAnything_InstallsAndSudoNeverSeesThePassword()
    {
        await PrepareAsync(NoPasswordRule);
        byte[] payload = "no password asked\n"u8.ToArray();

        WriteRun run = await WriteAsync(payload, RightPassword);

        await AssertInstalledAsync(run, payload);
        string log = await RootAsync("cat " + SudoLog + " 2>/dev/null || true");
        Assert.DoesNotContain("incorrect password", log, StringComparison.Ordinal);
        Assert.Equal(1, Count(log, "COMMAND="));
    }

    [DockerSudoFact]
    public async Task D_MixedRules_UnrelatedCommandWithoutPassword_Installs()
    {
        await PrepareAsync(MixedRules);
        byte[] payload = "mixed rules\n"u8.ToArray();

        WriteRun run = await WriteAsync(payload, RightPassword);

        await AssertInstalledAsync(run, payload);
        Assert.False(
            Encoding.UTF8.GetString(await ReadTargetAsync()).StartsWith(RightPassword, StringComparison.Ordinal));
    }

    [DockerSudoFact]
    public async Task D2_TheShellNeedsNoPasswordButValidationDoes_Installs()
    {
        await PrepareAsync(ShellWithoutPasswordRules);
        byte[] payload = "the command itself is passwordless\n"u8.ToArray();

        WriteRun run = await WriteAsync(payload, RightPassword);

        await AssertInstalledAsync(run, payload);
        Assert.False(
            Encoding.UTF8.GetString(await ReadTargetAsync()).StartsWith(RightPassword, StringComparison.Ordinal));
        string log = await RootAsync("cat " + SudoLog + " 2>/dev/null || true");
        Assert.DoesNotContain("incorrect password", log, StringComparison.Ordinal);
    }

    [DockerSudoFact]
    public async Task I_VerifypwAny_WithMixedRules_Installs()
    {
        await PrepareAsync("Defaults verifypw=any\n" + MixedRules);
        byte[] payload = "verifypw any\n"u8.ToArray();

        WriteRun run = await WriteAsync(payload, RightPassword);

        await AssertInstalledAsync(run, payload);
    }

    // --- e, f, g: refusals ------------------------------------------------------------------------

    [DockerSudoFact]
    public async Task E_WrongPassword_IsRejected_TargetUntouched_AndSudoCountsOneAttempt()
    {
        await PrepareAsync(PasswordRule);

        WriteRun run = await WriteAsync("never installed\n"u8.ToArray(), "definitely-not-the-password");

        Assert.NotEqual(0, run.Exit);
        Assert.Equal(PrivilegedWriteFailureKind.PasswordRejected, run.Kind);
        await AssertUntouchedAsync();

        string log = await RootAsync("cat " + SudoLog);
        string[] attempts = log.Split('\n').Where(line => line.Contains("incorrect password", StringComparison.Ordinal)).ToArray();
        Assert.True(attempts.Length == 1, "expected one logged failure, got: " + string.Join(" | ", attempts));
        Assert.Contains("1 incorrect password attempt", attempts[0], StringComparison.Ordinal);
    }

    [DockerSudoFact]
    public async Task F_UserNotInSudoers_IsNotPermitted_NotRejected()
    {
        await PrepareAsync(string.Empty);

        WriteRun run = await WriteAsync("never installed\n"u8.ToArray(), RightPassword);

        Assert.NotEqual(0, run.Exit);
        Assert.Equal(PrivilegedWriteFailureKind.NotPermitted, run.Kind);
        await AssertUntouchedAsync();
    }

    [DockerSudoFact]
    public async Task G_Requiretty_IsPasswordUnavailable()
    {
        await PrepareAsync("Defaults requiretty\n" + PasswordRule);

        WriteRun run = await WriteAsync("never installed\n"u8.ToArray(), RightPassword);

        Assert.NotEqual(0, run.Exit);
        Assert.Equal(PrivilegedWriteFailureKind.PasswordUnavailable, run.Kind);
        await AssertUntouchedAsync();
    }

    // --- h: payload edge cases --------------------------------------------------------------------

    [DockerSudoTheory]
    [InlineData("first line equals the password")]
    [InlineData("empty")]
    [InlineData("no final newline")]
    [InlineData("one MiB of random bytes with NULs")]
    public async Task H_PayloadEdgeCases_AreInstalledByteForByte(string name)
    {
        await PrepareAsync(PasswordRule);
        byte[] payload = Payload(name);

        WriteRun run = await WriteAsync(payload, RightPassword);

        await AssertInstalledAsync(run, payload);
    }

    private static byte[] Payload(string name)
    {
        switch (name)
        {
            case "first line equals the password":
                return [.. Encoding.UTF8.GetBytes(RightPassword + "\n"), .. "second line\n"u8];
            case "empty":
                return [];
            case "no final newline":
                return "ends without a newline"u8.ToArray();
            default:
                byte[] random = new byte[1024 * 1024];
                new Random(20260611).NextBytes(random);
                for (int index = 0; index < random.Length; index += 4099)
                {
                    random[index] = 0;
                }

                return random;
        }
    }

    // --- j: the I/O log ---------------------------------------------------------------------------

    [DockerSudoFact]
    public async Task J_LogInput_WithNoPasswordNeeded_NeverRecordsThePassword()
    {
        await PrepareAsync("Defaults log_input, iolog_dir=" + IoLogDirectory + "\n" + NoPasswordRule);
        byte[] payload = "recorded input\n"u8.ToArray();

        WriteRun run = await WriteAsync(payload, RightPassword);

        await AssertInstalledAsync(run, payload);

        // The log must exist, or the search below proves nothing.
        string files = await RootAsync("find " + IoLogDirectory + " -type f 2>/dev/null | head -20");
        Assert.False(string.IsNullOrWhiteSpace(files), "sudo wrote no I/O log.");

        // The pattern goes in on standard input, so the password is on no command line.
        (int status, byte[] _, string _) = await DockerAsync(
            ["exec", "-i", "-u", "root", Container, "grep", "-rlF", "-f", "-", IoLogDirectory],
            Encoding.UTF8.GetBytes(RightPassword + "\n"));
        Assert.Equal(1, status);
    }

    // --- k: another shell -------------------------------------------------------------------------

    [DockerSudoFact]
    public async Task K_TheCommandAlsoRunsUnderDash()
    {
        string dash = (await RootAsync("command -v dash || true")).Trim();
        if (dash.Length == 0)
        {
            return;
        }

        await PrepareAsync(PasswordRule);
        byte[] payload = "run by dash\n"u8.ToArray();

        WriteRun run = await WriteAsync(payload, RightPassword, shell: dash);

        await AssertInstalledAsync(run, payload);
    }

    // --- helpers ----------------------------------------------------------------------------------

    private sealed record WriteRun(int Exit, string Stderr)
    {
        public PrivilegedWriteFailureKind Kind
            => PrivilegedFileTransfer.ClassifyWrite(new PrivilegedCommandResult(Exit, string.Empty, Stderr));
    }

    private static async Task<WriteRun> WriteAsync(byte[] payload, string password, string shell = "sh")
    {
        string command = PrivilegedFileCommands.BuildStagedWriteCommand(Target, overwrite: true, passwordMode: true, payload.Length);

        using var stdin = new MemoryStream();
        await PrivilegedFileTransfer.WriteInputAsync(
            stdin,
            new MemoryStream(payload),
            PrivilegedFileTransfer.EncodePasswordLine(password),
            CancellationToken.None);

        (int exit, byte[] _, string stderr) = await DockerAsync(
            ["exec", "-i", "-u", User, Container, shell, "-c", command],
            stdin.ToArray());
        return new WriteRun(exit, stderr);
    }

    private async Task AssertInstalledAsync(WriteRun run, byte[] payload)
    {
        Assert.True(run.Exit == 0, $"exit {run.Exit}: {run.Stderr}");
        Assert.Equal(PrivilegedWriteFailureKind.None, run.Kind);
        Assert.Equal(payload, await ReadTargetAsync());
        Assert.Equal(TargetOwnerAndMode, (await RootAsync("stat -c %u:%g:%a " + Target)).Trim());
        await AssertNothingLeftBehindAsync();
    }

    private async Task AssertUntouchedAsync()
    {
        Assert.Equal(OriginalContent, Encoding.UTF8.GetString(await ReadTargetAsync()));
        Assert.Equal(TargetOwnerAndMode, (await RootAsync("stat -c %u:%g:%a " + Target)).Trim());
        await AssertNothingLeftBehindAsync();
    }

    private static async Task AssertNothingLeftBehindAsync()
    {
        string staging = await RootAsync("find /tmp -maxdepth 1 -name '.heimdall-stage.*'");
        Assert.True(string.IsNullOrWhiteSpace(staging), "staging left behind: " + staging);
        string work = await RootAsync("find " + TargetDirectory + " -mindepth 1 -not -name target.conf");
        Assert.True(string.IsNullOrWhiteSpace(work), "work files left beside the target: " + work);
    }

    private static async Task<byte[]> ReadTargetAsync()
    {
        (int status, byte[] output, string error) = await DockerAsync(
            ["exec", "-u", "root", Container, "cat", Target],
            null);
        Assert.True(status == 0, error);
        return output;
    }

    private static async Task PrepareAsync(string sudoers)
    {
        await RootAsync(
            "rm -rf " + TargetDirectory + " " + SudoLog + " " + IoLogDirectory + " /var/run/sudo/ts/" + User + "; "
            + "mkdir -p " + TargetDirectory + "; "
            + "printf 'original content\\n' > " + Target + "; "
            + "chown 1234:1235 " + Target + "; chmod 640 " + Target + "; "
            + "find /tmp -maxdepth 1 -name '.heimdall-stage.*' -exec rm -rf {} + ; true");

        string file = LogfileDefault + sudoers;
        (int status, byte[] _, string error) = await DockerAsync(
            ["exec", "-i", "-u", "root", Container, "sh", "-c",
                "cat > /tmp/heimdall-test.sudoers && visudo -cf /tmp/heimdall-test.sudoers >/dev/null "
                + "&& install -m 0440 -o root -g root /tmp/heimdall-test.sudoers " + SudoersFile
                + "; rc=$?; rm -f /tmp/heimdall-test.sudoers; exit $rc"],
            Encoding.UTF8.GetBytes(file));
        Assert.True(status == 0, "sudoers rejected: " + error);

        (int reset, byte[] _, string resetError) = await DockerAsync(["exec", "-u", User, Container, "sudo", "-k"], null);
        Assert.True(reset == 0, resetError);
    }

    private static async Task<string> RootAsync(string script)
    {
        (int _, byte[] output, string _) = await DockerAsync(["exec", "-u", "root", Container, "sh", "-c", script], null);
        return Encoding.UTF8.GetString(output);
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0;
        for (int index = haystack.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<(int Exit, byte[] Output, string Error)> DockerAsync(string[] arguments, byte[]? stdin)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        using var output = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        Task<string> error = process.StandardError.ReadToEndAsync();

        await using (Stream input = process.StandardInput.BaseStream)
        {
            if (stdin is { Length: > 0 })
            {
                try
                {
                    await input.WriteAsync(stdin);
                }
                catch (IOException)
                {
                    // The command ended before reading it all; its status says why.
                }
            }
        }

        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await copy;
        return (process.ExitCode, output.ToArray(), await error);
    }
}

/// <summary>A fact that runs only when the real-sudo container is named in the environment.</summary>
public sealed class DockerSudoFactAttribute : FactAttribute
{
    public DockerSudoFactAttribute()
    {
        Skip = DockerSudoEnvironment.SkipReason();
    }
}

/// <summary>A theory that runs only when the real-sudo container is named in the environment.</summary>
public sealed class DockerSudoTheoryAttribute : TheoryAttribute
{
    public DockerSudoTheoryAttribute()
    {
        Skip = DockerSudoEnvironment.SkipReason();
    }
}

internal static class DockerSudoEnvironment
{
    internal static string? SkipReason()
    {
        bool named = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(PrivilegedSudoRealHostTests.ContainerVariable));
        bool password = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable(PrivilegedSudoRealHostTests.PasswordVariable));
        return named && password
            ? null
            : $"Set {PrivilegedSudoRealHostTests.ContainerVariable} and {PrivilegedSudoRealHostTests.PasswordVariable} to run.";
    }
}
