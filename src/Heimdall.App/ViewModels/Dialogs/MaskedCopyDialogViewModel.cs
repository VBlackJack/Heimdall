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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Redaction;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// The "Copy output, secrets masked" dialog: the terminal text with its secrets masked, how many
/// of each kind were masked, the option to mask host names and addresses too, and a Copy that
/// puts exactly the masked text on the clipboard.
/// </summary>
/// <remarks>
/// <para>
/// Fail closed: when the redactor refuses (a rule ran out of time) the dialog shows the refusal
/// and Copy stays disabled; the unmasked text is never offered as a fallback. Copy is also
/// disabled while a re-run is in flight, so the clipboard never receives a text the screen does
/// not show.
/// </para>
/// <para>
/// The unmasked text is held only to re-run the redactor when the host option changes; it is not
/// exposed and nothing is written to disk.
/// </para>
/// </remarks>
public sealed partial class MaskedCopyDialogViewModel : ObservableObject
{
    private static readonly IReadOnlyDictionary<string, string> RuleLabelKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RedactionRuleIds.PrivateKeyBlock] = "MaskedCopyRulePrivateKeyBlock",
        [RedactionRuleIds.AuthorizationHeader] = "MaskedCopyRuleAuthorizationHeader",
        [RedactionRuleIds.UrlCredentials] = "MaskedCopyRuleUrlCredentials",
        [RedactionRuleIds.CliPassword] = "MaskedCopyRuleCliPassword",
        [RedactionRuleIds.KeyValueSecret] = "MaskedCopyRuleKeyValueSecret",
        [RedactionRuleIds.AwsAccessKey] = "MaskedCopyRuleAwsAccessKey",
        [RedactionRuleIds.GithubToken] = "MaskedCopyRuleGithubToken",
        [RedactionRuleIds.SlackToken] = "MaskedCopyRuleSlackToken",
        [RedactionRuleIds.Jwt] = "MaskedCopyRuleJwt",
        [RedactionRuleIds.PasswordPrompt] = "MaskedCopyRulePasswordPrompt",
        [RedactionRuleIds.Ipv4Address] = "MaskedCopyRuleIpv4Address",
        [RedactionRuleIds.HostName] = "MaskedCopyRuleHostName",
    };

    private readonly string _sourceText;
    private readonly LocalizationManager _localizer;
    private readonly IClipboardService _clipboard;
    private int _redactionVersion;

    private MaskedCopyDialogViewModel(
        string sourceText,
        bool truncated,
        LocalizationManager localizer,
        IClipboardService clipboard)
    {
        _sourceText = sourceText;
        _localizer = localizer;
        _clipboard = clipboard;
        IsTruncated = truncated;
    }

    /// <summary>Raised when the dialog should close (after a successful Copy).</summary>
    public event Action? CloseRequested;

    /// <summary>The dialog title.</summary>
    public string DialogTitle => _localizer["MaskedCopyDialogTitle"];

    /// <summary>The line that says nothing leaves the machine until Copy.</summary>
    public string IntroText => _localizer["MaskedCopyIntro"];

    /// <summary>Accessible name of the masked text box.</summary>
    public string TextAccessibleName => _localizer["MaskedCopyTextA11y"];

    /// <summary>Accessible name of the per-rule count list.</summary>
    public string CountsAccessibleName => _localizer["MaskedCopyCountsA11y"];

    /// <summary>Label of the option that also masks host names and addresses.</summary>
    public string HostsOptionLabel => _localizer["MaskedCopyHostsOption"];

    /// <summary>Label of the Copy button.</summary>
    public string CopyLabel => _localizer["MaskedCopyCopy"];

    /// <summary>Label of the Cancel button.</summary>
    public string CancelLabel => _localizer["MaskedCopyCancel"];

    /// <summary>The read was cut to the size cap: only the most recent output is shown.</summary>
    public bool IsTruncated { get; }

    /// <summary>The truncation notice.</summary>
    public string TruncatedNotice => _localizer["MaskedCopyTruncatedNotice"];

    /// <summary>The text with its secrets masked; empty when the redactor refused.</summary>
    [ObservableProperty]
    private string _maskedText = string.Empty;

    /// <summary>One line per rule that masked something: its name and how many occurrences.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _countLines = [];

    /// <summary>True when at least one rule masked something.</summary>
    [ObservableProperty]
    private bool _hasCounts;

    /// <summary>True when the redactor ran and found nothing to mask.</summary>
    [ObservableProperty]
    private bool _isNothingMasked;

    /// <summary>The "no rule found a secret" line, so a clean text is not read as a skipped check.</summary>
    public string NothingMaskedText => _localizer["MaskedCopyNothingMasked"];

    /// <summary>True when a rule hit its time limit and nothing can be copied.</summary>
    [ObservableProperty]
    private bool _isRefused;

    /// <summary>The refusal message, naming the rule; empty unless <see cref="IsRefused"/>.</summary>
    [ObservableProperty]
    private string _refusedText = string.Empty;

    /// <summary>True while a re-run of the redactor is in flight.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Also mask IPv4 addresses and host names, replaced by stable pseudonyms.</summary>
    [ObservableProperty]
    private bool _maskHosts;

    /// <summary>True once Copy has put the masked text on the clipboard.</summary>
    public bool Copied { get; private set; }

    /// <summary>The task of the last re-run; tests await it.</summary>
    internal Task LastRerun { get; private set; } = Task.CompletedTask;

    /// <summary>Copy is possible only for a settled, non-refused result.</summary>
    public bool CanCopy => !IsRefused && !IsBusy;

    /// <summary>
    /// Redacts <paramref name="sourceText"/> off the calling thread and builds the dialog around the result.
    /// </summary>
    /// <param name="sourceText">The unmasked terminal text.</param>
    /// <param name="truncated">True when the terminal cut the oldest output.</param>
    /// <param name="localizer">Words the dialog.</param>
    /// <param name="clipboard">Receives the masked text on Copy.</param>
    public static async Task<MaskedCopyDialogViewModel> CreateAsync(
        string sourceText,
        bool truncated,
        LocalizationManager localizer,
        IClipboardService clipboard)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(clipboard);

        RedactionResult result = await RedactAsync(sourceText, pseudonymizeHosts: false).ConfigureAwait(true);
        return FromResult(sourceText, truncated, result, localizer, clipboard);
    }

    /// <summary>
    /// Builds the dialog around a redaction that has already run. A refused result (a rule ran
    /// out of time) is the one outcome a real input cannot be made to produce on demand, so this
    /// is also how that outcome is reached.
    /// </summary>
    /// <param name="sourceText">The unmasked terminal text, kept to re-run the redactor.</param>
    /// <param name="truncated">True when the terminal cut the oldest output.</param>
    /// <param name="result">The first redaction of <paramref name="sourceText"/>.</param>
    /// <param name="localizer">Words the dialog.</param>
    /// <param name="clipboard">Receives the masked text on Copy.</param>
    internal static MaskedCopyDialogViewModel FromResult(
        string sourceText,
        bool truncated,
        RedactionResult result,
        LocalizationManager localizer,
        IClipboardService clipboard)
    {
        var viewModel = new MaskedCopyDialogViewModel(sourceText, truncated, localizer, clipboard);
        viewModel.Apply(result);
        return viewModel;
    }

    /// <summary>Puts exactly the masked text on the clipboard, then asks the dialog to close.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        // Execute does not consult CanExecute: the refusal must hold for a caller that skips it.
        if (!CanCopy)
        {
            return;
        }

        _clipboard.SetText(MaskedText);
        Copied = true;
        CloseRequested?.Invoke();
    }

    /// <summary>Asks the dialog to close without copying.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    partial void OnMaskHostsChanged(bool value) => LastRerun = RerunAsync(value);

    partial void OnIsRefusedChanged(bool value) => RefreshCanCopy();

    partial void OnIsBusyChanged(bool value) => RefreshCanCopy();

    private void RefreshCanCopy()
    {
        OnPropertyChanged(nameof(CanCopy));
        CopyCommand.NotifyCanExecuteChanged();
    }

    private async Task RerunAsync(bool pseudonymizeHosts)
    {
        int version = ++_redactionVersion;
        IsBusy = true;
        try
        {
            RedactionResult result = await RedactAsync(_sourceText, pseudonymizeHosts).ConfigureAwait(true);

            // Only the latest toggle may write: a slower, older run must not overwrite it.
            if (version == _redactionVersion)
            {
                Apply(result);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Masked copy: the redaction re-run failed: {ex.GetType().Name}");
            if (version == _redactionVersion)
            {
                ShowFailure();
            }
        }
        finally
        {
            if (version == _redactionVersion)
            {
                IsBusy = false;
            }
        }
    }

    private static Task<RedactionResult> RedactAsync(string text, bool pseudonymizeHosts) =>
        Task.Run(() => TextRedactor.Redact(text, new RedactionOptions { PseudonymizeHosts = pseudonymizeHosts }));

    private void Apply(RedactionResult result)
    {
        if (result.Refused)
        {
            ShowRefusal(_localizer.Format("MaskedCopyRefused", RuleLabel(result.RefusedRuleId ?? string.Empty)));
            return;
        }

        MaskedText = result.Text;
        IsRefused = false;
        RefusedText = string.Empty;

        // The catalogue's order, so the lines do not shuffle between runs.
        CountLines =
        [
            .. RedactionRuleCatalog.Default
                .Select(rule => (rule.Id, Count: result.Counts.TryGetValue(rule.Id, out int count) ? count : 0))
                .Where(entry => entry.Count > 0)
                .Select(entry => _localizer.Format(
                    "MaskedCopyCountLine",
                    RuleLabel(entry.Id),
                    _localizer.FormatCount(entry.Count, "MaskedCopyOccurrencesOne", "MaskedCopyOccurrences", entry.Count)))
        ];
        HasCounts = CountLines.Count > 0;
        IsNothingMasked = !HasCounts;
    }

    private void ShowRefusal(string message)
    {
        MaskedText = string.Empty;
        CountLines = [];
        HasCounts = false;
        IsNothingMasked = false;
        IsRefused = true;
        RefusedText = message;
    }

    private void ShowFailure()
    {
        // A re-run that threw is treated as a refusal: nothing is offered for the clipboard.
        ShowRefusal(_localizer["MaskedCopyRerunFailed"]);
    }

    private string RuleLabel(string ruleId) =>
        RuleLabelKeys.TryGetValue(ruleId, out string? key) ? _localizer[key] : ruleId;
}
