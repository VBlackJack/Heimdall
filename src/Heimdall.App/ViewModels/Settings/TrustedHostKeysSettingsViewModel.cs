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

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.ViewModels.Settings;

public enum TrustedHostKeySortColumn
{
    HostPort,
    Algorithm,
    Source,
    FirstSeen,
    LastSeen,
    Fingerprint
}

/// <summary>
/// Settings sub-panel ViewModel for auditing trusted SSH host keys.
/// </summary>
public sealed partial class TrustedHostKeysSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IHostKeyTrustService _trustService;
    private readonly Func<KnownHostsImportReport> _importKnownHosts;
    private readonly Func<KnownHostsExportReport> _exportKnownHosts;
    private readonly LocalizationManager _localizer;
    private readonly IDialogService _dialogService;
    private readonly IClipboardService _clipboard;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<TrustedHostKeyRowViewModel> _allRows = [];
    private bool _disposed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private TrustedHostKeyRowViewModel? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecurityPolicies))]
    private int _certAuthorityCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecurityPolicies))]
    private int _revokedKeyCount;

    [ObservableProperty]
    private string _policySummaryText = string.Empty;

    [ObservableProperty]
    private string _policyDetailsTooltip = string.Empty;

    [ObservableProperty]
    private string _newPolicyHostPatterns = "*";

    [ObservableProperty]
    private string _newPolicyKeyOrFingerprint = string.Empty;

    public TrustedHostKeysSettingsViewModel(
        IHostKeyTrustService trustService,
        KnownHostsImporter knownHostsImporter,
        KnownHostsExporter knownHostsExporter,
        LocalizationManager localizer,
        IDialogService dialogService,
        IClipboardService clipboard,
        IUiDispatcher dispatcher)
        : this(
            trustService,
            () => knownHostsImporter.ImportFile(),
            () => knownHostsExporter.ExportFile(),
            localizer,
            dialogService,
            clipboard,
            dispatcher)
    {
    }

    internal TrustedHostKeysSettingsViewModel(
        IHostKeyTrustService trustService,
        Func<KnownHostsImportReport> importKnownHosts,
        Func<KnownHostsExportReport> exportKnownHosts,
        LocalizationManager localizer,
        IDialogService dialogService,
        IClipboardService clipboard,
        IUiDispatcher dispatcher)
    {
        _trustService = trustService;
        _importKnownHosts = importKnownHosts;
        _exportKnownHosts = exportKnownHosts;
        _localizer = localizer;
        _dialogService = dialogService;
        _clipboard = clipboard;
        _dispatcher = dispatcher;

        _trustService.EntryTrusted += OnEntryTrusted;
        _trustService.EntryRemoved += OnEntryRemoved;
        _trustService.EntryReplaced += OnEntryReplaced;
        _trustService.PolicyChanged += OnPolicyChanged;
        _localizer.LocaleChanged += OnLocaleChanged;

        Refresh();
    }

    public ObservableCollection<TrustedHostKeyRowViewModel> Rows { get; } = [];

    public ObservableCollection<KnownHostsPolicyRowViewModel> PolicyRows { get; } = [];

    public TrustedHostKeySortColumn SortColumn { get; private set; } = TrustedHostKeySortColumn.LastSeen;

    public bool SortAscending { get; private set; }

    public bool HasRows => _allRows.Count > 0;

    public bool HasVisibleRows => Rows.Count > 0;

    public bool IsEmptyStateVisible => !HasRows;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool HasSecurityPolicies => CertAuthorityCount > 0 || RevokedKeyCount > 0;

    public string DefaultKnownHostsPath => KnownHostsImporter.GetDefaultKnownHostsPath();

    partial void OnSearchTextChanged(string value) => ApplyFilterAndSort();

    [RelayCommand]
    public void Refresh()
    {
        _allRows.Clear();
        foreach (var (hostPort, entry, _) in _trustService.GetAllAlgorithmEntries())
        {
            _allRows.Add(CreateRow(hostPort, entry));
        }

        RefreshPolicySummary();
        ApplyFilterAndSort();
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void SortBy(string? columnName)
    {
        if (TryParseSortColumn(columnName, out var column))
        {
            if (SortColumn == column)
            {
                SortAscending = !SortAscending;
            }
            else
            {
                SortColumn = column;
                SortAscending = column is TrustedHostKeySortColumn.HostPort
                    or TrustedHostKeySortColumn.Algorithm
                    or TrustedHostKeySortColumn.Source;
            }
        }

        ApplyFilterAndSort();
    }

    [RelayCommand]
    private void CopyFingerprint(TrustedHostKeyRowViewModel? row)
    {
        if (row is null) return;

        _clipboard.SetText(row.Fingerprint);
        StatusMessage = _localizer.Format("ToastCopyFingerprint", row.HostPort);
    }

    [RelayCommand]
    private Task ShowDetailsAsync(TrustedHostKeyRowViewModel? row)
    {
        if (row is null) return Task.CompletedTask;

        var viewModel = new TrustedHostKeyDetailsDialogViewModel(row, _localizer);
        return _dialogService.ShowTrustedHostKeyDetailsAsync(viewModel);
    }

    [RelayCommand]
    private Task ShowSelectedDetailsAsync()
        => ShowDetailsAsync(SelectedRow);

    [RelayCommand]
    private async Task RemoveAsync(TrustedHostKeyRowViewModel? row)
    {
        if (row is null || !HostKeyFormats.TryParseKey(row.HostPort, out var host, out var port))
        {
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["DialogTrustedHostKeyRemoveTitle"],
            _localizer.Format("DialogTrustedHostKeyRemoveMessage", row.HostPort, row.Fingerprint),
            "warning").ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        if (_trustService.RemoveAlgorithm(host, port, row.Algorithm))
        {
            RefreshHostRows(row.HostPort);
            StatusMessage = _localizer.Format("ToastRemoveConfirmed", row.HostPort);
        }
    }

    [RelayCommand]
    private Task RemoveSelectedAsync()
        => RemoveAsync(SelectedRow);

    [RelayCommand]
    private async Task ImportKnownHostsAsync(CancellationToken cancellationToken)
    {
        KnownHostsImportReport report;
        try
        {
            report = await Task.Run(_importKnownHosts, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = _localizer.Format("ToastImportFailed", ex.Message);
            _dialogService.ShowWarning(_localizer["SettingsSshTrustedHostKeysSectionTitle"], StatusMessage);
            return;
        }

        var replaced = 0;
        var kept = report.Conflicts.Count;
        if (report.Conflicts.Count > 0)
        {
            var conflictVm = new ImportKnownHostsConflictDialogViewModel(report.Conflicts, _localizer);
            var resolution = await _dialogService.ShowImportKnownHostsConflictAsync(conflictVm).ConfigureAwait(true);
            if (resolution is null)
            {
                RefreshPolicySummary();
                StatusMessage = _localizer.Format("ToastImportSummary", report.Imported, report.Matched);
                return;
            }

            kept = 0;
            foreach (var selection in resolution.Selections)
            {
                if (!selection.ReplaceWithImported)
                {
                    kept++;
                    continue;
                }

                _trustService.Import(
                    selection.Host,
                    selection.Port,
                    selection.ImportedFingerprint,
                    selection.Algorithm,
                    DateTimeOffset.UtcNow,
                    selection.ImportedPublicKeyBase64);
                replaced++;
            }
        }

        RefreshPolicySummary();
        StatusMessage = report.Conflicts.Count == 0
            ? _localizer.Format("ToastImportSummary", report.Imported, report.Matched)
            : _localizer.Format("ToastImportConflictSummary", kept, replaced, report.Imported);
    }

    [RelayCommand]
    private async Task ExportKnownHostsAsync(CancellationToken cancellationToken)
    {
        KnownHostsExportReport report;
        try
        {
            report = await Task.Run(_exportKnownHosts, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = _localizer.Format("ToastExportFailed", ex.Message);
            _dialogService.ShowWarning(_localizer["SettingsSshTrustedHostKeysSectionTitle"], StatusMessage);
            return;
        }

        StatusMessage = _localizer.Format("ToastExportSummary", report.Written, DefaultKnownHostsPath);
        if (report.SkippedWithoutPublicKey > 0)
        {
            StatusMessage += " " + _localizer.Format("ToastExportSkipped", report.SkippedWithoutPublicKey);
            _dialogService.ShowWarning(_localizer["SettingsSshTrustedHostKeysSectionTitle"], StatusMessage);
        }
    }

    private void ApplyFilterAndSort()
    {
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? _allRows
            : _allRows
                .Where(row => row.HostPort.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();

        IOrderedEnumerable<TrustedHostKeyRowViewModel> ordered = SortColumn switch
        {
            TrustedHostKeySortColumn.HostPort => SortAscending
                ? filtered.OrderBy(static row => row.HostPort, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderByDescending(static row => row.HostPort, StringComparer.OrdinalIgnoreCase),
            TrustedHostKeySortColumn.Algorithm => SortAscending
                ? filtered.OrderBy(static row => row.Algorithm, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderByDescending(static row => row.Algorithm, StringComparer.OrdinalIgnoreCase),
            TrustedHostKeySortColumn.Source => SortAscending
                ? filtered.OrderBy(static row => row.SourceDisplay, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderByDescending(static row => row.SourceDisplay, StringComparer.OrdinalIgnoreCase),
            TrustedHostKeySortColumn.FirstSeen => SortAscending
                ? filtered.OrderBy(static row => row.FirstSeen)
                : filtered.OrderByDescending(static row => row.FirstSeen),
            TrustedHostKeySortColumn.Fingerprint => SortAscending
                ? filtered.OrderBy(static row => row.Fingerprint, StringComparer.Ordinal)
                : filtered.OrderByDescending(static row => row.Fingerprint, StringComparer.Ordinal),
            _ => SortAscending
                ? filtered.OrderBy(static row => row.LastSeen)
                : filtered.OrderByDescending(static row => row.LastSeen)
        };

        Rows.Clear();
        foreach (var row in ordered)
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasVisibleRows));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
    }

    private TrustedHostKeyRowViewModel CreateRow(string hostPort, HostKeyEntry entry)
    {
        HostKeyFormats.TryParseKey(hostPort, out var host, out var port);
        return new TrustedHostKeyRowViewModel(
            hostPort,
            host,
            port,
            entry,
            LocalizeSource(entry.Source),
            FormatDate(entry.FirstSeen),
            FormatDate(entry.LastSeen),
            _localizer["LblTrustedHostKeyPublicKeyNotAvailable"]);
    }

    private void RemoveRow(string hostPort)
    {
        RefreshHostRows(hostPort);
    }

    private void RefreshHostRows(string hostPort)
    {
        _allRows.RemoveAll(row => string.Equals(row.HostPort, hostPort, StringComparison.Ordinal));
        foreach (var (entryHostPort, entry, _) in _trustService.GetAllAlgorithmEntries())
        {
            if (string.Equals(entryHostPort, hostPort, StringComparison.Ordinal))
            {
                _allRows.Add(CreateRow(entryHostPort, entry));
            }
        }

        ApplyFilterAndSort();
    }

    private void RefreshLocalizedRows()
    {
        for (var index = 0; index < _allRows.Count; index++)
        {
            var row = _allRows[index];
            _allRows[index] = CreateRow(row.HostPort, row.Entry);
        }

        RefreshPolicySummary();
        ApplyFilterAndSort();
    }

    [RelayCommand]
    private void AddCertAuthority()
        => AddPolicy(isCertAuthority: true);

    [RelayCommand]
    private void AddRevokedKey()
        => AddPolicy(isCertAuthority: false);

    [RelayCommand]
    private async Task RemovePolicyAsync(KnownHostsPolicyRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            _localizer["DialogTrustedHostKeyPolicyRemoveTitle"],
            _localizer.Format("DialogTrustedHostKeyPolicyRemoveMessage", row.KindDisplay, row.HostPatterns, row.Fingerprint),
            "warning").ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        bool removed = row.IsCertAuthority
            ? _trustService.RemoveCertAuthority(row.HostPatterns, row.Fingerprint)
            : _trustService.RemoveRevokedKey(row.HostPatterns, row.Fingerprint);

        if (removed)
        {
            RefreshPolicySummary();
            StatusMessage = _localizer.Format("ToastPolicyRemoved", row.KindDisplay, row.HostPatterns);
        }
    }

    private void AddPolicy(bool isCertAuthority)
    {
        var hostPatterns = NewPolicyHostPatterns?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(hostPatterns))
        {
            StatusMessage = _localizer["TrustedHostKeysPolicyInvalidHost"];
            return;
        }

        if (!TryNormalizePolicyFingerprint(NewPolicyKeyOrFingerprint, out var fingerprint))
        {
            StatusMessage = _localizer["TrustedHostKeysPolicyInvalidKey"];
            return;
        }

        if (isCertAuthority)
        {
            _trustService.AddCertAuthority(hostPatterns, fingerprint);
        }
        else
        {
            _trustService.AddRevokedKey(hostPatterns, fingerprint);
        }

        NewPolicyKeyOrFingerprint = string.Empty;
        RefreshPolicySummary();
        var kindLabel = isCertAuthority
            ? _localizer["TrustedHostKeysPolicyKindCa"]
            : _localizer["TrustedHostKeysPolicyKindRevoked"];
        StatusMessage = _localizer.Format("ToastPolicyAdded", kindLabel, hostPatterns);
    }

    /// <summary>
    /// Accepts either a canonical <c>SHA256:...</c> fingerprint or an OpenSSH public key line
    /// (<c>&lt;key-type&gt; &lt;base64-blob&gt; [comment]</c>) and returns the normalized
    /// <c>SHA256:...</c> fingerprint.
    /// </summary>
    internal static bool TryNormalizePolicyFingerprint(string? rawInput, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        var trimmed = rawInput.Trim();
        if (trimmed.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = trimmed["SHA256:".Length..].Trim().TrimEnd('=');
            if (payload.Length < 32 || payload.Any(char.IsWhiteSpace))
            {
                return false;
            }

            fingerprint = "SHA256:" + payload;
            return true;
        }

        var tokens = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        string? base64Candidate = tokens.Length >= 2 ? tokens[1] : null;
        if (string.IsNullOrWhiteSpace(base64Candidate))
        {
            return false;
        }

        try
        {
            var blob = Convert.FromBase64String(base64Candidate);
            if (blob.Length < 16)
            {
                return false;
            }

            var hash = SHA256.HashData(blob);
            fingerprint = "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void RefreshPolicySummary()
    {
        var cas = _trustService.GetCertAuthorities();
        var revoked = _trustService.GetRevokedKeys();
        CertAuthorityCount = cas.Count;
        RevokedKeyCount = revoked.Count;

        PolicyRows.Clear();
        var caKind = _localizer["TrustedHostKeysPolicyKindCa"];
        foreach (var (pattern, fp) in cas)
        {
            PolicyRows.Add(new KnownHostsPolicyRowViewModel(true, caKind, pattern, fp));
        }

        var revokedKind = _localizer["TrustedHostKeysPolicyKindRevoked"];
        foreach (var (pattern, fp) in revoked)
        {
            PolicyRows.Add(new KnownHostsPolicyRowViewModel(false, revokedKind, pattern, fp));
        }

        if (!HasSecurityPolicies)
        {
            PolicySummaryText = _localizer["TrustedHostKeysPolicyEmpty"];
            PolicyDetailsTooltip = string.Empty;
            return;
        }

        PolicySummaryText = _localizer.Format("TrustedHostKeysPolicySummary", cas.Count, revoked.Count);

        var lines = new List<string>();
        if (cas.Count > 0)
        {
            lines.Add(_localizer["TrustedHostKeysPolicyCaHeading"]);
            foreach (var (pattern, fp) in cas.Take(10))
            {
                lines.Add($"  {pattern} ({fp})");
            }

            if (cas.Count > 10)
            {
                lines.Add($"  ... (+{cas.Count - 10})");
            }
        }

        if (revoked.Count > 0)
        {
            lines.Add(_localizer["TrustedHostKeysPolicyRevokedHeading"]);
            foreach (var (pattern, fp) in revoked.Take(10))
            {
                lines.Add($"  {pattern} ({fp})");
            }

            if (revoked.Count > 10)
            {
                lines.Add($"  ... (+{revoked.Count - 10})");
            }
        }

        PolicyDetailsTooltip = string.Join(Environment.NewLine, lines);
    }

    private string LocalizeSource(HostKeySource source) => source switch
    {
        HostKeySource.UserConfirmed => _localizer["HostKeySourceUserConfirmed"],
        HostKeySource.ImportedKnownHosts => _localizer["HostKeySourceImportedKnownHosts"],
        HostKeySource.Factory => _localizer["HostKeySourceFactory"],
        _ => _localizer["HostKeySourceUnknown"]
    };

    private string FormatDate(DateTimeOffset value)
        => value <= DateTimeOffset.MinValue.AddDays(1)
            ? _localizer["LblTrustedHostKeyDateUnknown"]
            : value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private void OnEntryTrusted(string hostPort, HostKeyEntry entry)
        => _dispatcher.Invoke(() => RefreshHostRows(hostPort));

    private void OnEntryRemoved(string hostPort)
        => _dispatcher.Invoke(() => RemoveRow(hostPort));

    private void OnEntryReplaced(string hostPort, HostKeyEntry oldEntry, HostKeyEntry newEntry)
        => _dispatcher.Invoke(() => RefreshHostRows(hostPort));

    private void OnPolicyChanged()
        => _dispatcher.Invoke(RefreshPolicySummary);

    private void OnLocaleChanged(string locale)
        => _dispatcher.Invoke(RefreshLocalizedRows);

    private static bool TryParseSortColumn(string? columnName, out TrustedHostKeySortColumn column)
    {
        column = TrustedHostKeySortColumn.LastSeen;
        return !string.IsNullOrWhiteSpace(columnName)
            && Enum.TryParse(columnName, ignoreCase: true, out column);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _trustService.EntryTrusted -= OnEntryTrusted;
        _trustService.EntryRemoved -= OnEntryRemoved;
        _trustService.EntryReplaced -= OnEntryReplaced;
        _trustService.PolicyChanged -= OnPolicyChanged;
        _localizer.LocaleChanged -= OnLocaleChanged;
    }
}

public sealed class KnownHostsPolicyRowViewModel(
    bool isCertAuthority,
    string kindDisplay,
    string hostPatterns,
    string fingerprint)
{
    public bool IsCertAuthority { get; } = isCertAuthority;

    public string KindDisplay { get; } = kindDisplay;

    public string HostPatterns { get; } = hostPatterns;

    public string Fingerprint { get; } = fingerprint;

    public string FingerprintDisplay { get; } = fingerprint.Length <= 22 ? fingerprint : fingerprint[..22] + "...";
}

public sealed partial class TrustedHostKeyRowViewModel : ObservableObject
{
    internal TrustedHostKeyRowViewModel(
        string hostPort,
        string host,
        int port,
        HostKeyEntry entry,
        string sourceDisplay,
        string firstSeenDisplay,
        string lastSeenDisplay,
        string publicKeyUnavailableDisplay)
    {
        HostPort = hostPort;
        Host = host;
        Port = port;
        Entry = entry;
        Fingerprint = entry.Fingerprint;
        FingerprintDisplay = TruncateFingerprint(entry.Fingerprint);
        Algorithm = entry.Algorithm;
        Source = entry.Source;
        SourceDisplay = sourceDisplay;
        FirstSeen = entry.FirstSeen;
        LastSeen = entry.LastSeen;
        FirstSeenDisplay = firstSeenDisplay;
        LastSeenDisplay = lastSeenDisplay;
        PublicKeyBase64 = entry.PublicKeyBase64;
        PublicKeyDisplay = string.IsNullOrWhiteSpace(entry.PublicKeyBase64)
            ? publicKeyUnavailableDisplay
            : entry.PublicKeyBase64;
    }

    public string HostPort { get; }

    public string Host { get; }

    public int Port { get; }

    public HostKeyEntry Entry { get; }

    public string Algorithm { get; }

    public HostKeySource Source { get; }

    public string SourceDisplay { get; }

    public DateTimeOffset FirstSeen { get; }

    public DateTimeOffset LastSeen { get; }

    public string FirstSeenDisplay { get; }

    public string LastSeenDisplay { get; }

    public string Fingerprint { get; }

    public string FingerprintDisplay { get; }

    public string? PublicKeyBase64 { get; }

    public string PublicKeyDisplay { get; }

    private static string TruncateFingerprint(string fingerprint)
        => fingerprint.Length <= 16 ? fingerprint : fingerprint[..16] + "...";
}
