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

using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.App.Converters;
using Heimdall.App.ViewModels;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Tests;

/// <summary>
/// Keeps a session status a token rather than a sentence.
/// </summary>
/// <remarks>
/// <para>A pane's status is parsed back into a connection state to decide whether a session counts
/// as live, and turned into display text by a converter. Writing localized text into it breaks
/// both, silently: the parse fails, so a live session stops counting as connected, and the closing
/// confirmation that depends on that count stops appearing.</para>
/// <para>That is not hypothetical. The value written for a connected session was
/// <c>StatusConnected</c>, whose English text is a format string, so the field held the literal
/// <c>"Connected to: {0}"</c> - unparseable in every language, and displayed with its placeholder
/// unfilled on the two surfaces that showed it raw.</para>
/// </remarks>
public sealed class SessionStatusTokenGuardTests
{
    /// <summary>
    /// Writes that are deliberately free text, with the reason.
    /// </summary>
    /// <remarks>
    /// A tool pane is not a session. Its status must not parse as a connection state, or the
    /// connected-session census would count an open tool as a live connection.
    /// </remarks>
    private static readonly string[] DeliberateFreeTextWrites =
    [
        // A tool pane is not a session.
        "tab.Status = _localizer[\"StatusReady\"];",

        // A failed pane shows why it failed. The converter passes an unrecognised value through
        // unchanged for exactly this, and a failed session is not counted as live either way.
        "tab.Status = statusText;",
        "sessionTab.Status = localizedMsg;",
    ];

    private static readonly Regex StatusWrite = new(
        @"\.Status\s*=\s*(?<value>[^;]+);",
        RegexOptions.Compiled);

    [Fact]
    public void NoSessionStatusIsWrittenAsLocalizedText()
    {
        List<string> violations = [];

        foreach (string file in EnumerateAppSources())
        {
            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
            {
                Match match = StatusWrite.Match(lines[index]);
                if (!match.Success)
                {
                    continue;
                }

                string value = match.Groups["value"].Value;
                bool localized = value.Contains("_localizer", StringComparison.Ordinal)
                    || value.Contains("localizedMsg", StringComparison.Ordinal)
                    || value.Contains("L(", StringComparison.Ordinal);

                if (!localized || DeliberateFreeTextWrites.Any(allowed => lines[index].Contains(allowed, StringComparison.Ordinal)))
                {
                    continue;
                }

                violations.Add($"{file}:{index + 1}: {lines[index].Trim()}");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Guards the guard: the sweep has to reach the files that carry these writes.
    /// </summary>
    [Fact]
    public void TheSweepReachesTheFilesThatWriteAStatus()
    {
        List<string> files = EnumerateAppSources().ToList();

        Assert.NotEmpty(files);
        Assert.Contains(files, file => file.EndsWith("SessionCoordinator.cs", StringComparison.Ordinal));
        Assert.Contains(files, file => file.EndsWith("SplitService.cs", StringComparison.Ordinal));
        Assert.Contains(files, file => file.EndsWith("MainViewModel.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// The tokens that name a connection state, and what the census makes of each.
    /// </summary>
    [Theory]
    [InlineData(SessionStatusTokens.Connected, true)]
    [InlineData(SessionStatusTokens.LaunchedExternalClient, true)]
    [InlineData(SessionStatusTokens.RemoteSessionHandedOff, true)]
    [InlineData(SessionStatusTokens.Disconnected, false)]
    [InlineData(SessionStatusTokens.Disconnecting, false)]
    [InlineData(SessionStatusTokens.Error, false)]
    public void AStateTokenParsesAndCountsAsExpected(string token, bool countsAsConnected)
    {
        Assert.True(
            Enum.TryParse(token, ignoreCase: true, out ConnectionState _),
            $"'{token}' does not name a connection state, so a session carrying it stops counting.");
        Assert.Equal(countsAsConnected, ConnectionStateSets.IsConnected(token));
    }

    /// <summary>
    /// The two display-only tokens, asserted as such rather than left to look like an oversight.
    /// </summary>
    /// <remarks>
    /// No connection state is named "Connecting" or "Reconnecting". A pane carrying one is
    /// therefore not counted among the live sessions, which is the answer a session still being
    /// established should give. The converter still turns both into text, so nothing is lost on
    /// screen.
    /// </remarks>
    [Theory]
    [InlineData(SessionStatusTokens.Connecting)]
    [InlineData(SessionStatusTokens.Reconnecting)]
    public void ADisplayOnlyTokenDoesNotCountAsALiveSession(string token)
    {
        Assert.False(Enum.TryParse(token, ignoreCase: true, out ConnectionState _));
        Assert.False(ConnectionStateSets.IsConnected(token));
    }

    /// <summary>
    /// The value the product used to write, kept as a test so the reason this guard exists cannot
    /// be mistaken for style.
    /// </summary>
    [Fact]
    public void TheValueThatUsedToBeWrittenWouldNotCountAsConnected()
    {
        Assert.False(ConnectionStateSets.IsConnected("Connected to: {0}"));
        Assert.False(ConnectionStateSets.IsConnected("Connecte a : {0}"));
    }

    /// <summary>
    /// Every token has to have something to say in both languages.
    /// </summary>
    /// <remarks>
    /// The converter passes an unrecognised value through unchanged, so a token whose key is
    /// missing renders as the key name itself. Driven from the token class so a token added later
    /// without a message fails here rather than on screen.
    /// </remarks>
    [Fact]
    public void EveryTokenHasAMessageInBothLanguages()
    {
        SessionStatusToDisplayConverter converter = new(key => key);
        List<string> missing = [];
        int checkedTokens = 0;

        foreach (FieldInfo field in typeof(SessionStatusTokens)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)))
        {
            string token = (string)field.GetRawConstantValue()!;
            checkedTokens++;

            object? resolved = converter.Convert(token, typeof(string), null, CultureInfo.InvariantCulture);
            string key = Assert.IsType<string>(resolved);
            if (string.Equals(key, token, StringComparison.Ordinal))
            {
                missing.Add($"{field.Name}: the converter does not recognise '{token}'");
                continue;
            }

            foreach (string locale in new[] { "en", "fr" })
            {
                string path = Path.Combine(FindRepoRoot(), "locales", $"{locale}.json");
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty(key, out _))
                {
                    missing.Add($"{field.Name}: '{key}' missing from {locale}.json");
                }
            }
        }

        Assert.Empty(missing);
        Assert.True(checkedTokens >= 8, $"Only {checkedTokens} tokens were checked.");
    }

    private static IEnumerable<string> EnumerateAppSources()
    {
        string root = Path.Combine(FindRepoRoot(), "src", "Heimdall.App");

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }
}
