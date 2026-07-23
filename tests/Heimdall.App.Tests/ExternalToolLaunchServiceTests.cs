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
using System.IO;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Tests;

[CollectionDefinition("ExternalToolLaunchFileLogger", DisableParallelization = true)]
public sealed class ExternalToolLaunchFileLoggerCollectionDefinition;

[Collection("ExternalToolLaunchFileLogger")]
public sealed class ExternalToolLaunchServiceTests : IDisposable
{
    private readonly string _tempDirectory;

    public ExternalToolLaunchServiceTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "Heimdall.ExternalToolLaunch.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        FileLogger.SetEnabled(true);
        FileLogger.Initialize(_tempDirectory, flushIntervalMs: 60000);
    }

    public void Dispose()
    {
        FileLogger.SetEnabled(false);
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void LaunchConfigured_SecretArguments_AreNotWrittenToLog()
    {
        const string secret = "top-secret-token-value";
        ProcessStartInfo? capturedStartInfo = null;
        var service = new ExternalToolLaunchService(
            new NullDialogService(),
            startInfo =>
            {
                capturedStartInfo = startInfo;
                return null;
            });
        var tool = new ExternalToolDefinition
        {
            Name = "Token Inspector",
            ExecutablePath = "safe-tool.exe",
            Arguments = $"--token {secret}"
        };

        service.LaunchConfigured(tool, server: null, static key => key);

        Assert.NotNull(capturedStartInfo);
        Assert.Contains(secret, capturedStartInfo!.Arguments, StringComparison.Ordinal);
        string log = ReadLog();
        Assert.Contains("Token Inspector", log, StringComparison.Ordinal);
        Assert.Contains("safe-tool.exe", log, StringComparison.Ordinal);
        Assert.Contains("succeeded", log, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        Assert.DoesNotContain("--token", log, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchConfigured_FailureLog_DoesNotIncludeExceptionText()
    {
        const string secret = "exception-secret-token-value";
        var service = new ExternalToolLaunchService(
            new NullDialogService(),
            _ => throw new InvalidOperationException($"Launch failed for --token {secret}."));
        var tool = new ExternalToolDefinition
        {
            Name = "Token Inspector",
            ExecutablePath = "safe-tool.exe",
            Arguments = $"--token {secret}"
        };

        service.LaunchConfigured(tool, server: null, static key => key);

        string log = ReadLog();
        Assert.Contains("Token Inspector", log, StringComparison.Ordinal);
        Assert.Contains("safe-tool.exe", log, StringComparison.Ordinal);
        Assert.Contains("failed", log, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        Assert.DoesNotContain("--token", log, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchDetected_SecretArguments_AreNotWrittenToLog()
    {
        const string secret = "detected-secret-token-value";
        ProcessStartInfo? capturedStartInfo = null;
        var service = new ExternalToolLaunchService(
            new NullDialogService(),
            startInfo =>
            {
                capturedStartInfo = startInfo;
                return null;
            });
        var tool = new ExternalToolInfo
        {
            Id = "SAFE",
            Name = "Detected Inspector",
            ProviderName = "Test Provider",
            ExecutablePath = "detected-tool.exe",
            Arguments = $"--token {secret} --host {{Host}}"
        };
        ServerItemViewModel server = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = "server-1",
            DisplayName = "Server",
            RemoteServer = "server.example.test",
            ConnectionType = "SSH"
        });

        service.LaunchDetected(tool, server, static key => key);

        Assert.NotNull(capturedStartInfo);
        Assert.Contains(secret, capturedStartInfo!.Arguments, StringComparison.Ordinal);
        string log = ReadLog();
        Assert.Contains("Test Provider/Detected Inspector", log, StringComparison.Ordinal);
        Assert.Contains("detected-tool.exe", log, StringComparison.Ordinal);
        Assert.Contains("succeeded", log, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        Assert.DoesNotContain("--token", log, StringComparison.Ordinal);
    }

    private string ReadLog()
    {
        FileLogger.Flush();
        string logFile = Assert.Single(Directory.GetFiles(_tempDirectory, "heimdall_*.log"));
        return File.ReadAllText(logFile);
    }

    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info") =>
            Task.FromResult(false);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) =>
            Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null) =>
            Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null) =>
            Task.FromResult<GatewayDialogResult?>(null);

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null) =>
            Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(
            ScheduledTaskDialogViewModel? editVm = null) =>
            Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel) =>
            Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(
            SnapshotRestoreDialogViewModel viewModel) =>
            Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel) =>
            Task.FromResult<RdpImportSelection?>(null);

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult) =>
            Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult) =>
            Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview) =>
            Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel) =>
            Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel) =>
            Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null) =>
            Task.FromResult<CommandLibraryPickerResult?>(null);

        public Task<int?> ShowBulkEditPortAsync(
            int count,
            int? initialPort,
            CancellationToken cancellationToken) =>
            Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(
            int count,
            string? initialUsername,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(
            int count,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public void ShowError(string title, string message)
        {
        }

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowWarning(string title, string message)
        {
        }
    }
}
