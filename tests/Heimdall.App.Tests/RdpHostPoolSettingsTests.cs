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
using System.Text.Json;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.App.Tests;

/// <summary>
/// The two settings that bound the RDP control pool, from the settings file to the pool.
/// </summary>
/// <remarks>
/// The pool used to keep two idle controls for the life of the process, which is about 600 MB
/// of private commit after every tab is closed. Its capacity and expiry are now settings, read
/// live: a value written on the settings screen reaches the pool at its next release and its
/// next trim, with no restart. These tests pin the defaults to the pool's own, the shipped
/// defaults file, and the live read through the session manager.
/// </remarks>
public sealed class RdpHostPoolSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "heimdall-host-pool-tests", Guid.NewGuid().ToString("N"));

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
            // Best-effort cleanup of a temp directory.
        }
    }

    [Fact]
    public void TheSettingsDefaultsAreThePoolsOwn()
    {
        AppSettings defaults = new();

        Assert.Equal(ReusableHostPool<RdpActiveXHost>.DefaultCapacity, defaults.RdpHostPoolCapacity);
        Assert.Equal(
            ReusableHostPool<RdpActiveXHost>.DefaultIdleExpiry,
            TimeSpan.FromMinutes(defaults.RdpHostPoolIdleExpiryMinutes));
    }

    [Fact]
    public void WithoutSettings_TheResolversAnswerThePoolsDefaults()
    {
        Assert.Equal(ReusableHostPool<RdpActiveXHost>.DefaultCapacity, PooledRdpHostProvider.ResolveCapacity(null));
        Assert.Equal(ReusableHostPool<RdpActiveXHost>.DefaultIdleExpiry, PooledRdpHostProvider.ResolveIdleExpiry(null));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(-1, 0)]
    public void ResolveCapacity_ReadsTheSettingAndFloorsItAtZero(int configured, int expected)
    {
        Assert.Equal(expected, PooledRdpHostProvider.ResolveCapacity(new AppSettings { RdpHostPoolCapacity = configured }));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(7, 7)]
    public void ResolveIdleExpiry_ReadsTheSettingInMinutes_AndZeroMeansNever(int configured, int expectedMinutes)
    {
        TimeSpan expiry = PooledRdpHostProvider.ResolveIdleExpiry(new AppSettings { RdpHostPoolIdleExpiryMinutes = configured });

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), expiry);
    }

    [Fact]
    public void ShippedDefaultSettings_CarryBothPoolSettings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config", "settings.default.json");
        Assert.True(File.Exists(path), $"settings.default.json not found at {path}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        Assert.True(document.RootElement.TryGetProperty("rdpHostPoolCapacity", out JsonElement capacity));
        Assert.Equal(ReusableHostPool<RdpActiveXHost>.DefaultCapacity, capacity.GetInt32());
        Assert.True(document.RootElement.TryGetProperty("rdpHostPoolIdleExpiryMinutes", out JsonElement expiry));
        Assert.Equal(AppSettings.DefaultRdpHostPoolIdleExpiryMinutes, expiry.GetInt32());
    }

    // A manager built with no configuration source runs on the pool's defaults rather than
    // failing: the tests that build one with nulls, and any diagnostic path, keep working.
    [Fact]
    public void AManagerWithoutAConfigurationSource_RunsOnThePoolsDefaults()
    {
        EmbeddedSessionManager manager = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

        Assert.Equal(ReusableHostPool<RdpActiveXHost>.DefaultCapacity, manager.RdpHostProvider.Capacity);
        Assert.Equal(ReusableHostPool<RdpActiveXHost>.DefaultIdleExpiry, manager.RdpHostProvider.IdleExpiry);
    }

    // The pool reads the settings the configuration manager currently holds, so a save on the
    // settings screen changes what the next release keeps and what the next trim releases.
    [Fact]
    public async Task TheManagerReadsBothSettingsLiveFromTheConfigurationManager()
    {
        ConfigManager configManager = new(_root);
        await configManager.InitializeAsync();
        EmbeddedSessionManager manager = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, configManager);

        await configManager.SaveSettingsAsync(new AppSettings
        {
            RdpHostPoolCapacity = 0,
            RdpHostPoolIdleExpiryMinutes = 0,
        });
        _ = await configManager.LoadSettingsAsync();

        Assert.Equal(0, manager.RdpHostProvider.Capacity);
        Assert.Equal(TimeSpan.Zero, manager.RdpHostProvider.IdleExpiry);

        await configManager.SaveSettingsAsync(new AppSettings
        {
            RdpHostPoolCapacity = 3,
            RdpHostPoolIdleExpiryMinutes = 7,
        });
        _ = await configManager.LoadSettingsAsync();

        Assert.Equal(3, manager.RdpHostProvider.Capacity);
        Assert.Equal(TimeSpan.FromMinutes(7), manager.RdpHostProvider.IdleExpiry);
    }

    [Fact]
    public void DisposingTheManagerTwice_IsHarmless()
    {
        EmbeddedSessionManager manager = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

        manager.Dispose();
        manager.Dispose();
    }
}
