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

using Heimdall.Core.Discovery;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

/// <summary>
/// The report builders of the tools take a count localizer beside their key delegate, so the
/// language that words the keys also decides which wording a count takes.
/// </summary>
public sealed class ToolReportCountWordingTests
{
    private static string French(string key) => key switch
    {
        "ToolDefCredSummary" => "{0} sur {1}",
        "ToolDefCredSummaryCredentials" => "{0} identifiants par défaut trouvés",
        "ToolDefCredSummaryCredentialsOne" => "{0} identifiant par défaut trouvé",
        "ToolDefCredSummaryServices" => "{0} services",
        "ToolDefCredSummaryServicesOne" => "{0} service",
        "ToolCveSummary" => "{0} CVE trouvées pour {1}",
        "ToolCveSummaryOne" => "{0} CVE trouvée pour {1}",
        _ => key,
    };

    [Theory]
    [InlineData(1, 1, "1 identifiant par défaut trouvé sur 1 service")]
    [InlineData(2, 1, "2 identifiants par défaut trouvés sur 1 service")]
    [InlineData(2, 2, "2 identifiants par défaut trouvés sur 2 services")]
    public void DefaultCredentialSummary_WordsEachCountByItsNumber(int credentials, int services, string expected)
    {
        List<CredTestResultDto> results = [];
        for (int i = 0; i < credentials; i++)
        {
            results.Add(new CredTestResultDto
            {
                Service = "ssh",
                Port = 22 + (i % services),
                Username = $"user{i}",
                Password = "secret",
                Status = CredTestStatus.Default,
            });
        }

        string text = DefaultCredentialEngine.BuildSummaryText(
            results,
            French,
            new DelegateCountLocalizer(French, () => "fr"));

        Assert.Equal(expected, text);
    }

    /// <summary>The French singular also covers 0, so the language has to reach the engine.</summary>
    [Fact]
    public void CveCopyText_WordsZeroInTheFrenchSingular()
    {
        var result = new CveSearchResult("OpenSSH 8.9", []);

        string text = CveLookupEngine.BuildCopyText(result, French, new DelegateCountLocalizer(French, () => "fr"));

        Assert.StartsWith("0 CVE trouvée pour OpenSSH 8.9", text, StringComparison.Ordinal);
    }
}
