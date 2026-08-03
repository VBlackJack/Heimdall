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
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class SftpDeleteLocalizationTests
{
    [Theory]
    [InlineData("en", "SftpDeleteRefusedExecUnavailable")]
    [InlineData("en", "SftpDeleteRefusedShellUnavailable")]
    [InlineData("en", "SftpDeleteFailedEntry")]
    [InlineData("en", "SftpDeletePartialSummary")]
    [InlineData("en", "SftpCutSourceNotDeleted")]
    [InlineData("fr", "SftpDeleteRefusedExecUnavailable")]
    [InlineData("fr", "SftpDeleteRefusedShellUnavailable")]
    [InlineData("fr", "SftpDeleteFailedEntry")]
    [InlineData("fr", "SftpDeletePartialSummary")]
    [InlineData("fr", "SftpCutSourceNotDeleted")]
    public async Task DeleteOutcomeKey_ExistsInLocale(string locale, string key)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);

        string value = localizer[key];

        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.NotEqual(key, value);
    }
}
