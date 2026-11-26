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

using System.Text.Json.Nodes;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// A settings file is written by someone else: a value in the confirmation must not be able to
/// start a line of its own or reorder what is read, so control characters are shown as escapes.
/// </summary>
public sealed class SettingsImportPreviewUntrustedTextTests
{
    private const string LineFeed = "\n";
    private const string BidiOverride = "\u202e";
    private const string LineFeedEscape = "\\u{000a}";
    private const string BidiOverrideEscape = "\\u{202e}";
    private const string EllipsisText = "...";

    [Fact]
    public async Task AValueWithALineBreak_ShowsTheEscapeAndStartsNoLine()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        SettingsTransferChange change = new(
            nameof(AppSettings.TerminalFontFamily),
            JsonValue.Create("Consolas"),
            JsonValue.Create("Fake" + LineFeed + "Setting: On"));

        string line = SettingsImportPreview.DescribeChange(localizer, change);

        Assert.Contains("Fake" + LineFeedEscape + "Setting", line, StringComparison.Ordinal);
        Assert.DoesNotContain(LineFeed, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASensitiveValueWithALineBreak_ShowsTheEscapeInTheComposedText()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        SettingsTransferChange change = new(
            nameof(AppSettings.CredentialProviderCommand),
            JsonValue.Create(string.Empty),
            JsonValue.Create("run" + LineFeed + "hidden"));

        string text = SettingsImportPreview.Compose(localizer, [change]);

        Assert.Contains("run" + LineFeedEscape + "hidden", text, StringComparison.Ordinal);
        Assert.DoesNotContain(LineFeed + "hidden", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABidiOverride_IsEscaped()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        JsonValue value = JsonValue.Create("exe" + BidiOverride + "txt")!;

        Assert.Equal("exe" + BidiOverrideEscape + "txt", SettingsImportPreview.DescribeValue(localizer, value));
        Assert.Equal("exe" + BidiOverrideEscape + "txt", SettingsImportPreview.DescribeFullValue(localizer, value));
    }

    [Fact]
    public async Task AListItemWithALineBreak_IsEscaped()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        JsonArray names = new("a" + LineFeed + "b", "c");

        Assert.Equal("a" + LineFeedEscape + "b, c", SettingsImportPreview.DescribeValue(localizer, names));
        Assert.Equal("a" + LineFeedEscape + "b, c", SettingsImportPreview.DescribeFullValue(localizer, names));
    }

    [Fact]
    public async Task AToolNameAndExecutableWithALineBreak_AreEscaped()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        JsonArray tools = new(new JsonObject
        {
            ["Name"] = "Ping" + LineFeed + "Fake",
            ["ExecutablePath"] = @"C:\x" + LineFeed + "y.exe",
        });

        string text = SettingsImportPreview.DescribeFullValue(localizer, tools, nameof(AppSettings.ExternalTools));

        Assert.Equal("Ping" + LineFeedEscape + "Fake (C:\\x" + LineFeedEscape + "y.exe)", text);
    }

    [Fact]
    public async Task ASharedSourceNameAndPathWithALineBreak_AreEscaped()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        JsonArray sources = new(new JsonObject
        {
            ["Name"] = "Team" + LineFeed + "Fake",
            ["Path"] = @"\\share" + LineFeed + "team.mxtsessions",
            ["Enabled"] = true,
        });

        string text = SettingsImportPreview.DescribeFullValue(localizer, sources, nameof(AppSettings.SharedSessionSources));

        Assert.Equal("Team" + LineFeedEscape + "Fake (\\\\share" + LineFeedEscape + "team.mxtsessions)", text);
    }

    [Fact]
    public async Task AnAccentedPlainValue_IsUnchanged()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        const string accented = "Caf\u00e9 r\u00e9seau";

        Assert.Equal(accented, SettingsImportPreview.DescribeValue(localizer, JsonValue.Create(accented)));
        Assert.Equal(accented, SettingsImportPreview.DescribeFullValue(localizer, JsonValue.Create(accented)));
    }

    [Fact]
    public async Task ALongValueOfEscapes_StaysWithinTheLimitAndNeverCutsAnEscape()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        string longValue = string.Concat(Enumerable.Repeat("a" + LineFeed, SettingsImportPreview.MaxValueLength));

        string shown = SettingsImportPreview.DescribeValue(localizer, JsonValue.Create(longValue));

        Assert.True(shown.Length <= SettingsImportPreview.MaxValueLength, shown);
        Assert.EndsWith(EllipsisText, shown, StringComparison.Ordinal);
        Assert.DoesNotContain(LineFeed, shown, StringComparison.Ordinal);
        string withoutMarker = shown[..^EllipsisText.Length];
        Assert.True(withoutMarker.EndsWith('}') || withoutMarker.EndsWith('a'), shown);
    }

    [Fact]
    public async Task ALongPlainListAndValue_AreStillCutToTheLimit()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        string longValue = new('x', SettingsImportPreview.MaxValueLength + 10);
        JsonArray list = new(longValue, longValue);

        Assert.Equal(SettingsImportPreview.MaxValueLength, SettingsImportPreview.DescribeValue(localizer, JsonValue.Create(longValue)).Length);
        Assert.Equal(SettingsImportPreview.MaxValueLength, SettingsImportPreview.DescribeValue(localizer, list).Length);
    }

    private static async Task<LocalizationManager> LoadLocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }
}
