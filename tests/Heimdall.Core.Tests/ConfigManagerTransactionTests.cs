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

/// <summary>
/// Pins the settings/servers commit as one unit: what is restored on failure, what is never published
/// before both writes are durable, and what a subscriber can and cannot do to its neighbours.
/// </summary>
public sealed class ConfigManagerTransactionTests : IDisposable
{
    private readonly string _root;
    private readonly ConfigManager _configManager;

    public ConfigManagerTransactionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Heimdall.Txn." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _configManager = new ConfigManager(_root);

        // The manager derives its paths but does not create the directory until it writes.
        Directory.CreateDirectory(Path.GetDirectoryName(_configManager.SettingsPath)!);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    // Byte identity, not logical equivalence: a baseline carrying a byte-order mark and non-canonical
    // whitespace must come back exactly as it was, with no silent normalisation.
    [Fact]
    public async Task CommitMigration_ServersWriteFails_RestoresSettingsBytesExactly()
    {
        byte[] settingsBaseline =
        [
            0xEF, 0xBB, 0xBF, // BOM
            .. "{\r\n\t\"DefaultLocale\" :  \"en\"\r\n}\r\n"u8.ToArray(),
        ];
        await File.WriteAllBytesAsync(_configManager.SettingsPath, settingsBaseline);

        // A schema this build refuses for writing, so the servers step fails only after settings landed.
        await File.WriteAllTextAsync(
            _configManager.ServersPath,
            """{ "SchemaVersion": 99999, "Servers": [] }""");
        byte[] serversBaseline = await File.ReadAllBytesAsync(_configManager.ServersPath);

        int notifications = 0;
        _configManager.SettingsChanged += _ => notifications++;

        await Assert.ThrowsAnyAsync<Exception>(() => Commit(
            settings => settings.DefaultLocale = "fr",
            servers => servers.Clear()));

        Assert.Equal(settingsBaseline, await File.ReadAllBytesAsync(_configManager.SettingsPath));
        Assert.Equal(serversBaseline, await File.ReadAllBytesAsync(_configManager.ServersPath));
        Assert.Equal(0, notifications);
    }

    // A file that did not exist must not be replaced by an empty or serialised one.
    [Fact]
    public async Task CommitMigration_ServersWriteFails_LeavesAnAbsentSettingsFileAbsent()
    {
        Assert.False(File.Exists(_configManager.SettingsPath));

        await File.WriteAllTextAsync(
            _configManager.ServersPath,
            """{ "SchemaVersion": 99999, "Servers": [] }""");

        await Assert.ThrowsAnyAsync<Exception>(() => Commit(
            settings => settings.DefaultLocale = "fr",
            servers => servers.Clear()));

        Assert.False(File.Exists(_configManager.SettingsPath));
    }

    // Nothing is published before both writes are durable, so a failed transaction leaves the runtime
    // snapshot untouched rather than restoring it afterwards.
    [Fact]
    public async Task CommitMigration_Fails_LeavesTheRuntimeSnapshotUntouched()
    {
        await _configManager.SaveSettingsAsync(new AppSettings { DefaultLocale = "en" });
        AppSettings? baseline = _configManager.CurrentSettings;
        Assert.NotNull(baseline);

        await File.WriteAllTextAsync(
            _configManager.ServersPath,
            """{ "SchemaVersion": 99999, "Servers": [] }""");

        await Assert.ThrowsAnyAsync<Exception>(() => Commit(
            settings => settings.DefaultLocale = "fr",
            servers => servers.Clear()));

        AppSettings? after = _configManager.CurrentSettings;
        Assert.NotNull(after);
        Assert.Equal("en", after!.DefaultLocale);
        Assert.Equal(baseline!.DefaultLocale, after.DefaultLocale);
    }

    // A subscriber that throws must not silence the ones after it, and a subscriber that mutates its
    // argument must not hand a corrupted object to the next.
    [Fact]
    public async Task SettingsChanged_FirstSubscriberMutatesAndThrows_NextStillGetsAnIntactCandidate()
    {
        string? secondSaw = null;
        int secondCalls = 0;

        _configManager.SettingsChanged += settings =>
        {
            settings.DefaultLocale = "corrupted-by-first";
            throw new InvalidOperationException("subscriber failure");
        };
        _configManager.SettingsChanged += settings =>
        {
            secondCalls++;
            secondSaw = settings.DefaultLocale;
        };

        await _configManager.SaveSettingsAsync(new AppSettings { DefaultLocale = "fr" });

        Assert.Equal(1, secondCalls);
        Assert.Equal("fr", secondSaw);
    }

    // An observer's failure never turns an already-durable write into a failed one.
    [Fact]
    public async Task SettingsChanged_SubscriberThrows_DoesNotFailTheWrite()
    {
        _configManager.SettingsChanged += _ => throw new InvalidOperationException("subscriber failure");

        await _configManager.SaveSettingsAsync(new AppSettings { DefaultLocale = "fr" });

        AppSettings persisted = await _configManager.LoadSettingsAsync();
        Assert.Equal("fr", persisted.DefaultLocale);
    }

    // The second write can change its target and then fail before returning. That window is what the
    // server rollback exists for, and without driving it deterministically the branch is unprovable.
    [Fact]
    public async Task CommitMigration_ServerWriteSucceedsThenFails_RestoresBothFilesExactly()
    {
        await _configManager.SaveSettingsAsync(new AppSettings { DefaultLocale = "en" });
        await _configManager.MutateServersAsync(inventory =>
        {
            inventory.Add(new ServerProfileDto { Id = "keep", DisplayName = "keep" });
            return inventory.Count;
        });

        byte[] settingsBaseline = await File.ReadAllBytesAsync(_configManager.SettingsPath);
        byte[] serversBaseline = await File.ReadAllBytesAsync(_configManager.ServersPath);
        AppSettings? runtimeBaseline = _configManager.CurrentSettings;
        Assert.NotNull(runtimeBaseline);

        int notifications = 0;
        _configManager.SettingsChanged += _ => notifications++;

        bool serversActuallyChanged = false;
        _configManager.AfterTransactionalServerWriteAsync = async () =>
        {
            // The new inventory really is on disk at this point; only then does the step fail.
            byte[] midTransaction = await File.ReadAllBytesAsync(_configManager.ServersPath);
            serversActuallyChanged = !midTransaction.SequenceEqual(serversBaseline);
            throw new IOException("server write failed after modifying its target");
        };

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Commit(
                settings => settings.DefaultLocale = "fr",
                servers => servers.Clear()));
        }
        finally
        {
            _configManager.AfterTransactionalServerWriteAsync = null;
        }

        // Non-vacuous: the target really had been modified before the failure.
        Assert.True(serversActuallyChanged, "the server write must have changed its target before failing");

        Assert.Equal(settingsBaseline, await File.ReadAllBytesAsync(_configManager.SettingsPath));
        Assert.Equal(serversBaseline, await File.ReadAllBytesAsync(_configManager.ServersPath));
        Assert.Single(await _configManager.LoadServersAsync());

        AppSettings? runtimeAfter = _configManager.CurrentSettings;
        Assert.NotNull(runtimeAfter);
        Assert.Equal(runtimeBaseline!.DefaultLocale, runtimeAfter!.DefaultLocale);
        Assert.Equal(0, notifications);
    }

    // Baselines can hold encrypted secrets and credential references. No honest behavioural oracle can
    // observe a freed buffer, so this is pinned structurally and bounded to the commit's finally block:
    // a global search would match unrelated clears elsewhere in the file.
    [Fact]
    public void CommitMigration_ClearsBothBaselineBuffers()
    {
        string source = ReadConfigManagerSource();
        int commitIndex = source.IndexOf(
            "public async Task CommitMigrationAsync(",
            StringComparison.Ordinal);
        Assert.True(commitIndex >= 0, "the transactional commit must exist");

        int finallyIndex = source.IndexOf("        finally", commitIndex, StringComparison.Ordinal);
        Assert.True(finallyIndex > commitIndex, "the commit must have a finally block");

        int releaseIndex = source.IndexOf("_writeLock.Release();", finallyIndex, StringComparison.Ordinal);
        Assert.True(releaseIndex > finallyIndex, "the finally must release the write lock");

        string finallyBlock = source[finallyIndex..releaseIndex];
        Assert.Contains("Array.Clear(settingsBaseline);", finallyBlock, StringComparison.Ordinal);
        Assert.Contains("Array.Clear(serversBaseline);", finallyBlock, StringComparison.Ordinal);
    }

    private static string ReadConfigManagerSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            directory!.FullName,
            "src",
            "Heimdall.Core",
            "Configuration",
            "ConfigManager.cs"));
    }

    private Task Commit(Action<AppSettings> settingsMutation, Action<List<ServerProfileDto>> serversMutation)
        => ((IConfigTransactionalWriter)_configManager).CommitMigrationAsync(settingsMutation, serversMutation);
}
