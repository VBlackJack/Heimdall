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
using FluentAssertions;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public sealed class UpdateInstallerTests
{
    private sealed class FakeHost : IUpdateInstallerHost
    {
        public string? ExecutablePath { get; set; } = @"C:\Program Files\Heimdall\Heimdall.exe";
        public int ProcessId { get; set; } = 1234;
        public bool DirectoryWritable { get; set; } = true;
        public bool StartDetachedResult { get; set; } = true;
        public Func<string, string, bool>? StartDetachedThrows { get; set; }
        public Action<string, string>? WriteAllTextOverride { get; set; }

        public string ScriptPathValue { get; set; } = @"C:\Temp\Heimdall_relaunch_script.ps1";
        public string LogPathValue { get; set; } = @"C:\Temp\Heimdall_relaunch_log.log";
        public string PowerShellExecutable { get; set; } = "pwsh.exe";

        public string? WrittenPath { get; private set; }
        public string? WrittenContent { get; private set; }
        public string? StartedFileName { get; private set; }
        public string? StartedArguments { get; private set; }
        public int StartDetachedCallCount { get; private set; }

        public string CreateScriptPath() => ScriptPathValue;

        public string CreateLogPath() => LogPathValue;

        public string ResolvePowerShellExecutable() => PowerShellExecutable;

        public bool IsDirectoryWritable(string directory) => DirectoryWritable;

        public void WriteAllText(string path, string content)
        {
            WriteAllTextOverride?.Invoke(path, content);
            WrittenPath = path;
            WrittenContent = content;
        }

        public bool StartDetached(string fileName, string arguments)
        {
            StartDetachedCallCount++;
            StartedFileName = fileName;
            StartedArguments = arguments;
            return StartDetachedThrows?.Invoke(fileName, arguments) ?? StartDetachedResult;
        }
    }

    [Fact]
    public void BeginInstall_HappyPath_WritesScriptAndLaunchesHost()
    {
        var host = new FakeHost();
        var installer = new UpdateInstaller(host);

        var result = installer.BeginInstall(@"C:\Temp\HeimdallSetup.exe");

        result.Should().BeTrue();
        host.WrittenPath.Should().Be(host.ScriptPathValue);
        host.WrittenContent.Should().Contain(@"C:\Temp\HeimdallSetup.exe");
        host.StartDetachedCallCount.Should().Be(1);
        host.StartedFileName.Should().Be("pwsh.exe");
        host.StartedArguments.Should().Contain("-File");
        host.StartedArguments.Should().Contain(host.ScriptPathValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BeginInstall_UnknownExecutablePath_ReturnsFalseAndNeverLaunches(string? exePath)
    {
        var host = new FakeHost { ExecutablePath = exePath };
        var installer = new UpdateInstaller(host);

        var result = installer.BeginInstall(@"C:\Temp\HeimdallSetup.exe");

        result.Should().BeFalse();
        host.StartDetachedCallCount.Should().Be(0);
        host.WrittenPath.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BeginInstall_EmptyInstallerPath_ReturnsFalseAndNeverLaunches(string? installerPath)
    {
        var host = new FakeHost();
        var installer = new UpdateInstaller(host);

        var result = installer.BeginInstall(installerPath!);

        result.Should().BeFalse();
        host.StartDetachedCallCount.Should().Be(0);
        host.WrittenContent.Should().BeNull();
    }

    [Fact]
    public void BeginInstall_NonWritableInstallDir_ScriptRequestsElevation()
    {
        var host = new FakeHost { DirectoryWritable = false };
        var installer = new UpdateInstaller(host);

        installer.BeginInstall(@"C:\Temp\HeimdallSetup.exe");

        host.WrittenContent.Should().Contain("-Verb RunAs");
    }

    [Fact]
    public void BeginInstall_WritableInstallDir_ScriptDoesNotRequestElevation()
    {
        var host = new FakeHost { DirectoryWritable = true };
        var installer = new UpdateInstaller(host);

        installer.BeginInstall(@"C:\Temp\HeimdallSetup.exe");

        host.WrittenContent.Should().NotContain("-Verb RunAs");
    }

    [Fact]
    public void BeginInstall_StartDetachedReturnsFalse_ReturnsFalse()
    {
        var host = new FakeHost { StartDetachedResult = false };
        var installer = new UpdateInstaller(host);

        var result = installer.BeginInstall(@"C:\Temp\HeimdallSetup.exe");

        result.Should().BeFalse();
        host.StartDetachedCallCount.Should().Be(1);
    }

    [Fact]
    public void BeginInstall_WriteAllTextThrowsIoException_ReturnsFalseWithoutEscaping()
    {
        var host = new FakeHost
        {
            WriteAllTextOverride = (_, _) => throw new IOException("disk full"),
        };
        var installer = new UpdateInstaller(host);

        var act = () => installer.BeginInstall(@"C:\Temp\HeimdallSetup.exe");

        var result = act.Should().NotThrow().Subject;
        result.Should().BeFalse();
        host.StartDetachedCallCount.Should().Be(0);
    }
}
