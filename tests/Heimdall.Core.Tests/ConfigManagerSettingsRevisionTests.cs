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

using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

public sealed class ConfigManagerSettingsRevisionTests : IDisposable
{
    private readonly string _tempDirectory;

    public ConfigManagerSettingsRevisionTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "Heimdall.SettingsRevision.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "config"));
    }

    public void Dispose()
    {
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
    public async Task StaleLoad_DoesNotOverwriteNewerPublishedSettings()
    {
        PausedLoadFixture fixture = await CreatePausedLoadFixtureAsync();

        await fixture.Manager.SaveSettingsAsync(new AppSettings { DefaultTheme = "New" });
        Assert.Equal("New", fixture.Manager.CurrentSettings?.DefaultTheme);

        fixture.ResumeLoad.TrySetResult();
        AppSettings staleResult = await fixture.StaleLoad;

        Assert.Equal("Old", staleResult.DefaultTheme);
        Assert.Equal("New", fixture.Manager.CurrentSettings?.DefaultTheme);
    }

    [Fact]
    public async Task Save_PublishesMonotonicRevision()
    {
        PausedLoadFixture fixture = await CreatePausedLoadFixtureAsync();
        AppSettings? eventSettings = null;
        fixture.Manager.SettingsChanged += settings => eventSettings = settings;
        var settingsToSave = new AppSettings { DefaultTheme = "New" };

        await fixture.Manager.SaveSettingsAsync(settingsToSave);
        long committedRevision = fixture.Manager.CurrentSettingsRevision;
        settingsToSave.DefaultTheme = "Mutated After Save";

        Assert.True(committedRevision > 0);
        Assert.Equal("New", fixture.Manager.CurrentSettings?.DefaultTheme);
        Assert.NotNull(eventSettings);
        Assert.NotSame(settingsToSave, eventSettings);
        eventSettings!.DefaultTheme = "Mutated Event Snapshot";
        Assert.Equal("New", fixture.Manager.CurrentSettings?.DefaultTheme);
        AppSettings publishedSnapshot = Assert.IsType<AppSettings>(fixture.Manager.CurrentSettings);
        publishedSnapshot.DefaultTheme = "Mutated Published Snapshot";
        Assert.Equal("New", fixture.Manager.CurrentSettings?.DefaultTheme);

        fixture.ResumeLoad.TrySetResult();
        await fixture.StaleLoad;

        Assert.Equal(committedRevision, fixture.Manager.CurrentSettingsRevision);
        Assert.Equal("New", fixture.Manager.CurrentSettings?.DefaultTheme);
    }

    private async Task<PausedLoadFixture> CreatePausedLoadFixtureAsync()
    {
        var loadReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeLoad = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hookInvocationCount = 0;
        var manager = new ConfigManager(
            _tempDirectory,
            Path.Combine(_tempDirectory, "config"),
            async () =>
            {
                if (Interlocked.Increment(ref hookInvocationCount) == 1)
                {
                    loadReady.TrySetResult();
                    await resumeLoad.Task.ConfigureAwait(false);
                }
            });
        await manager.SaveSettingsAsync(new AppSettings { DefaultTheme = "Old" });

        Task<AppSettings> staleLoad = manager.LoadSettingsAsync();
        await loadReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return new PausedLoadFixture(manager, staleLoad, resumeLoad);
    }

    private sealed record PausedLoadFixture(
        ConfigManager Manager,
        Task<AppSettings> StaleLoad,
        TaskCompletionSource ResumeLoad);
}
