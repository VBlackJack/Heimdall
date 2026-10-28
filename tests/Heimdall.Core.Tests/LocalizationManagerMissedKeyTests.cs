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
using Heimdall.Core.Logging;

namespace Heimdall.Core.Tests;

/// <summary>
/// A lookup that misses puts the raw key on the screen and nothing else says so; the manager
/// remembers each missed key and logs its first miss, which is what a hand pass reads.
/// </summary>
[Collection("FileLogger")]
public sealed class LocalizationManagerMissedKeyTests : IDisposable
{
    private const string UnknownKey = "KeyNoLocaleDefines";

    private readonly string _tempDir;
    private readonly string _localesPath;
    private readonly string _logDirectory;
    private readonly LocalizationManager _manager = new();

    public LocalizationManagerMissedKeyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.Tests." + Guid.NewGuid().ToString("N"));
        _localesPath = Path.Combine(_tempDir, "locales");
        _logDirectory = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(_localesPath);

        File.WriteAllText(
            Path.Combine(_localesPath, "en.json"),
            """{ "StatusReady": "Ready", "OnlyInEnglish": "English only" }""",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(_localesPath, "fr.json"),
            """{ "StatusReady": "Pret" }""",
            new UTF8Encoding(false));

        FileLogger.SetEnabled(true);
        FileLogger.Initialize(_logDirectory);
    }

    public void Dispose()
    {
        FileLogger.Flush();
        FileLogger.SetEnabled(true);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task AKeyTheLocaleDefinesIsNotRecordedAsMissed()
    {
        await _manager.LoadAsync(_localesPath, "en");

        Assert.Equal("Ready", _manager.GetString("StatusReady"));

        Assert.Equal(0, _manager.MissedKeyCount);
        Assert.Empty(_manager.MissedKeys);
    }

    [Fact]
    public async Task AMissedKeyIsRecordedOnceHoweverOftenItIsLookedUp()
    {
        await _manager.LoadAsync(_localesPath, "en");

        Assert.Equal(UnknownKey, _manager.GetString(UnknownKey));
        Assert.Equal(UnknownKey, _manager[UnknownKey]);
        Assert.Equal(UnknownKey, _manager.Format(UnknownKey, "argument"));

        Assert.Equal(1, _manager.MissedKeyCount);
        Assert.Equal([UnknownKey], _manager.MissedKeys);
    }

    [Fact]
    public async Task TheFirstMissOfAKeyIsLoggedAtDebugLevelWithTheLocale()
    {
        await _manager.LoadAsync(_localesPath, "fr");

        _manager.GetString("OnlyInEnglish");
        _manager.GetString("OnlyInEnglish");
        FileLogger.Flush();

        string[] lines = ReadLog().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] misses = lines.Where(line => line.Contains("'OnlyInEnglish'", StringComparison.Ordinal)).ToArray();
        string miss = Assert.Single(misses);
        Assert.Contains("[DEBUG]", miss, StringComparison.Ordinal);
        Assert.Contains("'fr'", miss, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadingAnotherLocaleStartsTheListOver()
    {
        await _manager.LoadAsync(_localesPath, "fr");
        _manager.GetString("OnlyInEnglish");
        Assert.Equal(1, _manager.MissedKeyCount);

        await _manager.SwitchLocaleAsync("en");

        Assert.Equal(0, _manager.MissedKeyCount);
        Assert.Equal("English only", _manager.GetString("OnlyInEnglish"));
        Assert.Empty(_manager.MissedKeys);
    }

    private string ReadLog()
    {
        string? logFile = Directory.GetFiles(_logDirectory, "heimdall_*.log").FirstOrDefault();
        return logFile is null ? string.Empty : File.ReadAllText(logFile);
    }
}
