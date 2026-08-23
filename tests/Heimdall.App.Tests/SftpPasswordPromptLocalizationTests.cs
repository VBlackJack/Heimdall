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
using Heimdall.App.Localization;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The three keys the SFTP password prompt shows, in both catalogues.
/// </summary>
/// <remarks>
/// There is no general parity guard between the two locale files in this repository -
/// every locale test enumerates its own keys by hand. A key added to English only would
/// ship silently and show a French user the raw key name, because the localizer returns
/// the key verbatim when it cannot resolve it.
/// </remarks>
public sealed class SftpPasswordPromptLocalizationTests
{
    [Theory]
    [InlineData("en", SshLocalizationKeys.DialogSftpPasswordPromptTitle)]
    [InlineData("en", SshLocalizationKeys.DialogSftpPasswordPromptNoCredential)]
    [InlineData("en", SshLocalizationKeys.DialogSftpPasswordPromptRefused)]
    [InlineData("fr", SshLocalizationKeys.DialogSftpPasswordPromptTitle)]
    [InlineData("fr", SshLocalizationKeys.DialogSftpPasswordPromptNoCredential)]
    [InlineData("fr", SshLocalizationKeys.DialogSftpPasswordPromptRefused)]
    public async Task PromptKey_ResolvesInBothCatalogues(string locale, string key)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);

        string value = localizer[key];

        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.NotEqual(key, value);
    }

    [Theory]
    [InlineData("en", SshLocalizationKeys.DialogSftpPasswordPromptNoCredential)]
    [InlineData("en", SshLocalizationKeys.DialogSftpPasswordPromptRefused)]
    [InlineData("fr", SshLocalizationKeys.DialogSftpPasswordPromptNoCredential)]
    [InlineData("fr", SshLocalizationKeys.DialogSftpPasswordPromptRefused)]
    public async Task PromptMessage_NamesTheAccountItIsAskingAbout(string locale, string key)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);

        // A password box that does not say which account it is for is a box a careful
        // user should refuse to fill in.
        Assert.Contains("{0}", localizer[key], StringComparison.Ordinal);
    }
}
