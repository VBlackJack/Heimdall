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
using Xunit;

namespace Heimdall.App.Tests;

/// <summary>
/// The bundled plink is the attested build: the legacy default path must not shadow it.
/// </summary>
public sealed class ConnectionHelpersPlinkPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heimdall-plinkpath-" + Guid.NewGuid().ToString("N"));

    public ConnectionHelpersPlinkPathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string CreateBundled()
    {
        string baseDir = Path.Combine(_root, "app");
        string tools = Path.Combine(baseDir, AppConstants.EmbeddedToolsSubdir);
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "plink.exe"), "bundled");
        return baseDir;
    }

    private string CreateLegacy()
    {
        string dir = Path.Combine(_root, "putty");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "plink.exe");
        File.WriteAllText(path, "system");
        return path;
    }

    private static string BundledPath(string baseDir) =>
        Path.Combine(baseDir, AppConstants.EmbeddedToolsSubdir, "plink.exe");

    [Fact]
    public void ResolvePlinkPath_LegacyDefaultPresent_PrefersBundled()
    {
        string baseDir = CreateBundled();
        string legacy = CreateLegacy();

        // Different casing must still count as the legacy default.
        string? resolved = ConnectionHelpers.ResolvePlinkPath(legacy.ToUpperInvariant(), baseDir, legacy);

        Assert.Equal(BundledPath(baseDir), resolved);
    }

    [Fact]
    public void ResolvePlinkPath_BlankSetting_PrefersBundled()
    {
        string baseDir = CreateBundled();

        Assert.Equal(BundledPath(baseDir), ConnectionHelpers.ResolvePlinkPath(null, baseDir));
        Assert.Equal(BundledPath(baseDir), ConnectionHelpers.ResolvePlinkPath("  ", baseDir));
    }

    [Fact]
    public void ResolvePlinkPath_UserChosenPath_IsHonoured()
    {
        string baseDir = CreateBundled();
        string legacy = CreateLegacy();
        string chosen = Path.Combine(_root, "mine.exe");
        File.WriteAllText(chosen, "mine");

        Assert.Equal(chosen, ConnectionHelpers.ResolvePlinkPath(chosen, baseDir, legacy));
    }

    [Fact]
    public void ResolvePlinkPath_NoBundled_FallsBackToTheLegacyPath()
    {
        string legacy = CreateLegacy();
        string emptyBase = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyBase);

        Assert.Equal(legacy, ConnectionHelpers.ResolvePlinkPath(legacy, emptyBase, legacy));
    }

    [Fact]
    public void ResolvePlinkPath_NothingAnywhere_ReturnsWhatItReturnedBefore()
    {
        string emptyBase = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyBase);
        string missing = Path.Combine(_root, "missing", "plink.exe");

        Assert.Null(ConnectionHelpers.ResolvePlinkPath(missing, emptyBase, missing));
        Assert.Null(ConnectionHelpers.ResolvePlinkPath(null, emptyBase));
    }

    [Fact]
    public void ResolvePlinkPath_LegacyDefaultWithForwardSlashes_IsTreatedAsLegacy()
    {
        string baseDir = CreateBundled();
        string legacy = CreateLegacy();

        string? resolved = ConnectionHelpers.ResolvePlinkPath(legacy.Replace('\\', '/'), baseDir, legacy);

        Assert.Equal(BundledPath(baseDir), resolved);
    }

    [Fact]
    public void ResolvePlinkPath_UserChosenPathMissing_BundledPresent_ReturnsBundled()
    {
        string baseDir = CreateBundled();
        string legacy = CreateLegacy();
        string missing = Path.Combine(_root, "gone", "plink.exe");

        Assert.Equal(BundledPath(baseDir), ConnectionHelpers.ResolvePlinkPath(missing, baseDir, legacy));
    }

    [Fact]
    public void ResolvePlinkPath_WhitespaceSetting_NoBundled_ReturnsNull()
    {
        string emptyBase = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyBase);

        Assert.Null(ConnectionHelpers.ResolvePlinkPath("   ", emptyBase));
    }

    [Fact]
    public void DefaultSettingsJson_PlinkPath_EqualsLegacyConstant()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config", "settings.default.json");
        Assert.True(File.Exists(path), $"settings.default.json not found at {path}");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        string? configured = doc.RootElement.GetProperty("plinkPath").GetString();

        Assert.Equal(AppSettings.LegacyPlinkPath, configured);
        Assert.Equal(AppSettings.LegacyPlinkPath, new AppSettings().PlinkPath);
    }
}
