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
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// The too-many-failures message states how many agent keys were held, when that is known.
/// </summary>
public sealed class SshFailureLocalizerAgentKeyCountTests
{
    private static readonly SshFailureInfo TooMany =
        new(SshFailureCode.TooManyAuthFailures, "Too many auth failures.", true);

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task PluralKey_PutsTheCountIntoThePlaceholder(string locale)
    {
        LocalizationManager localizer = await LoadAsync(locale);

        string message = SshFailureLocalizer.Localize(TooMany, localizer, "host", 7).Message;

        Assert.Equal(localizer.Format(SshAuthFailureLocaleKeys.TooManyAuthFailuresAgentKeys, 7), message);
        Assert.Contains("7", message, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task SingularKey_IsUsedForOneKey(string locale)
    {
        LocalizationManager localizer = await LoadAsync(locale);

        string message = SshFailureLocalizer.Localize(TooMany, localizer, "host", 1).Message;

        Assert.Equal(localizer[SshAuthFailureLocaleKeys.TooManyAuthFailuresOneAgentKey], message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task PlainKey_IsUsedWhenTheCountIsUnknownOrZero(int? count)
    {
        LocalizationManager localizer = await LoadAsync("en");

        string message = SshFailureLocalizer.Localize(TooMany, localizer, "host", count).Message;

        Assert.Equal(localizer["ErrorSshTooManyAuthFailures"], message);
    }

    private static async Task<LocalizationManager> LoadAsync(string locale)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return localizer;
    }
}
