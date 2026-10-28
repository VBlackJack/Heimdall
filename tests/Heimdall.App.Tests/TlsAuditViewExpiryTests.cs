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
using Heimdall.App.Views.Tools;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The TLS audit's certificate card words the days left in the language of the session, with
/// the same key the certificate inspector uses for the same figure.
/// </summary>
public sealed class TlsAuditViewExpiryTests
{
    [Theory]
    [InlineData("en", "2027-01-05 10:30:00 UTC (42 days remaining)")]
    [InlineData("fr", "2027-01-05 10:30:00 UTC (42 jours restants)")]
    [InlineData("es", "2027-01-05 10:30:00 UTC (42 días restantes)")]
    public async Task FormatCertificateExpiry_WordsTheDaysLeftInTheLocale(string locale, string expected)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);

        string text = TlsAuditView.FormatCertificateExpiry(
            new DateTime(2027, 1, 5, 10, 30, 0, DateTimeKind.Utc),
            42,
            localizer["ToolCertDaysRemaining"]);

        Assert.Equal(expected, text);
    }
}
