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

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Heimdall.App.Views;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Covers the notice shown when a cancelled reconnection succeeds anyway.
/// </summary>
/// <remarks>
/// <para>Cancelling stops the retries that have not started yet; an attempt already inside MsTscAx
/// keeps going and can still succeed. The session is kept, and this notice is what tells the user
/// why the screen went from closing to connected.</para>
/// <para>Two ways it can silently stop working, and one test each. The message can lose its
/// translation, in which case the toast appears empty and the user is told nothing at all. Or the
/// flag it depends on can be read after being cleared, in which case the toast never appears and
/// nothing fails.</para>
/// </remarks>
public sealed class RdpCancelledReconnectNoticeTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void TheNoticeHasAMessageInBothLanguages(string language)
    {
        IReadOnlyDictionary<string, JsonElement> locale = ReadLocale(language);
        string key = EmbeddedRdpView.LocaleKeys.ReconnectSucceededAfterCancel;

        Assert.True(
            locale.TryGetValue(key, out JsonElement value),
            $"'{key}' is missing from {language}.json, so the toast would appear empty.");

        string? text = value.GetString();
        Assert.False(
            string.IsNullOrWhiteSpace(text),
            $"'{key}' is present but blank in {language}.json.");
    }

    /// <summary>
    /// The flag must be read before the same handler clears it.
    /// </summary>
    /// <remarks>
    /// The clearing is not optional: leaving it raised makes the next genuine drop of a live
    /// session read as a user disconnect. So the notice has to take its copy first, and the order
    /// of two statements in one method is the whole of the contract. Nothing about the toast fails
    /// if they are swapped; it simply never appears.
    /// </remarks>
    [Fact]
    public void TheFlagIsReadBeforeTheHandlerClearsIt()
    {
        string handler = ReadAutoReconnectedHandler();

        int read = handler.IndexOf(
            "bool cancelLostTheRace = _userInitiatedDisconnect;",
            System.StringComparison.Ordinal);
        int cleared = handler.IndexOf(
            "_userInitiatedDisconnect = false;",
            System.StringComparison.Ordinal);
        int announced = handler.IndexOf(
            "ShowTransientToast(",
            System.StringComparison.Ordinal);

        Assert.True(read >= 0, "the handler no longer takes a copy of the flag");
        Assert.True(cleared >= 0, "the handler no longer clears the flag");
        Assert.True(announced >= 0, "the handler no longer announces anything");

        Assert.True(
            read < cleared,
            "The flag is cleared before it is read, so the notice can never appear.");
        Assert.True(
            cleared < announced,
            "The announcement runs before the flag is cleared, which reverses the order the "
                + "surrounding code depends on.");
    }

    /// <summary>
    /// The notice must stay behind the condition that gives it its meaning.
    /// </summary>
    /// <remarks>
    /// The order above holds just as well with the <c>if (cancelLostTheRace)</c> wrapper removed:
    /// the three statements are still in sequence, the slice is still long, the locale key is still
    /// there. What changes is that a server which drops the link and reconnects on its own - the
    /// user never touched Cancel - now tells the user the reconnection they cancelled succeeded
    /// anyway. That is the mutant this file could not see.
    /// </remarks>
    [Fact]
    public void TheNoticeIsOnlyShownWhenTheCancelLostTheRace()
    {
        string handler = ReadAutoReconnectedHandler();

        int guarded = handler.IndexOf(
            "if (cancelLostTheRace)",
            System.StringComparison.Ordinal);
        int announced = handler.IndexOf(
            "ShowTransientToast(",
            System.StringComparison.Ordinal);

        Assert.True(
            guarded >= 0,
            "The notice is no longer conditional. Every auto-reconnect now tells the user the "
                + "reconnection they cancelled succeeded anyway, including the ones nobody "
                + "cancelled.");
        Assert.True(
            guarded < announced,
            "The announcement is outside the condition that gives it its meaning.");
    }

    // Guarding the guard: a slice that failed to find the handler would be empty, and every
    // IndexOf above would return -1 and be reported as a missing statement rather than as a
    // broken slice.
    [Fact]
    public void TheHandlerSliceIsRealAndBounded()
    {
        string handler = ReadAutoReconnectedHandler();

        Assert.Contains("private void OnRdpAutoReconnected()", handler, System.StringComparison.Ordinal);
        Assert.DoesNotContain("private void OnRdpDisconnected", handler, System.StringComparison.Ordinal);
        Assert.True(handler.Length > 400, $"the slice is only {handler.Length} characters long");
    }

    private static string ReadAutoReconnectedHandler()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedRdpView.xaml.cs"));

        const string signature = "private void OnRdpAutoReconnected()";
        int start = source.IndexOf(signature, System.StringComparison.Ordinal);
        Assert.True(start >= 0, "OnRdpAutoReconnected not found in the view");

        int next = source.IndexOf(
            "\n    private ",
            start + signature.Length,
            System.StringComparison.Ordinal);

        return next < 0 ? source[start..] : source[start..next];
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadLocale(string language)
    {
        string path = Path.Combine(FindRepoRoot(), "locales", $"{language}.json");
        Assert.True(File.Exists(path), $"Locale file not found: {path}");

        Dictionary<string, JsonElement>? parsed =
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));

        Assert.NotNull(parsed);
        Assert.True(parsed.Count > 1000, "the locale file did not load");
        return parsed;
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
            $"Cannot find repository root from test binary directory: {AppContext.BaseDirectory}");
    }
}
