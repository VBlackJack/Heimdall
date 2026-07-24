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
using Heimdall.Core.Updates;

namespace Heimdall.App.Tests;

public sealed class UpdateInstallerTests
{
    private static readonly string InstallerSha256 = new('a', 64);

    private static TestPackage CreatePackage(
        string installerPath = @"C:\Temp\update-stage\HeimdallSetup.exe",
        string? expectedSha256 = null) =>
        new(
            installerPath,
            expectedSha256 ?? InstallerSha256,
            @"C:\Temp\update-stage");

    private sealed class FakeHost : IUpdateInstallerHost
    {
        public string? ExecutablePath { get; set; } = @"C:\Program Files\Heimdall\Heimdall.exe";
        public int ProcessId { get; set; } = 1234;
        public bool DirectoryWritable { get; set; } = true;
        public bool StartDetachedResult { get; set; } = true;
        public Func<string, string, bool>? StartDetachedThrows { get; set; }
        public Action<string, string>? WriteProtectedTextOverride { get; set; }
        public bool VerifySha256Result { get; set; } = true;

        public string ScriptPathValue { get; set; } =
            @"C:\Temp\update-stage\Heimdall_relaunch_script.ps1";
        public string LogPathValue { get; set; } = @"C:\Temp\Heimdall_relaunch_log.log";
        public string PowerShellExecutable { get; set; } = "pwsh.exe";

        public string? WrittenPath { get; private set; }
        public string? WrittenContent { get; private set; }
        public string? StartedFileName { get; private set; }
        public string? StartedArguments { get; private set; }
        public int StartDetachedCallCount { get; private set; }
        public string? ScriptStagingDirectory { get; private set; }
        public string? VerifiedPath { get; private set; }
        public string? VerifiedSha256 { get; private set; }

        public string CreateScriptPath(string stagingDirectory)
        {
            ScriptStagingDirectory = stagingDirectory;
            return ScriptPathValue;
        }

        public string CreateLogPath() => LogPathValue;

        public string ResolvePowerShellExecutable() => PowerShellExecutable;

        public bool IsDirectoryWritable(string directory) => DirectoryWritable;

        public void WriteProtectedText(string path, string content)
        {
            WriteProtectedTextOverride?.Invoke(path, content);
            WrittenPath = path;
            WrittenContent = content;
        }

        public bool VerifySha256(string path, string expectedSha256)
        {
            VerifiedPath = path;
            VerifiedSha256 = expectedSha256;
            return VerifySha256Result;
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

        TestPackage package = CreatePackage();

        var result = installer.BeginInstall(package);

        result.Should().BeTrue();
        host.WrittenPath.Should().Be(host.ScriptPathValue);
        host.WrittenContent.Should().Contain(package.InstallerPath);
        host.WrittenContent.Should().Contain(package.ExpectedSha256);
        host.ScriptStagingDirectory.Should().Be(package.StagingDirectory);
        host.VerifiedPath.Should().Be(host.ScriptPathValue);
        host.VerifiedSha256.Should().HaveLength(64);
        host.StartDetachedCallCount.Should().Be(1);
        host.StartedFileName.Should().Be("pwsh.exe");
        host.StartedArguments.Should().Contain("-EncodedCommand");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BeginInstall_UnknownExecutablePath_ReturnsFalseAndNeverLaunches(string? exePath)
    {
        var host = new FakeHost { ExecutablePath = exePath };
        var installer = new UpdateInstaller(host);

        var result = installer.BeginInstall(CreatePackage());

        result.Should().BeFalse();
        host.StartDetachedCallCount.Should().Be(0);
        host.WrittenPath.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BeginInstall_InvalidInstallerPath_ReturnsFalseAndNeverLaunches(string? installerPath)
    {
        var host = new FakeHost();
        var installer = new UpdateInstaller(host);

        var result = installer.BeginInstall(CreatePackage(installerPath!));

        result.Should().BeFalse();
        host.StartDetachedCallCount.Should().Be(0);
        host.WrittenContent.Should().BeNull();
    }

    [Fact]
    public void BeginInstall_NonWritableInstallDir_ScriptRequestsElevation()
    {
        var host = new FakeHost { DirectoryWritable = false };
        var installer = new UpdateInstaller(host);

        installer.BeginInstall(CreatePackage());

        host.WrittenContent.Should().Contain("-Verb RunAs");
    }

    [Fact]
    public void BeginInstall_WritableInstallDir_ScriptDoesNotRequestElevation()
    {
        var host = new FakeHost { DirectoryWritable = true };
        var installer = new UpdateInstaller(host);

        installer.BeginInstall(CreatePackage());

        host.WrittenContent.Should().NotContain("-Verb RunAs");
    }

    [Fact]
    public void BeginInstall_StartDetachedReturnsFalse_ReturnsFalse()
    {
        var host = new FakeHost { StartDetachedResult = false };
        var installer = new UpdateInstaller(host);

        var result = installer.BeginInstall(CreatePackage());

        result.Should().BeFalse();
        host.StartDetachedCallCount.Should().Be(1);
    }

    [Fact]
    public void BeginInstall_WriteProtectedTextThrowsIoException_ReturnsFalseWithoutEscaping()
    {
        var host = new FakeHost
        {
            WriteProtectedTextOverride = (_, _) => throw new IOException("disk full"),
        };
        var installer = new UpdateInstaller(host);

        var act = () => installer.BeginInstall(CreatePackage());

        var result = act.Should().NotThrow().Subject;
        result.Should().BeFalse();
        host.StartDetachedCallCount.Should().Be(0);
    }

    [Fact]
    public void BeginInstall_ScriptReadbackHashMismatch_ReturnsFalseWithoutLaunching()
    {
        var host = new FakeHost { VerifySha256Result = false };
        var installer = new UpdateInstaller(host);

        bool result = installer.BeginInstall(CreatePackage());

        result.Should().BeFalse();
        host.StartDetachedCallCount.Should().Be(0);
    }

    [Fact]
    public void BeginInstall_InvalidInstallerHash_ReturnsFalseWithoutWriting()
    {
        var host = new FakeHost();
        var installer = new UpdateInstaller(host);

        bool result = installer.BeginInstall(CreatePackage(expectedSha256: "invalid"));

        result.Should().BeFalse();
        host.WrittenPath.Should().BeNull();
        host.StartDetachedCallCount.Should().Be(0);
    }

    private sealed class TestPackage(
        string installerPath,
        string expectedSha256,
        string stagingDirectory) : IVerifiedUpdatePackage
    {
        public string InstallerPath { get; } = installerPath;

        public string ExpectedSha256 { get; } = expectedSha256;

        public string StagingDirectory { get; } = stagingDirectory;

        public void TransferCleanupToRelauncher()
        {
        }

        public void Dispose()
        {
        }
    }
}
