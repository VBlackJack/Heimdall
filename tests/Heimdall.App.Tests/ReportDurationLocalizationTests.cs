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
using System.Net;
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// Durations and the "not available" placeholder come out of the catalogue, so the SecNumCloud
/// report and the About page say them in the interface language instead of in English.
/// </summary>
public sealed class ReportDurationLocalizationTests
{
    private static readonly TimeSpan OneHourTwoMinutesThreeSeconds = new(1, 2, 3);
    private static readonly TimeSpan OneMinuteFiveSeconds = new(0, 1, 5);
    private static readonly TimeSpan FortySevenSeconds = TimeSpan.FromSeconds(47);

    [Theory]
    [InlineData("en", "1 h 02 min 03 s", "1 min 05 s", "47 s")]
    [InlineData("fr", "1 h 02 min 03 s", "1 min 05 s", "47 s")]
    [InlineData("es", "1 h 02 min 03 s", "1 min 05 s", "47 s")]
    public async Task Format_UsesTheCataloguePatternAtEachMagnitude(
        string language, string hours, string minutes, string seconds)
    {
        Func<string, string> localize = await LocalizerAsync(language);

        Assert.Equal(hours, DurationFormatter.Format(OneHourTwoMinutesThreeSeconds, localize));
        Assert.Equal(minutes, DurationFormatter.Format(OneMinuteFiveSeconds, localize));
        Assert.Equal(seconds, DurationFormatter.Format(FortySevenSeconds, localize));
    }

    [Fact]
    public void Format_TakesItsUnitsFromThePatternItIsGiven()
    {
        // The unit text must come from the delegate: a formatter that wrote its own "h" or "min"
        // would ignore this pattern and fail here.
        Dictionary<string, string> patterns = new()
        {
            [DurationFormatter.HoursMinutesSecondsKey] = "{0}H-{1}M-{2}S",
            [DurationFormatter.MinutesSecondsKey] = "{0}M-{1}S",
            [DurationFormatter.SecondsKey] = "{0}S",
        };

        Assert.Equal("1H-2M-3S", DurationFormatter.Format(OneHourTwoMinutesThreeSeconds, key => patterns[key]));
        Assert.Equal("1M-5S", DurationFormatter.Format(OneMinuteFiveSeconds, key => patterns[key]));
        Assert.Equal("47S", DurationFormatter.Format(FortySevenSeconds, key => patterns[key]));
    }

    [Fact]
    public async Task FormatWithTenths_KeepsATenthOfASecondUnderAMinuteInTheCallersCulture()
    {
        Func<string, string> localize = await LocalizerAsync("fr");
        TimeSpan quickRun = TimeSpan.FromMilliseconds(400);

        Assert.Equal("0,4 s", DurationFormatter.FormatWithTenths(quickRun, localize, CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal("0.4 s", DurationFormatter.FormatWithTenths(quickRun, localize, CultureInfo.InvariantCulture));
        Assert.Equal(
            DurationFormatter.Format(OneMinuteFiveSeconds, localize),
            DurationFormatter.FormatWithTenths(OneMinuteFiveSeconds, localize, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("en", "N/A", "1 h 02 min 03 s")]
    [InlineData("fr", "N/D", "1 h 02 min 03 s")]
    [InlineData("es", "N/D", "1 h 02 min 03 s")]
    public async Task Report_SaysDurationAndMissingScopeInTheInterfaceLanguage(
        string language, string notAvailable, string duration)
    {
        Func<string, string> localize = await LocalizerAsync(language);
        DateTime start = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
        AuditReport report = new()
        {
            StartTime = start,
            EndTime = start + OneHourTwoMinutesThreeSeconds,
            Scope = new AuditScope([], Subnet: null, GatewayId: null),
        };

        // The generator encodes accents as character references; compare what a browser shows.
        string html = WebUtility.HtmlDecode(HtmlReportGenerator.Generate(report, localize));

        Assert.Contains($"{localize("AuditReportScope")}: {notAvailable} |", html, StringComparison.Ordinal);
        Assert.Contains($"{localize("AuditReportDuration")}: {duration}", html, StringComparison.Ordinal);
        Assert.Contains($"{localize("AuditReportAuditDuration")}: {duration}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("1h 02m", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_WithAScope_DoesNotShowThePlaceholder()
    {
        Func<string, string> localize = await LocalizerAsync("fr");
        DateTime start = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
        AuditReport report = new()
        {
            StartTime = start,
            EndTime = start + FortySevenSeconds,
            Scope = new AuditScope(["10.0.0.5"], Subnet: null, GatewayId: null),
        };

        // The generator encodes accents as character references; compare what a browser shows.
        string html = WebUtility.HtmlDecode(HtmlReportGenerator.Generate(report, localize));

        Assert.Contains("10.0.0.5", html, StringComparison.Ordinal);
        Assert.DoesNotContain(localize("ValueNotAvailable"), html, StringComparison.Ordinal);
    }

    // An absence guard: the English units and the literal placeholder are gone from the code
    // that used to write them, and a copy that comes back is caught here. The behaviour behind
    // it is the three tests above; these two files hold UI text that no test can drive without a
    // window.
    [Theory]
    [InlineData("MainWindow.xaml.cs", "\"N/A\"")]
    [InlineData("Views/Tools/SecNumCloudAuditView.xaml.cs", "{ts.Minutes}m")]
    [InlineData("Services/HtmlReportGenerator.cs", "\"N/A\"")]
    public void TheCodeThatUsedToHardCodeTheEnglishText_NoLongerDoes(string relativePath, string forbidden)
    {
        string source = File.ReadAllText(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", relativePath));

        Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    private static async Task<Func<string, string>> LocalizerAsync(string language)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), language);
        return localizer.GetString;
    }
}
