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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the dedicated known_hosts import preview dialog.
/// </summary>
public partial class ImportKnownHostsDialogViewModel(
    KnownHostsImporter importer,
    LocalizationManager localizer) : ObservableObject
{
    private readonly KnownHostsImporter _importer = importer;
    private readonly LocalizationManager _localizer = localizer;
    private bool _syncingSelection;
    private bool _allSelected;

    public ObservableCollection<KnownHostItemViewModel> Items { get; } = [];

    public ObservableCollection<KnownHostDiagnosticViewModel> Diagnostics { get; } = [];

    public string DialogTitle => _localizer["DialogTitleImportKnownHosts"];

    public string DialogConfirmLabel => _localizer["BtnConfirmImport"];

    public string DialogCancelLabel => _localizer["BtnCancel"];

    public string ImportColumnHeader => _localizer["HeaderImport"];

    public string HostColumnHeader => _localizer["HeaderHost"];

    public string PortColumnHeader => _localizer["HeaderPort"];

    public string FingerprintColumnHeader => _localizer["HeaderFingerprint"];

    public string StatusColumnHeader => _localizer["HeaderStatus"];

    public string NotesColumnHeader => _localizer["HeaderNotes"];

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public KnownHostsImportOutcome? Result { get; private set; }

    public event Action<bool>? CloseRequested;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _diagnosticsHeader = string.Empty;

    [ObservableProperty]
    private bool _isDiagnosticsExpanded;

    public bool AllSelected
    {
        get => _allSelected;
        set
        {
            if (SetProperty(ref _allSelected, value) && !_syncingSelection)
            {
                _syncingSelection = true;
                try
                {
                    foreach (var item in Items.Where(item => item.IsSelectable))
                    {
                        item.IsSelected = value;
                    }
                }
                finally
                {
                    _syncingSelection = false;
                }

                RefreshState();
            }
        }
    }

    public bool CanConfirm => Items.Any(item => item.IsSelectable && item.IsSelected);

    public async Task InitializeAsync(KnownHostsImportPreview preview, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        ct.ThrowIfCancellationRequested();
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        Items.Clear();
        Diagnostics.Clear();

        foreach (var row in preview.Rows)
        {
            var notes = row.Status switch
            {
                KnownHostsCandidateStatus.Existing => _localizer["NotesExistingSameFingerprint"],
                KnownHostsCandidateStatus.Conflict when !string.IsNullOrWhiteSpace(row.ExistingFingerprint) =>
                    _localizer.Format("NotesConflictVsStore", KnownHostItemViewModel.GetFingerprintDisplay(row.ExistingFingerprint)),
                KnownHostsCandidateStatus.Conflict => _localizer["NotesConflictIntraFile"],
                _ => string.Empty
            };

            var item = new KnownHostItemViewModel(
                row.Candidate,
                row.Candidate.Host,
                row.Candidate.Port,
                row.Candidate.Fingerprint,
                row.Status,
                GetStatusDisplay(row.Status),
                notes,
                _localizer.Format(
                    "A11yImportKnownHostRow",
                    row.Candidate.Host,
                    row.Candidate.Port,
                    GetStatusDisplay(row.Status)));
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        foreach (var diagnostic in preview.Diagnostics)
        {
            Diagnostics.Add(BuildDiagnostic(diagnostic));
        }

        RefreshState();
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        var selected = Items
            .Where(item => item.IsSelectable && item.IsSelected)
            .Select(item => item.Candidate)
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        Result = await _importer.ImportSelectedAsync(selected).ConfigureAwait(true);
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(false);
    }

    private string GetStatusDisplay(KnownHostsCandidateStatus status)
    {
        return status switch
        {
            KnownHostsCandidateStatus.New => _localizer["StatusKnownHostNew"],
            KnownHostsCandidateStatus.Existing => _localizer["StatusKnownHostExisting"],
            KnownHostsCandidateStatus.Conflict => _localizer["StatusKnownHostConflict"],
            _ => status.ToString()
        };
    }

    private KnownHostDiagnosticViewModel BuildDiagnostic(KnownHostsImportDiagnostic diagnostic)
    {
        var key = diagnostic.Code switch
        {
            KnownHostsDiagnosticCode.HashedEntryNotSupported => "DiagKnownHostsHashedEntryNotSupported",
            KnownHostsDiagnosticCode.CertAuthorityNotSupported => "DiagKnownHostsCertAuthorityNotSupported",
            KnownHostsDiagnosticCode.RevokedEntryNotSupported => "DiagKnownHostsRevokedEntryNotSupported",
            KnownHostsDiagnosticCode.UnsupportedHostPattern => "DiagKnownHostsUnsupportedHostPattern",
            KnownHostsDiagnosticCode.UnsupportedKeyType => "DiagKnownHostsUnsupportedKeyType",
            KnownHostsDiagnosticCode.MalformedLine => "DiagKnownHostsMalformedLine",
            KnownHostsDiagnosticCode.DuplicateFingerprintInSourceMerged => "DiagKnownHostsDuplicateFingerprintInSourceMerged",
            KnownHostsDiagnosticCode.IntraFileFingerprintConflict => "DiagKnownHostsIntraFileFingerprintConflict",
            KnownHostsDiagnosticCode.LegacyKeyType => "DiagKnownHostsLegacyKeyType",
            KnownHostsDiagnosticCode.FileTooLarge => "DiagKnownHostsFileTooLarge",
            KnownHostsDiagnosticCode.FileReadError => "DiagKnownHostsFileReadError",
            _ => null
        };

        // File-level diagnostics describe the whole file and carry no source line;
        // their templates take the context only, so "line 0" is never rendered.
        var message = key switch
        {
            "DiagKnownHostsFileTooLarge" or
            "DiagKnownHostsFileReadError" =>
                _localizer.Format(key, diagnostic.Context ?? string.Empty),
            "DiagKnownHostsUnsupportedHostPattern" or
            "DiagKnownHostsUnsupportedKeyType" or
            "DiagKnownHostsMalformedLine" or
            "DiagKnownHostsIntraFileFingerprintConflict" or
            "DiagKnownHostsLegacyKeyType" =>
                _localizer.Format(key, diagnostic.SourceLineNumber, diagnostic.Context ?? string.Empty),
            not null => _localizer.Format(key, diagnostic.SourceLineNumber),
            _ => diagnostic.Code.ToString()
        };

        return new KnownHostDiagnosticViewModel(
            diagnostic.Level == KnownHostsDiagnosticLevel.Warning
                ? _localizer["LevelDiagnosticWarning"]
                : _localizer["LevelDiagnosticInfo"],
            diagnostic.SourceLineNumber,
            message);
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KnownHostItemViewModel.IsSelected))
        {
            RefreshState();
        }
    }

    private void RefreshState()
    {
        _syncingSelection = true;
        try
        {
            AllSelected = Items.Count > 0 && Items.Where(item => item.IsSelectable).All(item => item.IsSelected);
        }
        finally
        {
            _syncingSelection = false;
        }

        SummaryText = _localizer.Format(
            "SummaryKnownHostsItems",
            Items.Count,
            Items.Count(item => item.Status == KnownHostsCandidateStatus.New),
            Items.Count(item => item.Status == KnownHostsCandidateStatus.Existing),
            Items.Count(item => item.Status == KnownHostsCandidateStatus.Conflict));
        DiagnosticsHeader = _localizer.Format("LabelDiagnosticsExpander", Diagnostics.Count);
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class KnownHostItemViewModel : ObservableObject, IAccessibleItemViewModel
{
    public KnownHostItemViewModel(
        KnownHostsImportCandidate candidate,
        string host,
        int port,
        string fingerprint,
        KnownHostsCandidateStatus status,
        string statusDisplay,
        string notes,
        string accessibleName)
    {
        Candidate = candidate;
        Host = host;
        Port = port;
        Fingerprint = fingerprint;
        FingerprintDisplay = GetFingerprintDisplay(fingerprint);
        Status = status;
        StatusDisplay = statusDisplay;
        Notes = notes;
        AccessibleName = accessibleName;
        _isSelected = status == KnownHostsCandidateStatus.New;
    }

    [ObservableProperty]
    private bool _isSelected;

    public KnownHostsImportCandidate Candidate { get; }

    public string Host { get; }

    public int Port { get; }

    public string Fingerprint { get; }

    public string FingerprintDisplay { get; }

    public KnownHostsCandidateStatus Status { get; }

    public string StatusDisplay { get; }

    public string Notes { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Composed by the parent, which owns the localizer, exactly as
    /// <see cref="StatusDisplay" /> and <see cref="Notes" /> already are.
    /// </remarks>
    public string AccessibleName { get; }

    /// <inheritdoc />
    public string? AccessibleHelpText => null;


    public bool IsSelectable => Status == KnownHostsCandidateStatus.New;

    public static string GetFingerprintDisplay(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        return fingerprint.Length > 25
            ? $"{fingerprint[..17]}...{fingerprint[^10..]}"
            : fingerprint;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!IsSelectable && value)
        {
            IsSelected = false;
        }
    }
}

public sealed class KnownHostDiagnosticViewModel
{
    public KnownHostDiagnosticViewModel(string levelDisplay, int lineNumber, string message)
    {
        LevelDisplay = levelDisplay;
        LineNumber = lineNumber;
        Message = message;
    }

    public string LevelDisplay { get; }

    public int LineNumber { get; }

    public bool HasLineNumber => LineNumber >= 0;

    public string Message { get; }
}
