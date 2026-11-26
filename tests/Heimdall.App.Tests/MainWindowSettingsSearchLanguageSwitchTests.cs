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
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The settings search follows an interface language switch: the same query is filtered again over
/// the texts as they read in the new language, and its hint is worded in that language.
/// </summary>
/// <remarks>
/// The window itself cannot be driven here (it needs the whole shell), so the extracted pieces the
/// window runs on typing and on a switch are tested directly: the filter over live element texts,
/// and the two hint wordings. The window's own wiring (a ContextIdle re-run on the locale event, a
/// no-op on an empty box) is not covered by a test.
/// </remarks>
public sealed class MainWindowSettingsSearchLanguageSwitchTests
{
    private const string FrenchLocale = "fr";
    private const string EnglishQuery = "theme";
    private const string EnglishText = "Theme";
    private const string FrenchText = "Apparence";
    private const string OtherText = "Start minimized";
    private const int PositionHintFirst = 1;
    private const int HintMatchCount = 3;

    private static async Task<LocalizationManager> LoadAsync(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return localizer;
    }

    [Fact]
    public void Filter_SameQuery_FollowsTheTextsAsTheyReadNow()
    {
        RunOnStaThread(() =>
        {
            TextBlock label = new() { Text = EnglishText };
            TextBlock other = new() { Text = OtherText };
            TabItem section = new();
            List<MainWindow.SettingsSearchEntry> index =
            [
                new(label, section),
                new(other, section)
            ];

            List<MainWindow.SettingsSearchEntry> before = MainWindow.FilterSettingsSearchEntries(index, EnglishQuery, _ => true);
            Assert.Single(before);

            // A language switch rewords the element; the index keeps the element, not its text.
            label.Text = FrenchText;
            List<MainWindow.SettingsSearchEntry> after = MainWindow.FilterSettingsSearchEntries(index, EnglishQuery, _ => true);
            Assert.Empty(after);

            List<MainWindow.SettingsSearchEntry> inNewLanguage = MainWindow.FilterSettingsSearchEntries(index, FrenchText, _ => true);
            Assert.Single(inNewLanguage);
        });
    }

    [Fact]
    public void Filter_SkipsEntriesThatCannotBeReached()
    {
        RunOnStaThread(() =>
        {
            TextBlock label = new() { Text = EnglishText };
            List<MainWindow.SettingsSearchEntry> index = [new(label, new TabItem())];

            Assert.Empty(MainWindow.FilterSettingsSearchEntries(index, EnglishQuery, _ => false));
        });
    }

    [Fact]
    public async Task CountHint_IsWordedInTheCurrentLanguage()
    {
        LocalizationManager localizer = await LoadAsync("en");
        string english = MainWindow.BuildSettingsSearchCountHint(localizer.GetString, HintMatchCount);
        string englishNone = MainWindow.BuildSettingsSearchCountHint(localizer.GetString, 0);
        Assert.Equal(string.Format(localizer["SettingsSearchResultCount"], HintMatchCount), english);
        Assert.Equal(localizer["SettingsSearchNoResults"], englishNone);

        await localizer.SwitchLocaleAsync(FrenchLocale);

        string french = MainWindow.BuildSettingsSearchCountHint(localizer.GetString, HintMatchCount);
        Assert.NotEqual(english, french);
        Assert.Equal(string.Format(localizer["SettingsSearchResultCount"], HintMatchCount), french);
        Assert.Equal(localizer["SettingsSearchNoResults"], MainWindow.BuildSettingsSearchCountHint(localizer.GetString, 0));
        Assert.NotEqual(englishNone, localizer["SettingsSearchNoResults"]);
    }

    [Fact]
    public async Task PositionHint_IsWordedInTheCurrentLanguage()
    {
        LocalizationManager localizer = await LoadAsync("en");
        string english = MainWindow.BuildSettingsSearchPositionHint(localizer.GetString, PositionHintFirst, HintMatchCount);

        await localizer.SwitchLocaleAsync(FrenchLocale);

        string french = MainWindow.BuildSettingsSearchPositionHint(localizer.GetString, PositionHintFirst, HintMatchCount);
        Assert.NotEqual(english, french);
        Assert.Equal(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                localizer["SettingsSearchResultPosition"],
                PositionHintFirst,
                HintMatchCount),
            french);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
