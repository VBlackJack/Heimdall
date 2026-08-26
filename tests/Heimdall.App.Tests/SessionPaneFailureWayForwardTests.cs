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
using System.Text.RegularExpressions;

namespace Heimdall.App.Tests;

/// <summary>
/// The failure pane every protocol shows must offer a way forward, not only a way round.
/// </summary>
/// <remarks>
/// It had exactly two buttons: Reconnect and Close. Reconnect repeats whatever just failed, so on
/// a wrong username, a wrong port or a missing key the pane said "do it again" and "give up" - and
/// a first-time user presses Reconnect two or three times before concluding the app cannot
/// connect. The route to the field that is actually wrong existed already, wired to the RDP
/// overlay alone: the one failure surface a newcomer is least likely to meet first, since SSH and
/// SFTP are where they start.
///
/// These assert against the XAML rather than a running window because instantiating a WPF Window
/// in this suite seals app-level styles onto the shared dispatcher and fails unrelated tests on
/// thread affinity. A source assertion is weaker than a live one and worth saying so: it proves
/// the buttons are declared and wired, not that they are reachable on screen. The reachability
/// half is a visual pass, and was done.
/// </remarks>
public sealed class SessionPaneFailureWayForwardTests
{
    [Theory]
    [InlineData("EditProfileButton")]
    [InlineData("CopyErrorButton")]
    public void TheGenericFailurePane_DeclaresTheButton(string name)
    {
        string xaml = ReadPaneXaml();

        Assert.True(
            Regex.IsMatch(xaml, $@"x:Name\s*=\s*""{Regex.Escape(name)}"""),
            $"'{name}' is gone from SessionPaneControl.xaml. The generic failure pane is back to "
            + "offering only Reconnect and Close, which is the dead end this removed.");
    }

    [Theory]
    [InlineData("EditProfileButton", "OnEditProfileClick")]
    [InlineData("CopyErrorButton", "OnCopyErrorClick")]
    public void EachButton_IsWiredToItsHandler(string button, string handler)
    {
        string code = ReadPaneCodeBehind();

        Assert.Contains($"{button}.Click += {handler};", code, StringComparison.Ordinal);
        Assert.Contains($"private void {handler}(", code, StringComparison.Ordinal);
    }

    // A button that exists but reaches nothing is the shape this repo has already been caught by:
    // a close guard attached to no host, and an empty-state button that changed nothing where the
    // user was looking. Declaring it is not enough; it has to go somewhere.
    [Fact]
    public void EditProfile_RoutesToTheProfileEditor()
    {
        string code = ReadPaneCodeBehind();

        Assert.Contains("EditServerByIdAsync", code, StringComparison.Ordinal);
        Assert.Contains("ProfileLookupServerId", code, StringComparison.Ordinal);
    }

    // An ad-hoc session has no saved profile to open, so offering the button there would recreate
    // the defect one button over: a control that does nothing when pressed.
    [Fact]
    public void EditProfile_IsHiddenWhenThereIsNoStoredProfile()
    {
        string code = ReadPaneCodeBehind();

        Assert.Contains("UpdateEditProfileAvailability", code, StringComparison.Ordinal);
        Assert.Contains("EditProfileButton.Visibility", code, StringComparison.Ordinal);
    }

    // The expander header borrowed a column label from the network map tool, so one string served
    // two unrelated surfaces and neither could be reworded without moving the other.
    [Fact]
    public void TheDetailsExpander_UsesItsOwnHeaderKey()
    {
        string xaml = ReadPaneXaml();

        Assert.DoesNotContain("ToolNetMapColDetails", xaml, StringComparison.Ordinal);
        Assert.Contains("SessionPaneFailureDetailsHeader", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void EveryStringTheButtonsUse_ExistsInBothLocales(string locale)
    {
        using JsonDocument document = ReadLocale(locale);

        foreach (string key in new[]
                 {
                     "BtnEditProfileOverlay",
                     "TooltipEditProfileOverlay",
                     "A11yEditProfileOverlay",
                     "BtnCopyErrorOverlay",
                     "TooltipCopyErrorOverlay",
                     "A11yCopyErrorOverlay",
                     "SessionPaneFailureDetailsHeader"
                 })
        {
            Assert.True(
                document.RootElement.TryGetProperty(key, out JsonElement value),
                $"'{key}' is missing from {locale}.json");
            Assert.False(
                string.IsNullOrWhiteSpace(value.GetString()),
                $"'{key}' is empty in {locale}.json");
        }
    }

    private static string ReadPaneXaml() =>
        File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Heimdall.App", "Views", "SessionPaneControl.xaml"));

    private static string ReadPaneCodeBehind() =>
        File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Heimdall.App", "Views", "SessionPaneControl.xaml.cs"));

    private static JsonDocument ReadLocale(string locale) =>
        JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "locales", $"{locale}.json")));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
