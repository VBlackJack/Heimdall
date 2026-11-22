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

using System.Runtime.Versioning;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

/// <summary>
/// The organisation veto on the Audit pack governs the privilege launcher: both the entry point
/// and the headless elevated child refuse before doing anything.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrivilegeLauncherPolicyVetoTests
{
    private const string PrivLaunch = "--privlaunch";
    private const string Payload = "--payload";

    // A fully-qualified path to a file that does not exist fails payload validation for every
    // level, so no process can start whatever the account the tests run under.
    private static readonly string NeverLaunchedExecutable = Path.Combine(
        Path.GetTempPath(),
        "heimdall-no-such-executable-" + Guid.NewGuid().ToString("N") + ".exe");

    private static ToolPackPolicy Forbidding(params ToolPack[] packs)
    {
        HashSet<string> names = packs.Select(ToolPackPolicy.ValueNameFor).ToHashSet(StringComparer.Ordinal);
        return new ToolPackPolicy(name => names.Contains(name) ? ToolPackPolicy.ForbiddenValue : ToolPackPolicy.NoRestrictionValue);
    }

    private static ToolPackPolicy NoPolicy() => new(_ => null);

    [Theory]
    [InlineData(PrivilegeLevel.CurrentUserElevated)]
    [InlineData(PrivilegeLevel.System)]
    [InlineData(PrivilegeLevel.TrustedInstaller)]
    public void Launch_WhenThePolicyForbidsTheAuditPack_RefusesEveryLevelBeforeAnyWork(PrivilegeLevel level)
    {
        PrivilegeLaunchResult result = PrivilegeLauncher.Launch(
            NeverLaunchedExecutable, ["--flag"], level, Forbidding(ToolPack.Audit));

        Assert.False(result.Success);
        Assert.True(result.RefusedByPolicy);
        Assert.Equal(0, result.ProcessId);
    }

    [Theory]
    [InlineData(PrivilegeLevel.CurrentUserElevated)]
    [InlineData(PrivilegeLevel.System)]
    [InlineData(PrivilegeLevel.TrustedInstaller)]
    public void Launch_WithoutTheVeto_BehavesAsBefore(PrivilegeLevel level)
    {
        PrivilegeLaunchResult result = PrivilegeLauncher.Launch(
            NeverLaunchedExecutable, ["--flag"], level, NoPolicy());

        // The payload check still refuses the missing file, with its own message.
        Assert.False(result.Success);
        Assert.False(result.RefusedByPolicy);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public void Launch_AVetoOnAnotherPack_DoesNotRefuse()
    {
        PrivilegeLaunchResult result = PrivilegeLauncher.Launch(
            NeverLaunchedExecutable, [], PrivilegeLevel.System, Forbidding(ToolPack.NetworkDiagnostics, ToolPack.Administration));

        Assert.False(result.RefusedByPolicy);
    }

    [Fact]
    public void TheForbiddenExitCode_IsNamedAndCollidesWithNoOtherExitCode()
    {
        int[] codes =
        [
            PrivilegeLauncher.ExitCodeSuccess,
            PrivilegeLauncher.ExitCodeRefused,
            PrivilegeLauncher.ExitCodeLaunchFailed,
            PrivilegeLauncher.ExitCodeForbiddenByPolicy,
        ];

        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal(0, PrivilegeLauncher.ExitCodeSuccess);
        Assert.Equal(1, PrivilegeLauncher.ExitCodeRefused);
        Assert.Equal(2, PrivilegeLauncher.ExitCodeLaunchFailed);
    }

    [Fact]
    public void HandlePrivilegeLaunchArgs_WhenThePolicyForbidsTheAuditPack_RefusesAWellFormedRequest()
    {
        string encoded = PrivilegeLauncher.EncodeLaunchPayload(Environment.GetEnvironmentVariable("COMSPEC")!, []);
        string[] args = [PrivLaunch, nameof(PrivilegeLevel.System), Payload, encoded];
        bool elevationChecked = false;

        int? code = PrivilegeLauncher.HandlePrivilegeLaunchArgs(
            args,
            () =>
            {
                elevationChecked = true;
                return true;
            },
            Forbidding(ToolPack.Audit),
            out PrivilegeLauncher.PrivilegeLaunchRefusal refusal);

        Assert.Equal(PrivilegeLauncher.ExitCodeForbiddenByPolicy, code);
        Assert.Equal(PrivilegeLauncher.PrivilegeLaunchRefusal.ForbiddenByPolicy, refusal);
        Assert.False(elevationChecked);
    }

    [Fact]
    public void HandlePrivilegeLaunchArgs_TheVetoComesBeforeEveryOtherCheck()
    {
        int? code = PrivilegeLauncher.HandlePrivilegeLaunchArgs(
            [PrivLaunch],
            static () => false,
            Forbidding(ToolPack.Audit),
            out PrivilegeLauncher.PrivilegeLaunchRefusal refusal);

        Assert.Equal(PrivilegeLauncher.ExitCodeForbiddenByPolicy, code);
        Assert.Equal(PrivilegeLauncher.PrivilegeLaunchRefusal.ForbiddenByPolicy, refusal);
    }

    [Theory]
    [InlineData]
    [InlineData("--other")]
    [InlineData("--payload", "x")]
    public void HandlePrivilegeLaunchArgs_NotAPrivilegeLaunch_StillReturnsNullUnderTheVeto(params string[] args)
    {
        int? code = PrivilegeLauncher.HandlePrivilegeLaunchArgs(
            args,
            static () => false,
            Forbidding(ToolPack.Audit),
            out PrivilegeLauncher.PrivilegeLaunchRefusal refusal);

        Assert.Null(code);
        Assert.Equal(PrivilegeLauncher.PrivilegeLaunchRefusal.None, refusal);
    }

    [Fact]
    public void HandlePrivilegeLaunchArgs_WithoutTheVeto_BehavesAsBefore()
    {
        int? malformed = PrivilegeLauncher.HandlePrivilegeLaunchArgs(
            [PrivLaunch],
            static () => false,
            NoPolicy(),
            out PrivilegeLauncher.PrivilegeLaunchRefusal malformedReason);

        string encoded = PrivilegeLauncher.EncodeLaunchPayload(Environment.GetEnvironmentVariable("COMSPEC")!, []);
        int? notElevated = PrivilegeLauncher.HandlePrivilegeLaunchArgs(
            [PrivLaunch, nameof(PrivilegeLevel.System), Payload, encoded],
            static () => false,
            NoPolicy(),
            out PrivilegeLauncher.PrivilegeLaunchRefusal notElevatedReason);

        Assert.Equal(PrivilegeLauncher.ExitCodeRefused, malformed);
        Assert.Equal(PrivilegeLauncher.PrivilegeLaunchRefusal.MalformedCommandLine, malformedReason);
        Assert.Equal(PrivilegeLauncher.ExitCodeRefused, notElevated);
        Assert.Equal(PrivilegeLauncher.PrivilegeLaunchRefusal.NotElevated, notElevatedReason);
    }
}
