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
using Heimdall.App.ViewModels.Onboarding;
using Heimdall.App.ViewModels.Shell;

namespace Heimdall.App.Tests;

/// <summary>
/// Every step of the tour must point at a control that still exists.
/// </summary>
/// <remarks>
/// The tour spotlights real controls by <c>x:Name</c>, which buys a dependency: rename or delete
/// a named element and the step silently stops highlighting anything. The overlay degrades to a
/// centred card rather than drawing a ring around empty space, so nothing throws, nothing fails,
/// and the only symptom is a step that quietly stops teaching - visible to a pair of eyes and to
/// nothing else.
///
/// That is the same shape as three defects found by hand this week: text clipped instead of
/// wrapped, translated keys referenced nowhere, and a style key that resolves in one window and
/// not another. All three compiled, all three passed every test. This is the cheap guard that
/// class deserves, and it is the reason the step table names its targets as data rather than
/// burying them in a switch.
/// </remarks>
public sealed class OnboardingStepTargetsTests
{
    [Fact]
    public void EveryTargetedStep_NamesAnElementThatExists()
    {
        string xaml = ReadMainWindowXaml();

        foreach (OnboardingFlowViewModel.Step step in OnboardingFlowViewModel.Steps)
        {
            if (step.TargetElementName is null)
            {
                continue;
            }

            Assert.True(
                Regex.IsMatch(xaml, $@"x:Name\s*=\s*""{Regex.Escape(step.TargetElementName)}"""),
                $"Onboarding step targets '{step.TargetElementName}', which no longer exists in "
                + "MainWindow.xaml. The step would show a centred card and teach nothing.");
        }
    }

    [Fact]
    public void EveryStep_HasBothItsStringsInBothLocales()
    {
        foreach (string locale in new[] { "en", "fr" })
        {
            using JsonDocument document = ReadLocale(locale);

            foreach (OnboardingFlowViewModel.Step step in OnboardingFlowViewModel.Steps)
            {
                foreach (string key in new[] { step.TitleKey, step.BodyKey })
                {
                    Assert.True(
                        document.RootElement.TryGetProperty(key, out JsonElement value),
                        $"Onboarding step key '{key}' is missing from {locale}.json");
                    Assert.False(
                        string.IsNullOrWhiteSpace(value.GetString()),
                        $"Onboarding step key '{key}' is empty in {locale}.json");
                }
            }
        }
    }

    // A tab name the shell does not handle would leave the step pointing into a tab that was
    // never opened, which is the failure the navigate-first ordering exists to prevent.
    [Fact]
    public void EveryDeclaredShellTab_IsOneTheShellCanOpen()
    {
        string[] known = [ShellTab.Sessions, ShellTab.Tools];

        foreach (OnboardingFlowViewModel.Step step in OnboardingFlowViewModel.Steps)
        {
            if (step.ShellTab is null)
            {
                continue;
            }

            Assert.Contains(step.ShellTab, known);
        }
    }

    [Fact]
    public void TheTour_HasMoreThanTheThreeStepsItStartedWith()
    {
        // Guards the theories above against becoming vacuous, and pins the decision that the
        // tour goes further into the product than the original three generic cards.
        Assert.True(
            OnboardingFlowViewModel.StepCount > 3,
            $"The tour has {OnboardingFlowViewModel.StepCount} steps.");
        Assert.Contains(OnboardingFlowViewModel.Steps, s => s.TargetElementName is not null);
    }

    private static string ReadMainWindowXaml() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));

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
