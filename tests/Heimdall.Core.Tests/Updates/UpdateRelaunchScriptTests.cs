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

using Heimdall.Core.Updates;

namespace Heimdall.Core.Tests;

public sealed class UpdateRelaunchScriptTests
{
    private static UpdateRelaunchSpec SampleSpec(
        bool requiresElevation = false,
        string? logPath = null,
        string installerPath = @"C:\Temp\HeimdallSetup.exe",
        string targetExecutablePath = @"C:\Program Files\Heimdall\Heimdall.exe",
        string scriptPath = @"C:\Temp\heimdall_relaunch.ps1") =>
        new(
            InstallerPath: installerPath,
            TargetExecutablePath: targetExecutablePath,
            ProcessId: 4242,
            ScriptPath: scriptPath,
            RequiresElevation: requiresElevation,
            LogPath: logPath);

    [Fact]
    public void EscapeSingleQuoted_DoublesSingleQuotes()
    {
        Assert.Equal("it''s", UpdateRelaunchScript.EscapeSingleQuoted("it's"));
        Assert.Equal("''a''b''", UpdateRelaunchScript.EscapeSingleQuoted("'a'b'"));
    }

    [Fact]
    public void EscapeSingleQuoted_LeavesNormalPathUnchanged()
    {
        const string path = @"C:\Program Files\Heimdall\Heimdall.exe";
        Assert.Equal(path, UpdateRelaunchScript.EscapeSingleQuoted(path));
    }

    [Fact]
    public void EscapeSingleQuoted_HandlesEmptyString()
    {
        Assert.Equal(string.Empty, UpdateRelaunchScript.EscapeSingleQuoted(string.Empty));
    }

    [Fact]
    public void BuildPowerShellArguments_ContainsHostFlagsAndQuotedScriptPath()
    {
        const string scriptPath = @"C:\Temp\heimdall_relaunch.ps1";
        var args = UpdateRelaunchScript.BuildPowerShellArguments(scriptPath);

        Assert.Contains("-NoProfile", args);
        Assert.Contains("-NonInteractive", args);
        Assert.Contains("-ExecutionPolicy Bypass", args);
        Assert.Contains("-WindowStyle Hidden", args);
        Assert.Contains($"-File \"{scriptPath}\"", args);
    }

    [Fact]
    public void Build_EmbedsProcessIdTimeoutPathsAndArguments()
    {
        var spec = SampleSpec();
        var script = UpdateRelaunchScript.Build(spec);

        Assert.Contains("-Id 4242", script);
        Assert.Contains($"-Timeout {UpdateRelaunchScript.DefaultWaitTimeoutSeconds}", script);
        Assert.Contains($"'{spec.InstallerPath}'", script);
        Assert.Contains($"'{spec.TargetExecutablePath}'", script);
        Assert.Contains($"'{spec.ScriptPath}'", script);
        Assert.Contains($"-ArgumentList '{UpdateRelaunchScript.DefaultInstallerArguments}'", script);
    }

    [Fact]
    public void Build_EmitsSelfDeleteOfScriptPath()
    {
        var spec = SampleSpec();
        var script = UpdateRelaunchScript.Build(spec);

        Assert.Contains(
            $"Remove-Item -LiteralPath '{spec.ScriptPath}' -Force -ErrorAction SilentlyContinue",
            script);
    }

    [Fact]
    public void Build_WithElevation_EmitsRunAsVerb()
    {
        var script = UpdateRelaunchScript.Build(SampleSpec(requiresElevation: true));
        Assert.Contains("-Verb RunAs", script);
    }

    [Fact]
    public void Build_WithoutElevation_OmitsRunAsVerb()
    {
        var script = UpdateRelaunchScript.Build(SampleSpec(requiresElevation: false));
        Assert.DoesNotContain("-Verb RunAs", script);
    }

    [Fact]
    public void Build_WithLogPath_EmitsTranscriptStartAndStop()
    {
        const string logPath = @"C:\Temp\heimdall_update.log";
        var script = UpdateRelaunchScript.Build(SampleSpec(logPath: logPath));

        Assert.Contains($"Start-Transcript -Path '{logPath}' -Append", script);
        Assert.Contains("Stop-Transcript", script);
    }

    [Fact]
    public void Build_WithoutLogPath_EmitsNoTranscript()
    {
        var script = UpdateRelaunchScript.Build(SampleSpec(logPath: null));

        Assert.DoesNotContain("Start-Transcript", script);
        Assert.DoesNotContain("Stop-Transcript", script);
    }

    [Fact]
    public void Build_PlacesRelaunchInsideFinallyBlock()
    {
        var spec = SampleSpec();
        var script = UpdateRelaunchScript.Build(spec);

        var finallyIndex = script.IndexOf("finally", StringComparison.Ordinal);
        var relaunchIndex = script.IndexOf(
            $"Start-Process -FilePath '{spec.TargetExecutablePath}'", StringComparison.Ordinal);

        Assert.True(finallyIndex >= 0, "script must contain a finally block");
        Assert.True(relaunchIndex > finallyIndex, "relaunch must appear after the finally token");
    }

    [Fact]
    public void Build_PathWithSingleQuote_IsEscapedInScript()
    {
        var spec = SampleSpec(installerPath: @"C:\Temp\o'brien\setup.exe");
        var script = UpdateRelaunchScript.Build(spec);

        Assert.Contains(@"'C:\Temp\o''brien\setup.exe'", script);
    }
}
