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

using System.Text;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

/// <summary>
/// A tool detected on the machine carries the product's own name, not a locale key, and
/// showing that name must not count as a missing translation.
/// </summary>
/// <remarks>
/// At startup the tool lists looked up "Autorunsc" and "Event Log View" in the locale: each
/// miss was logged as a missing translation, and the name came back unchanged, so nothing
/// on screen was wrong and only the log said so.
/// </remarks>
public sealed class ToolDescriptorLabelTests : IDisposable
{
    private const string BuiltInKey = "PaletteToolPing";
    private const string BuiltInLabel = "Ping";
    private const string DetectedName = "Autorunsc";
    private const string DetectedNameWithSpaces = "Event Log View";

    private readonly string _tempDir;
    private readonly LocalizationManager _manager = new();

    public ToolDescriptorLabelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(
            Path.Combine(_tempDir, "en.json"),
            "{ \"" + BuiltInKey + "\": \"" + BuiltInLabel + "\" }",
            new UTF8Encoding(false));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Theory]
    [InlineData(DetectedName)]
    [InlineData(DetectedNameWithSpaces)]
    public async Task ADetectedToolNameIsShownAsIsAndNeverLookedUpAsAKey(string name)
    {
        await _manager.LoadAsync(_tempDir, "en");
        var descriptor = Detected(name);

        var label = descriptor.ResolveLabel(_manager.GetString);

        Assert.Equal(name, label);
        Assert.Equal(0, _manager.MissedKeyCount);
    }

    [Fact]
    public async Task ABuiltInToolNameIsStillResolvedThroughTheLocale()
    {
        await _manager.LoadAsync(_tempDir, "en");
        var descriptor = new ToolDescriptor(
            "PING", ToolCategory.Network, "ToolCategoryNetwork",
            BuiltInKey, null, ["ping"], true);

        var label = descriptor.ResolveLabel(_manager.GetString);

        Assert.Equal(BuiltInLabel, label);
        Assert.Equal(0, _manager.MissedKeyCount);
    }

    private static ToolDescriptor Detected(string name) => new(
        "EXT:SYSINTERNALS:" + name.ToUpperInvariant(), ToolCategory.External, "ToolCategoryExternal",
        name, null, [name.ToLowerInvariant()], false, ExternalProviderName: "Sysinternals");
}
