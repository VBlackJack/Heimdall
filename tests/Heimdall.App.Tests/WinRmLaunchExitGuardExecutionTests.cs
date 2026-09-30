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
using System.Text;
using Heimdall.App.Services.WinRm;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Terminal.ConPty;

namespace Heimdall.App.Tests;

/// <summary>
/// Runs the WinRM launch the builder and the credential bootstrap actually emit, in a real
/// Windows PowerShell under a pseudo console, and holds it to the rule that the process ends
/// the first time the host would show a local prompt.
/// </summary>
/// <remarks>
/// <para>No WinRM endpoint is reachable from a test, so Enter-PSSession is shadowed by a
/// function of the same name defined ahead of the emitted text (a function wins over a cmdlet).
/// The failing double writes a NON-terminating error, as the real cmdlet does for a refused
/// connection, so the launch's own <c>-ErrorAction Stop</c> (or the bootstrap's
/// <c>$ErrorActionPreference</c>) is what makes it end the command. The returning double pushes
/// nothing, so the host's next prompt is local at once: the same state as after a remote
/// <c>exit</c>, which pops the remote runspace.</para>
/// <para>Measured 2026-09-30 against a real pushed runspace (Enter-PSHostProcess, which pushes
/// through the same host call as Enter-PSSession), on pwsh 7.6 and Windows PowerShell 5.1: the
/// prompt is evaluated in the pushed runspace while it lasts, and the local guard fires, with
/// exit code 0, once the user types <c>exit</c> there.</para>
/// <para>CIUnstable for the reason ConPtySessionTests gives: under a test runner a child can
/// fail to attach to the pseudo console and exit with code 0 on its own. The expected-1 case
/// cannot pass that way, and the expected-0 case also requires the double's marker.</para>
/// </remarks>
public sealed class WinRmLaunchExitGuardExecutionTests
{
    /// <summary>Failure bound on one launch; paid only when the process fails to end.</summary>
    private static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(60);

    private const string CommandArgumentsPrefix = "-NoLogo -NoExit -NoProfile -Command \"";

    private const string EnteredMarker = "HEIMDALL-TEST-ENTERED";

    private const string DoubleParameters =
        "[CmdletBinding()] param($ComputerName, $Port, $Authentication, [switch]$UseSSL, $SessionOption, $Credential)";

    private const string FailingEnterPSSession =
        "function Enter-PSSession { " + DoubleParameters + " Write-Error 'simulated WinRM connection failure' }";

    private const string ReturningEnterPSSession =
        "function Enter-PSSession { " + DoubleParameters + " Write-Host '" + EnteredMarker + "' }";

    [Theory]
    [Trait("Category", "CIUnstable")]
    [InlineData(false, WinRmPowerShellLaunchBuilder.RemoteSessionNotEnteredExitCode)]
    [InlineData(true, WinRmPowerShellLaunchBuilder.RemoteSessionEndedExitCode)]
    public async Task CurrentUserLaunch_EndsAtFirstLocalPrompt(bool enterReturns, int expectedExitCode)
    {
        if (!ConPtySession.IsAvailable)
        {
            return;
        }

        WinRmPowerShellLaunchSpec spec = CreateBuilder().Build(CreateServer());
        Assert.StartsWith(CommandArgumentsPrefix, spec.Arguments, StringComparison.Ordinal);
        string shadow = enterReturns ? ReturningEnterPSSession : FailingEnterPSSession;
        string arguments = CommandArgumentsPrefix + shadow + "; " + spec.Arguments[CommandArgumentsPrefix.Length..];

        (int exitCode, string output) = await RunUntilExitAsync(spec.Executable, arguments);

        Assert.Equal(expectedExitCode, exitCode);
        if (enterReturns)
        {
            Assert.Contains(EnteredMarker, output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [Trait("Category", "CIUnstable")]
    [InlineData(false, true, WinRmPowerShellLaunchBuilder.RemoteSessionNotEnteredExitCode)]
    [InlineData(true, false, WinRmPowerShellLaunchBuilder.RemoteSessionNotEnteredExitCode)]
    [InlineData(true, true, WinRmPowerShellLaunchBuilder.RemoteSessionEndedExitCode)]
    public async Task CredentialBootstrap_EndsAtFirstLocalPrompt(
        bool enterReturns,
        bool blobDecrypts,
        int expectedExitCode)
    {
        if (!ConPtySession.IsAvailable)
        {
            return;
        }

        ServerProfileDto server = CreateServer();
        server.WinRmIdentityMode = WinRmIdentityMode.Credential;
        server.WinRmUsername = @"CONTOSO\operator";
        server.WinRmPasswordEncrypted = "stored";
        string blob = blobDecrypts
            ? DpapiProvider.ProtectBytes(Encoding.UTF8.GetBytes("test-only-password"))
            : Convert.ToBase64String(Encoding.UTF8.GetBytes("not a dpapi blob"));
        string shadow = enterReturns ? ReturningEnterPSSession : FailingEnterPSSession;
        string scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"{WinRmCredentialBootstrap.ScriptFilePrefix}exit_guard_test_{Guid.NewGuid():N}.ps1");

        try
        {
            File.WriteAllText(
                scriptPath,
                shadow + "\r\n" + WinRmCredentialBootstrap.BuildScript(server, blob));
            WinRmPowerShellLaunchSpec spec = CreateBuilder().Build(server, scriptPath);

            (int exitCode, string output) = await RunUntilExitAsync(spec.Executable, spec.Arguments);

            Assert.Equal(expectedExitCode, exitCode);
            if (enterReturns && blobDecrypts)
            {
                Assert.Contains(EnteredMarker, output, StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static WinRmPowerShellLaunchBuilder CreateBuilder()
    {
        string windowsPowerShell = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return new WinRmPowerShellLaunchBuilder(
            name => string.Equals(name, "powershell.exe", StringComparison.OrdinalIgnoreCase)
                ? windowsPowerShell
                : null);
    }

    private static ServerProfileDto CreateServer()
        => new ServerProfileDto
        {
            ConnectionType = "WINRM",
            RemoteServer = "server01.contoso.local",
            WinRmPort = DefaultPorts.WinRmHttp,
            WinRmUseSsl = false,
            WinRmIdentityMode = WinRmIdentityMode.CurrentUser
        };

    private static async Task<(int ExitCode, string Output)> RunUntilExitAsync(
        string executable,
        string arguments)
    {
        using ConPtySession session = new();
        StringBuilder output = new();
        object outputLock = new();
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DataReceived += data =>
        {
            lock (outputLock)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
            }
        };
        session.ProcessExited += code => exited.TrySetResult(code);

        await session.StartAsync(executable, arguments);
        Task finished = await Task.WhenAny(exited.Task, Task.Delay(ExitDeadline));

        string captured;
        lock (outputLock)
        {
            captured = output.ToString();
        }

        if (finished != exited.Task)
        {
            session.Kill();
            Assert.Fail(
                $"the PowerShell host was still running {ExitDeadline.TotalSeconds} s after start: "
                + $"it is sitting at a local prompt. Output: {captured}");
        }

        return (await exited.Task, captured);
    }
}
