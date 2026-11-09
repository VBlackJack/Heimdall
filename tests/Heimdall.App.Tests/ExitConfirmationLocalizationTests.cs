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
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The exit confirmation, built from the shipped locale files. The localizer swallows a
/// malformed template and returns it raw, so a count that is not substituted shows up as a
/// brace in the text, and that is what these tests look for.
/// </summary>
public sealed class ExitConfirmationLocalizationTests
{
    private const int OpenSessionCount = 7;
    private const int ConnectedSessionCount = 4;

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task BuildExitConfirmation_ShippedLocale_CarriesBothCountsAndNoPlaceholder(string language)
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), language);

        (string title, string message) = ShutdownDecisions.BuildExitConfirmation(
            localizer,
            OpenSessionCount,
            ConnectedSessionCount);

        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.NotEqual("ConfirmCloseSessionGroupTitle", title);
        Assert.Contains(OpenSessionCount.ToString(System.Globalization.CultureInfo.InvariantCulture), message, StringComparison.Ordinal);
        Assert.Contains(ConnectedSessionCount.ToString(System.Globalization.CultureInfo.InvariantCulture), message, StringComparison.Ordinal);
        Assert.DoesNotContain("{", message, StringComparison.Ordinal);
        Assert.DoesNotContain("}", message, StringComparison.Ordinal);
        Assert.DoesNotContain("{", title, StringComparison.Ordinal);
    }
}
