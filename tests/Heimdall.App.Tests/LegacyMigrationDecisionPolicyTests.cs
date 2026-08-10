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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class LegacyMigrationDecisionPolicyTests : IDisposable
{
    private readonly string _rootPath;
    private readonly string _legacyPath;
    private readonly string _currentPath;

    public LegacyMigrationDecisionPolicyTests()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(),
            "heimdall-legacy-decision-tests",
            Guid.NewGuid().ToString("N"));
        _legacyPath = Path.Combine(_rootPath, "legacy");
        _currentPath = Path.Combine(_rootPath, "current");
        Directory.CreateDirectory(Path.Combine(_legacyPath, "config"));
        Directory.CreateDirectory(_currentPath);
    }

    [Fact]
    public async Task RecordDeclineAsync_SameOfferSuppressesAndPersistsOnlyVersionAndFingerprint()
    {
        const string fakeSecret = "UXG028-FAKE-SECRET-DO-NOT-PERSIST";
        await File.WriteAllTextAsync(
            Path.Combine(_legacyPath, "config", "settings.json"),
            $"{{\"secret\":\"{fakeSecret}\"}}");
        await File.WriteAllTextAsync(
            Path.Combine(_legacyPath, "config", "servers.json"),
            "[]");
        ConfigManager configManager = new(_currentPath);
        await configManager.InitializeAsync();
        LegacyMigrationOffer offer = await LegacyMigrationDecisionPolicy.CreateOfferAsync(
            _legacyPath);

        await LegacyMigrationDecisionPolicy.RecordDeclineAsync(configManager, offer);

        ConfigManager reloadedManager = new(_currentPath);
        await reloadedManager.InitializeAsync();
        AppSettings reloaded = await reloadedManager.LoadSettingsAsync();
        string persistedJson = await File.ReadAllTextAsync(reloadedManager.SettingsPath);
        Assert.Equal(offer.Version, reloaded.LegacyMigrationDeclinedOfferVersion);
        Assert.Equal(
            offer.SourceFingerprint,
            reloaded.LegacyMigrationDeclinedSourceFingerprint);
        Assert.False(LegacyMigrationDecisionPolicy.ShouldOffer(reloaded, offer));
        Assert.DoesNotContain(fakeSecret, persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(_legacyPath, persistedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShouldOffer_ChangedSourceOrOlderStoredVersion_Reoffers()
    {
        await WriteLegacyFilesAsync("{}", "[]");
        LegacyMigrationOffer original = await LegacyMigrationDecisionPolicy.CreateOfferAsync(
            _legacyPath);
        AppSettings settings = new()
        {
            LegacyMigrationDeclinedOfferVersion = original.Version,
            LegacyMigrationDeclinedSourceFingerprint = original.SourceFingerprint,
        };
        await File.WriteAllTextAsync(
            Path.Combine(_legacyPath, "config", "servers.json"),
            "[{}]");
        LegacyMigrationOffer changed = await LegacyMigrationDecisionPolicy.CreateOfferAsync(
            _legacyPath);

        Assert.True(LegacyMigrationDecisionPolicy.ShouldOffer(settings, changed));

        settings.LegacyMigrationDeclinedSourceFingerprint = changed.SourceFingerprint;
        settings.LegacyMigrationDeclinedOfferVersion = changed.Version - 1;
        Assert.True(LegacyMigrationDecisionPolicy.ShouldOffer(settings, changed));
    }

    [Fact]
    public async Task ShouldOffer_NewerStoredVersionWithSameFingerprint_Suppresses()
    {
        await WriteLegacyFilesAsync("{}", "[]");
        LegacyMigrationOffer offer = await LegacyMigrationDecisionPolicy.CreateOfferAsync(
            _legacyPath);
        AppSettings settings = new()
        {
            LegacyMigrationDeclinedOfferVersion = offer.Version + 1,
            LegacyMigrationDeclinedSourceFingerprint = offer.SourceFingerprint,
        };

        Assert.False(LegacyMigrationDecisionPolicy.ShouldOffer(settings, offer));
    }

    [Fact]
    public async Task CreateOfferAsync_LengthDelimitsFilesInFixedOrder()
    {
        await WriteLegacyFilesAsync("ab", "c");
        LegacyMigrationOffer first = await LegacyMigrationDecisionPolicy.CreateOfferAsync(
            _legacyPath);

        await WriteLegacyFilesAsync("a", "bc");
        LegacyMigrationOffer second = await LegacyMigrationDecisionPolicy.CreateOfferAsync(
            _legacyPath);

        Assert.NotEqual(first.SourceFingerprint, second.SourceFingerprint);
        Assert.Equal(64, first.SourceFingerprint.Length);
        Assert.Equal(first.SourceFingerprint.ToUpperInvariant(), first.SourceFingerprint);
    }

    [Fact]
    public async Task ClearDeclineAsync_RemovesBothPersistedMarkers()
    {
        ConfigManager configManager = new(_currentPath);
        await configManager.InitializeAsync();
        await configManager.MergeSettingAsync(settings =>
        {
            settings.LegacyMigrationDeclinedOfferVersion = 3;
            settings.LegacyMigrationDeclinedSourceFingerprint = "ABC123";
        });

        await LegacyMigrationDecisionPolicy.ClearDeclineAsync(configManager);

        ConfigManager reloadedManager = new(_currentPath);
        await reloadedManager.InitializeAsync();
        AppSettings reloaded = await reloadedManager.LoadSettingsAsync();
        Assert.Equal(0, reloaded.LegacyMigrationDeclinedOfferVersion);
        Assert.Null(reloaded.LegacyMigrationDeclinedSourceFingerprint);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temporary test files.
        }
    }

    private async Task WriteLegacyFilesAsync(string settingsJson, string serversJson)
    {
        await File.WriteAllTextAsync(
            Path.Combine(_legacyPath, "config", "settings.json"),
            settingsJson);
        await File.WriteAllTextAsync(
            Path.Combine(_legacyPath, "config", "servers.json"),
            serversJson);
    }
}
