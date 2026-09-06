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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// French wording rules that a key-parity guard cannot see.
/// </summary>
/// <remarks>
/// Two update messages pointed the user at "le panneau A propos" with a bare capital A,
/// while the tab they must find is labelled with the accented capital in the same file.
/// A message that names a panel by a spelling the interface does not use is a class of
/// defect, so the rule is over every value, with the About tab's own spelling as the
/// positive control.
/// </remarks>
public sealed class FrenchLocaleWordingGuardTests
{
    private static readonly Regex UnaccentedAbout = new(@"\bA propos\b", RegexOptions.Compiled);

    [Fact]
    public void NoFrenchValueNamesTheAboutPanelWithoutItsAccent()
    {
        Dictionary<string, string> values = FrenchValues();

        List<string> offenders = values
            .Where(pair => UnaccentedAbout.IsMatch(pair.Value))
            .Select(pair => pair.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0, "French values naming the About panel without its accent: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheAboutTabItselfIsAccented_AndTheUpdateMessagesFollowIt()
    {
        Dictionary<string, string> values = FrenchValues();

        Assert.Equal("\u00c0 propos", values["TabAbout"]);
        Assert.Contains("panneau \u00c0 propos", values["UpdateBannerOutcomeInstallerFailed"], StringComparison.Ordinal);
        Assert.Contains("panneau \u00c0 propos", values["UpdateBannerOutcomeNotApplied"], StringComparison.Ordinal);
        Assert.Contains("men\u00e9e \u00e0 son terme", values["UpdateBannerOutcomeCancelled"], StringComparison.Ordinal);
    }

    private static Dictionary<string, string> FrenchValues()
    {
        string path = Path.Combine(ViewSource.RepoRoot(), "locales", "fr.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                values[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        Assert.True(values.Count > 1000, "the French catalogue was not read");
        return values;
    }
}
