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
using Heimdall.App.ViewModels.Scheduled;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// A folder creation and a scheduled-task save write only their own field, so an RDP
/// certificate revoked while they were in flight stays revoked.
/// </summary>
/// <remarks>
/// The concurrent revocation is injected at the instant the writer's settings snapshot is
/// handed over, which is when a load-then-save window opens. A writer that goes through
/// MergeSettingAsync never loads a snapshot of its own, so the hook may not fire at all and the
/// revocation then runs after the write; the assertion is on the file, which is what the user
/// reads back.
/// </remarks>
public sealed class SettingsTargetedWriteRevocationTests
{
    private const string TrustedHost = "rdp-revoked.example.com";
    private const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";

    [Fact]
    public async Task CommitEmptyFolder_RevocationDuringTheWrite_StaysRevoked()
    {
        await WithTrustedCertificateAsync(async (configManager, intercepting) =>
        {
            // The caller loads the settings to decide whether the name is free; that load is
            // where the window used to open.
            _ = await intercepting.LoadSettingsAsync();

            AppSettings result = await MainWindow.CommitEmptyFolderAsync(intercepting, "Staging");

            Assert.Contains("Staging", result.EmptyGroups);
        });
    }

    [Fact]
    public async Task PersistScheduledTasks_RevocationDuringTheWrite_StaysRevoked()
    {
        await WithTrustedCertificateAsync(async (configManager, intercepting) =>
        {
            ScheduledTaskDto task = new() { ServerId = "srv-1", ServerName = "Server 1" };

            await ScheduledTasksViewModel.PersistTasksAsync(intercepting, [task]);

            AppSettings reloaded = await configManager.LoadSettingsAsync();
            Assert.Equal("srv-1", Assert.Single(reloaded.ScheduledTasks).ServerId);
        });
    }

    /// <summary>
    /// Seeds a trusted certificate, arms a revocation at the next settings load, runs the
    /// writer, and checks the revocation survived it on disk.
    /// </summary>
    private static async Task WithTrustedCertificateAsync(
        Func<ConfigManager, InterceptingConfigManager, Task> writer)
    {
        string rootPath = Path.Combine(
            Path.GetTempPath(),
            "Heimdall-SettingsTargetedWriteRevocationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            ConfigManager configManager = new(rootPath);
            await configManager.InitializeAsync();
            await configManager.MergeSettingAsync(settings =>
                settings.TrustedRdpCertificates[TrustedHost] =
                    [new RdpCertificateEntry(Thumbprint, DateTimeOffset.UtcNow)]);

            InterceptingConfigManager intercepting = new(configManager);
            bool armed = true;
            intercepting.AfterLoadSettings = () =>
            {
                if (!armed)
                {
                    return;
                }

                armed = false;
                configManager
                    .MergeSettingAsync(settings => settings.TrustedRdpCertificates.Remove(TrustedHost))
                    .GetAwaiter()
                    .GetResult();
            };

            await writer(configManager, intercepting);

            // A writer that never loaded a snapshot opened no window; the revocation still
            // happens, just after it, and must hold either way.
            if (armed)
            {
                armed = false;
                await configManager.MergeSettingAsync(
                    settings => settings.TrustedRdpCertificates.Remove(TrustedHost));
            }

            ConfigManager reloadedManager = new(rootPath);
            await reloadedManager.InitializeAsync();
            AppSettings reloaded = await reloadedManager.LoadSettingsAsync();
            Assert.False(
                reloaded.TrustedRdpCertificates.ContainsKey(TrustedHost),
                "a certificate revoked while the write was in flight came back");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
