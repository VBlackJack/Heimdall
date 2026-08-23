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
/// What the certificate question says, given what the profile already trusts.
/// </summary>
public sealed class RdpCertificatePromptTextTests
{
    [Fact]
    public void AlreadyTrustedKey_FirstCertificateEver_SaysNothing()
    {
        // There is nothing to reassure about: the profile has never trusted anything for
        // this name, so the plain question is the honest one. "You already trust 0 others"
        // would be noise exactly where the alarm is appropriate.
        Assert.Null(RdpCertificatePromptText.AlreadyTrustedKey(0));
    }

    [Fact]
    public void AlreadyTrustedKey_ExactlyOne_UsesTheSingularSentence()
        => Assert.Equal(
            RdpCertificatePromptLocaleKeys.AlreadyTrustedOne,
            RdpCertificatePromptText.AlreadyTrustedKey(1));

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void AlreadyTrustedKey_Several_UsesThePluralSentence(int count)
        => Assert.Equal(
            RdpCertificatePromptLocaleKeys.AlreadyTrustedMany,
            RdpCertificatePromptText.AlreadyTrustedKey(count));

    [Fact]
    public async Task SingularSentence_CarriesNoNumberField()
    {
        LocalizationManager english = await CreateLocalizerAsync("en");
        LocalizationManager french = await CreateLocalizerAsync("fr");

        // The singular says "1 other certificate" in words, so a substitution field here
        // would render as a literal brace to the user - the failure a plural-by-format
        // approach produces and that two separate keys exist to avoid.
        Assert.DoesNotContain("{0}", english[RdpCertificatePromptLocaleKeys.AlreadyTrustedOne], StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", french[RdpCertificatePromptLocaleKeys.AlreadyTrustedOne], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PluralSentence_HasSomewhereToPutTheCount()
    {
        LocalizationManager english = await CreateLocalizerAsync("en");
        LocalizationManager french = await CreateLocalizerAsync("fr");

        Assert.Contains("{0}", english[RdpCertificatePromptLocaleKeys.AlreadyTrustedMany], StringComparison.Ordinal);
        Assert.Contains("{0}", french[RdpCertificatePromptLocaleKeys.AlreadyTrustedMany], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Message_NamesTheProfileAndTheAddress()
    {
        LocalizationManager english = await CreateLocalizerAsync("en");

        // Two fields, because "a certificate changed" without saying WHICH profile and
        // WHERE is an alarm the user cannot act on.
        string message = english[RdpCertificatePromptLocaleKeys.Message];
        Assert.Contains("{0}", message, StringComparison.Ordinal);
        Assert.Contains("{1}", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryKey_IsTranslatedInBothCatalogues()
    {
        LocalizationManager english = await CreateLocalizerAsync("en");
        LocalizationManager french = await CreateLocalizerAsync("fr");

        // Reflected rather than listed, so a key added later without a translation is a
        // red test rather than silent drift.
        string[] keys =
        [
            .. typeof(RdpCertificatePromptLocaleKeys)
                .GetFields()
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
        ];

        Assert.Equal(8, keys.Length);
        foreach (string key in keys)
        {
            Assert.NotEqual(key, english[key]);
            Assert.NotEqual(key, french[key]);
            Assert.NotEqual(english[key], french[key]);
        }
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
