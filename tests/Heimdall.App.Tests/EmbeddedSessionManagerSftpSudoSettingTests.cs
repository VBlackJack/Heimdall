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
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The sudo button of every live SFTP pane follows the setting: the session manager hears the
/// configuration change and has each pane read it again, and stops hearing it once disposed.
/// </summary>
public sealed class EmbeddedSessionManagerSftpSudoSettingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "heimdall-sftp-sudo-setting", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ASettingsChange_RefreshesTheSudoButtonOfEveryLivePane()
    {
        ConfigManager config = new(_root);
        await config.InitializeAsync();
        using EmbeddedSessionManager manager = CreateManager(config);
        SftpSudoFallbackMode mode = SftpSudoFallbackMode.Ask;
        EmbeddedSftpViewModel pane = SftpTestKit.CreateViewModel();
        pane.SetSudoFallbackModeProvider(() => mode);
        manager.TrackSftpViewModel(pane);
        await pane.ToggleSudoMode();
        Assert.True(pane.SudoMode);
        List<string?> raised = [];
        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        mode = SftpSudoFallbackMode.Never;
        await config.SaveSettingsAsync(new AppSettings());

        Assert.Contains(nameof(EmbeddedSftpViewModel.IsSudoModeAvailable), raised);
        Assert.False(pane.IsSudoModeAvailable);
        Assert.False(pane.SudoMode);
    }

    [Fact]
    public async Task ADisposedManager_NoLongerRefreshesThePanes()
    {
        ConfigManager config = new(_root);
        await config.InitializeAsync();
        EmbeddedSessionManager manager = CreateManager(config);
        EmbeddedSftpViewModel pane = SftpTestKit.CreateViewModel();
        manager.TrackSftpViewModel(pane);
        List<string?> raised = [];
        pane.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        manager.Dispose();
        await config.SaveSettingsAsync(new AppSettings());

        Assert.DoesNotContain(nameof(EmbeddedSftpViewModel.IsSudoModeAvailable), raised);
    }

    // Only the configuration manager matters here; the other dependencies stay null so a new
    // dependency of this path surfaces as a failure instead of being silently tolerated.
    private static EmbeddedSessionManager CreateManager(ConfigManager config)
        => new(null!, null!, null!, null!, null!, null!, null!, null!, null!, config);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
