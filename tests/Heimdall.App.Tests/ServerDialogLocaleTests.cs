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

public sealed class ServerDialogLocaleTests
{
    [Theory]
    [InlineData("en", "Choose the protocol", "host", "port")]
    [InlineData("fr", "Choisissez le protocole", "h\u00f4te", "port")]
    public async Task ServerDialogConnectionBasicsDesc_DoesNotMentionChooseProtocol(
        string locale,
        string obsoletePhrase,
        string hostTerm,
        string portTerm)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        string description = localizer["ServerDialogConnectionBasicsDesc"];

        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.DoesNotContain(obsoletePhrase, description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(hostTerm, description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(portTerm, description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task ServerDialogResolutionModeAutoTitle_ExistsInBothLocales(string locale)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        string text = localizer["ServerDialogResolutionModeAutoTitle"];

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Theory]
    [InlineData("en", "host", "fullscreen", "windowed")]
    [InlineData("fr", "h\u00f4te", "plein \u00e9cran", "fen\u00eatr\u00e9")]
    public async Task ServerDialogResolutionModeAutoDescription_MentionsHostAndFullscreen(
        string locale,
        string hostTerm,
        string fullscreenTerm,
        string windowedTerm)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        string text = localizer["ServerDialogResolutionModeAutoDescription"];

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(hostTerm, text, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            text.Contains(fullscreenTerm, StringComparison.OrdinalIgnoreCase)
            || text.Contains(windowedTerm, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task ServerDialogResolutionMoreOptions_ExistsInBothLocales(string locale)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        string text = localizer["ServerDialogResolutionMoreOptions"];

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task ServerDialogResolutionModeSwitchToAuto_ExistsInBothLocales(string locale)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        string text = localizer["ServerDialogResolutionModeSwitchToAuto"];

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Theory]
    [InlineData("en", "monitor")]
    [InlineData("fr", "moniteur")]
    public async Task ServerDialogResolutionModeMultimonUnavailableTooltip_ExistsInBothLocales(
        string locale,
        string expectedTerm)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        string text = localizer["ServerDialogResolutionModeMultimonUnavailableTooltip"];

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(expectedTerm, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("en", "ServerDialogSessionLoggingOverrideLabel")]
    [InlineData("en", "ServerDialogSessionLoggingOverrideInherit")]
    [InlineData("en", "ServerDialogSessionLoggingOverrideOn")]
    [InlineData("en", "ServerDialogSessionLoggingOverrideOff")]
    [InlineData("en", "ServerDialogSessionLoggingOverrideHint")]
    [InlineData("fr", "ServerDialogSessionLoggingOverrideLabel")]
    [InlineData("fr", "ServerDialogSessionLoggingOverrideInherit")]
    [InlineData("fr", "ServerDialogSessionLoggingOverrideOn")]
    [InlineData("fr", "ServerDialogSessionLoggingOverrideOff")]
    [InlineData("fr", "ServerDialogSessionLoggingOverrideHint")]
    public async Task SessionLoggingOverrideKeys_ExistInBothLocales(string locale, string key)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        string text = localizer[key];

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.NotEqual(key, text);
    }

    [Theory]
    [InlineData("en", "RdpAutofillRetryAction")]
    [InlineData("en", "RdpAutofillRetryTooltip")]
    [InlineData("en", "RdpAutofillDismissAction")]
    [InlineData("en", "RdpAutofillDismissTooltip")]
    [InlineData("fr", "RdpAutofillRetryAction")]
    [InlineData("fr", "RdpAutofillRetryTooltip")]
    [InlineData("fr", "RdpAutofillDismissAction")]
    [InlineData("fr", "RdpAutofillDismissTooltip")]
    public async Task RdpAutofillActionKeys_ExistInBothLocales(string locale, string key)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        string text = localizer[key];

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task RdpSplitDisplayResizeWarning_ExistsInBothLocales(string locale)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        string text = localizer["RdpSplitDisplayResizeWarning"];

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task SshProbeKeys_ExistInBothLocales(string locale)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);
        string[] keys =
        [
            "SshProbeMissingBanner",
            "SshProbeNonSshBanner",
            "SshProbeConnectionTimedOut",
            "SshProbeConnectionRefused",
            "SshProbeNetworkUnreachable",
            "SshProbeConnectionReset",
            "SshProbeUnknownFailure"
        ];

        foreach (string key in keys)
        {
            string text = localizer[key];

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(key, text);
        }
    }

    [Theory]
    [InlineData("en", "ServerDialogProtocolWinRmName")]
    [InlineData("en", "ServerDialogProtocolWinRmDesc")]
    [InlineData("en", "ServerDialogWinRmCredentials")]
    [InlineData("en", "ServerDialogWinRmIdentityMode")]
    [InlineData("en", "ServerDialogWinRmIdentityCurrentUser")]
    [InlineData("en", "ServerDialogWinRmIdentityCredential")]
    [InlineData("en", "ServerDialogWinRmSkipCertCheck")]
    [InlineData("en", "ServerDialogWinRmSkipCertCheckHint")]
    [InlineData("en", "ServerDialogWinRmUseSsl")]
    [InlineData("en", "ServerDialogWinRmUseSslHint")]
    [InlineData("en", "ServerDialogWinRmUseSslGatewayHint")]
    [InlineData("en", "ServerDialogSessionWinRm")]
    [InlineData("en", "ServerDialogPortLabelWinRm")]
    [InlineData("en", "ServerDialogPortHelpWinRm")]
    [InlineData("en", "ValidationWinRmPortRange")]
    [InlineData("fr", "ServerDialogProtocolWinRmName")]
    [InlineData("fr", "ServerDialogProtocolWinRmDesc")]
    [InlineData("fr", "ServerDialogWinRmCredentials")]
    [InlineData("fr", "ServerDialogWinRmIdentityMode")]
    [InlineData("fr", "ServerDialogWinRmIdentityCurrentUser")]
    [InlineData("fr", "ServerDialogWinRmIdentityCredential")]
    [InlineData("fr", "ServerDialogWinRmSkipCertCheck")]
    [InlineData("fr", "ServerDialogWinRmSkipCertCheckHint")]
    [InlineData("fr", "ServerDialogWinRmUseSsl")]
    [InlineData("fr", "ServerDialogWinRmUseSslHint")]
    [InlineData("fr", "ServerDialogWinRmUseSslGatewayHint")]
    [InlineData("fr", "ServerDialogSessionWinRm")]
    [InlineData("fr", "ServerDialogPortLabelWinRm")]
    [InlineData("fr", "ServerDialogPortHelpWinRm")]
    [InlineData("fr", "ValidationWinRmPortRange")]
    public async Task WinRmServerDialogKeys_ExistInBothLocales(string locale, string key)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        string text = localizer[key];

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
