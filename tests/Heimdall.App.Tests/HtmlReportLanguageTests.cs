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
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The SecNumCloud HTML report names its own language in the lang attribute, so a screen reader and
/// a browser's hyphenation follow the text instead of treating a French report as English.
/// </summary>
public sealed class HtmlReportLanguageTests
{
    private static readonly DateTime ReportStart = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("fr", "<html lang=\"fr\">")]
    [InlineData("es", "<html lang=\"es\">")]
    [InlineData("en", "<html lang=\"en\">")]
    public void Generate_WritesTheLanguageItWasGiven(string languageCode, string expectedTag)
    {
        string html = HtmlReportGenerator.Generate(NewReport(), languageCode: languageCode);

        Assert.Contains(expectedTag, html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Generate_FallsBackToEnglishWhenNoLanguageIsGiven(string? languageCode)
    {
        string html = HtmlReportGenerator.Generate(NewReport(), languageCode: languageCode);

        Assert.Contains("<html lang=\"" + HtmlReportGenerator.DefaultLanguageCode + "\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_EncodesTheLanguageItWasGiven()
    {
        string html = HtmlReportGenerator.Generate(NewReport(), languageCode: "fr\"><script>");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    // The export handler is the one place that knows the interface language; it cannot be driven
    // without a window, so this pins that it hands the current locale to the generator.
    [Fact]
    public void TheExportHandler_PassesTheInterfaceLanguageToTheGenerator()
    {
        string source = File.ReadAllText(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Tools", "SecNumCloudAuditView.xaml.cs"));

        Assert.Contains("HtmlReportGenerator.Generate(_lastReport, key => L(key), _localizer?.CurrentLocale)", source, StringComparison.Ordinal);
    }

    private static AuditReport NewReport() => new()
    {
        StartTime = ReportStart,
        EndTime = ReportStart.AddMinutes(1),
        Scope = new AuditScope([], Subnet: null, GatewayId: null),
    };
}
