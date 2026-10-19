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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Core.Redaction;
using static Heimdall.App.Tests.MaskedCopyTestSupport;

namespace Heimdall.App.Tests;

/// <summary>
/// Covers <c>MaskedCopyDialogViewModel</c>: what the review dialog shows and what Copy sends.
/// </summary>
public sealed class MaskedCopyDialogViewModelTests
{
    private const string SecretLine = "login ok\npassword=" + Secret + "\n";
    private const string HostLine = "connected to 10.0.0.5 as root\n";

    private static async Task<MaskedCopyDialogViewModel> CreateAsync(
        string text,
        bool truncated,
        RecordingClipboard clipboard,
        string locale = "en") =>
        await MaskedCopyDialogViewModel.CreateAsync(text, truncated, await LocalizerAsync(locale), clipboard);

    [Fact]
    public async Task TheMaskedText_HasNoSecret_AndCountsOnlyTheRulesThatMasked()
    {
        var viewModel = await CreateAsync(SecretLine, false, new RecordingClipboard());
        var localizer = await LocalizerAsync();

        Assert.DoesNotContain(Secret, viewModel.MaskedText, StringComparison.Ordinal);
        Assert.Contains("[masked:" + RedactionRuleIds.KeyValueSecret + "]", viewModel.MaskedText, StringComparison.Ordinal);
        string line = Assert.Single(viewModel.CountLines);
        Assert.Equal(
            localizer.Format(
                "MaskedCopyCountLine",
                localizer["MaskedCopyRuleKeyValueSecret"],
                localizer.FormatCount(1, "MaskedCopyOccurrencesOne", "MaskedCopyOccurrences", 1)),
            line);
        Assert.True(viewModel.HasCounts);
        Assert.False(viewModel.IsNothingMasked);
    }

    [Fact]
    public async Task ATextWithNoSecret_ListsNoCount_AndSaysSo()
    {
        var viewModel = await CreateAsync("ls -la\ntotal 0\n", false, new RecordingClipboard());

        Assert.Empty(viewModel.CountLines);
        Assert.False(viewModel.HasCounts);
        Assert.True(viewModel.IsNothingMasked);
    }

    [Fact]
    public async Task TheHostOption_ReRunsTheRedactorWithPseudonyms_AndRefreshesTextAndCounts()
    {
        var viewModel = await CreateAsync(HostLine, false, new RecordingClipboard());
        var localizer = await LocalizerAsync();
        Assert.Contains("10.0.0.5", viewModel.MaskedText, StringComparison.Ordinal);
        Assert.Empty(viewModel.CountLines);

        viewModel.MaskHosts = true;
        await viewModel.LastRerun;

        Assert.DoesNotContain("10.0.0.5", viewModel.MaskedText, StringComparison.Ordinal);
        Assert.Contains("ip-1", viewModel.MaskedText, StringComparison.Ordinal);
        string line = Assert.Single(viewModel.CountLines);
        Assert.Contains(localizer["MaskedCopyRuleIpv4Address"], line, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task TurningTheHostOptionOff_RestoresTheUnpseudonymizedText()
    {
        var viewModel = await CreateAsync(HostLine, false, new RecordingClipboard());

        viewModel.MaskHosts = true;
        await viewModel.LastRerun;
        viewModel.MaskHosts = false;
        await viewModel.LastRerun;

        Assert.Contains("10.0.0.5", viewModel.MaskedText, StringComparison.Ordinal);
        Assert.Empty(viewModel.CountLines);
    }

    /// <summary>
    /// Producer: <c>MaskedCopyDialogViewModel.RerunAsync</c> catch branch, reached by toggling the host
    /// option when the redactor throws (here a null source text, which <c>TextRedactor.Redact</c> rejects).
    /// The dialog must show its own message, not the one for a failed terminal read.
    /// </summary>
    [Fact]
    public async Task AFailedRerun_ShowsTheRerunMessage_NotTheReadFailure_AndDisablesCopy()
    {
        var localizer = await LocalizerAsync();
        var clipboard = new RecordingClipboard();
        var initial = RedactionResult.Success("masked", new Dictionary<string, int>());
        var viewModel = MaskedCopyDialogViewModel.FromResult(null!, false, initial, localizer, clipboard);

        viewModel.MaskHosts = true;
        await viewModel.LastRerun;

        Assert.True(viewModel.IsRefused);
        Assert.Equal(localizer["MaskedCopyRerunFailed"], viewModel.RefusedText);
        Assert.NotEqual(localizer["MaskedCopyStatusFailed"], viewModel.RefusedText);
        Assert.False(viewModel.CanCopy);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(string.Empty, viewModel.MaskedText);
    }

    [Fact]
    public async Task ARefusedResult_ShowsTheRefusal_AndDisablesCopy()
    {
        var localizer = await LocalizerAsync();
        var clipboard = new RecordingClipboard();
        var refused = RedactionResult.Refusal(RedactionRuleIds.Jwt, new Dictionary<string, int>());

        var viewModel = MaskedCopyDialogViewModel.FromResult(SecretLine, false, refused, localizer, clipboard);

        Assert.True(viewModel.IsRefused);
        Assert.False(viewModel.CanCopy);
        Assert.False(viewModel.CopyCommand.CanExecute(null));
        Assert.Contains(localizer["MaskedCopyRuleJwt"], viewModel.RefusedText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.MaskedText);

        viewModel.CopyCommand.Execute(null);

        Assert.Empty(clipboard.Texts);
        Assert.False(viewModel.Copied);
    }

    [Fact]
    public async Task Copy_SendsExactlyTheMaskedText_AndNothingElse_ThenCloses()
    {
        var clipboard = new RecordingClipboard();
        var viewModel = await CreateAsync(SecretLine, false, clipboard);
        int closes = 0;
        viewModel.CloseRequested += () => closes++;

        viewModel.CopyCommand.Execute(null);

        Assert.Equal([viewModel.MaskedText], clipboard.Texts);
        Assert.DoesNotContain(Secret, clipboard.Texts[0], StringComparison.Ordinal);
        Assert.True(viewModel.Copied);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task Cancel_CopiesNothing_AndCloses()
    {
        var clipboard = new RecordingClipboard();
        var viewModel = await CreateAsync(SecretLine, false, clipboard);
        int closes = 0;
        viewModel.CloseRequested += () => closes++;

        viewModel.CancelCommand.Execute(null);

        Assert.Empty(clipboard.Texts);
        Assert.False(viewModel.Copied);
        Assert.Equal(1, closes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATruncatedRead_ShowsTheNotice(bool truncated)
    {
        var viewModel = await CreateAsync(SecretLine, truncated, new RecordingClipboard());

        Assert.Equal(truncated, viewModel.IsTruncated);
    }

    /// <summary>
    /// Every rule the catalogue can count has a display name in every shipped locale, so a count
    /// line never falls back to a raw rule id.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task EveryCatalogueRule_HasADisplayNameInEveryLocale(string locale)
    {
        LocalizationManager localizer = await LocalizerAsync(locale);
        var text = string.Join(
            "\n",
            "-----BEGIN RSA PRIVATE KEY-----\nabc\n-----END RSA PRIVATE KEY-----",
            "Authorization: Bearer abcdefghijklmnop",
            "https://user:pw12345@example.com/",
            "password=" + Secret,
            "AKIAIOSFODNN7EXAMPLE");

        var viewModel = await MaskedCopyDialogViewModel.CreateAsync(text, false, localizer, new RecordingClipboard());

        Assert.NotEmpty(viewModel.CountLines);
        foreach (RedactionRule rule in RedactionRuleCatalog.Default)
        {
            Assert.DoesNotContain(viewModel.CountLines, line => line.Contains(rule.Id, StringComparison.Ordinal));
        }
    }
}
