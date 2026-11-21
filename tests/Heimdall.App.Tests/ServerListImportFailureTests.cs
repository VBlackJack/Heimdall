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
using System.Reflection;
using System.Text.Json;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

/// <summary>
/// The import entry points are called from <c>async void</c> handlers, so an exception that
/// escapes them ends the process. A store that cannot be reloaded after an import must be shown
/// to the user instead.
/// </summary>
public sealed class ServerListImportFailureTests
{
    private const string ImportFailedKey = "StatusImportFailed";
    private const string ReloadFailedKey = "ErrorImportReloadFailed";
    private const string FailureReason = "the store is unreadable";

    public static TheoryData<string, string> Failures()
    {
        TheoryData<string, string> data = [];
        foreach (string import in new[] { "rdp", "openssh", "putty" })
        {
            foreach (string exception in new[] { "io", "json", "unauthorized" })
            {
                data.Add(import, exception);
            }
        }

        return data;
    }

    public static TheoryData<string, string> StageFailures()
    {
        TheoryData<string, string> data = [];
        foreach (string import in new[] { "rdp", "openssh", "putty" })
        {
            foreach (string exception in new[] { "io", "json", "unauthorized", "security", "validation", "schema" })
            {
                data.Add(import, exception);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Import_WhenTheStoreCannotBeReloaded_ReportsTheReloadFailureAndDoesNotThrow(string import, string exception)
    {
        (RecordingDialogProxy dialog, LocalizationManager localizer) =
            await RunAsync(import, reloadFailure: CreateException(exception), stageFailure: null);

        string message = Assert.Single(dialog.Errors);
        Assert.Equal(string.Format(localizer[ReloadFailedKey], FailureReason), message);
        Assert.Empty(dialog.Infos);
        Assert.Empty(dialog.Warnings);
    }

    [Theory]
    [MemberData(nameof(StageFailures))]
    public async Task Import_WhenTheFlowFailsBeforeTheReload_ReportsAnImportFailureAndDoesNotThrow(string import, string exception)
    {
        Exception failure = CreateException(exception);
        (RecordingDialogProxy dialog, LocalizationManager localizer) =
            await RunAsync(import, reloadFailure: null, stageFailure: failure);

        string message = Assert.Single(dialog.Errors);
        Assert.Equal(string.Format(localizer[ImportFailedKey], failure.Message), message);
        Assert.Empty(dialog.Infos);
        Assert.Empty(dialog.Warnings);
    }

    private static async Task<(RecordingDialogProxy Dialog, LocalizationManager Localizer)> RunAsync(
        string import,
        Exception? reloadFailure,
        Exception? stageFailure)
    {
        string root = Path.Combine(Path.GetTempPath(), "heimdall-import-failure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            IConfigManager configManager = ThrowingConfigProxy.Create(reloadFailure);
            IDialogService dialogService = DispatchProxy.Create<IDialogService, RecordingDialogProxy>();
            RecordingDialogProxy dialog = (RecordingDialogProxy)(object)dialogService;
            dialog.ImportDialogFailure = stageFailure;

            using ConnectionService connectionService = new(
                configManager,
                localizer,
                new NullTunnelService(),
                Array.Empty<IProtocolHandler>());
            RawPuttySession[] puttySessions = import == "putty"
                ? [new RawPuttySession("alias", new Dictionary<string, object?> { ["HostName"] = "host.example", ["Protocol"] = "ssh" })]
                : [];
            using ServerListViewModel viewModel = new(
                configManager,
                localizer,
                new FakeUiDispatcher(),
                new ConnectionStateMachine(),
                connectionService,
                dialogService,
                null!,
                new PuttySessionImporter(new FixedPuttyRegistrySource(puttySessions), configManager),
                new KnownHostsImporter(configManager, new HostKeyStore()),
                profileImportService: new ChangedProfileImportService(stageFailure));

            string sshConfig = Path.Combine(root, "config");
            await File.WriteAllTextAsync(sshConfig, "Host alpha\n  HostName alpha.example\n");

            Task task = import switch
            {
                "rdp" => viewModel.ImportRdpFilesAsync([Path.Combine(root, "a.rdp")]),
                "openssh" => viewModel.ImportOpenSshConfigAsync(sshConfig),
                _ => viewModel.ImportPuttySessionsAsync(),
            };
            await task;

            return (dialog, localizer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Exception CreateException(string kind) => kind switch
    {
        "io" => new IOException(FailureReason),
        "json" => new JsonException(FailureReason),
        "unauthorized" => new UnauthorizedAccessException(FailureReason),
        "security" => new System.Security.SecurityException(FailureReason),
        "validation" => new ConfigurationValidationException("servers.json", [FailureReason]),
        "schema" => new ConfigurationSchemaVersionException("servers.json", "servers.json", 9, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public class ThrowingConfigProxy : DispatchProxy
    {
        private Exception? _failure;

        public static IConfigManager Create(Exception? failure)
        {
            IConfigManager proxy = Create<IConfigManager, ThrowingConfigProxy>();
            ((ThrowingConfigProxy)(object)proxy)._failure = failure;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (_failure is not null && targetMethod.Name is "LoadSettingsAsync" or "LoadServersAsync")
            {
                Type result = targetMethod.ReturnType.GetGenericArguments()[0];
                return typeof(ServerListImportFailureTests)
                    .GetMethod(nameof(Faulted), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(result)
                    .Invoke(null, [_failure]);
            }

            return DefaultFor(targetMethod.ReturnType);
        }
    }

    public class RecordingDialogProxy : DispatchProxy
    {
        public List<string> Errors { get; } = [];

        public List<string> Infos { get; } = [];

        public List<string> Warnings { get; } = [];

        public Exception? ImportDialogFailure { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            switch (targetMethod.Name)
            {
                case "ShowError":
                    Errors.Add((string)args![1]!);
                    return null;
                case "ShowInfo":
                    Infos.Add((string)args![1]!);
                    return null;
                case "ShowWarning":
                    Warnings.Add((string)args![1]!);
                    return null;
                case "ShowImportOpenSshConfigAsync":
                case "ShowImportPuttySessionsAsync":
                    if (ImportDialogFailure is not null)
                    {
                        return Task.FromException<ImportOutcome?>(ImportDialogFailure);
                    }

                    return Task.FromResult<ImportOutcome?>(new ImportOutcome(1, 0));
                default:
                    return DefaultFor(targetMethod.ReturnType);
            }
        }
    }

    private static Task<TResult> Faulted<TResult>(Exception failure) => Task.FromException<TResult>(failure);

    private static object? DefaultFor(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType == typeof(ValueTask))
        {
            return default(ValueTask);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            Type result = returnType.GetGenericArguments()[0];
            object? value = result.IsValueType ? Activator.CreateInstance(result) : null;
            return typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(result)
                .Invoke(null, [value]);
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }

    private sealed class ChangedProfileImportService(Exception? failure) : IProfileImportService
    {
        public Task<ProfileImportResult> ImportFromPathAsync(string path, CancellationToken ct = default) =>
            ImportFromPathsAsync([path], ct);

        public Task<ProfileImportResult> ImportFromPathsAsync(IEnumerable<string> paths, CancellationToken ct = default) =>
            failure is null
                ? Task.FromResult(new ProfileImportResult { HasChanges = true })
                : Task.FromException<ProfileImportResult>(failure);
    }

    private sealed class FixedPuttyRegistrySource(RawPuttySession[] sessions) : IPuttySessionRegistrySource
    {
        public Task<IReadOnlyList<RawPuttySession>> ReadSessionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RawPuttySession>>(sessions);
    }

    private sealed class NullTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false) =>
            Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }
}
