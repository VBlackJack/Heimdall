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
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.ViewModels.Dialogs;

public sealed record ImportKnownHostsConflictSelection(
    string Host,
    int Port,
    string ImportedFingerprint,
    string Algorithm,
    bool ReplaceWithImported);

public sealed record ImportKnownHostsConflictResolution(
    IReadOnlyList<ImportKnownHostsConflictSelection> Selections);

public sealed partial class ImportKnownHostsConflictDialogViewModel : ObservableObject
{
    public ImportKnownHostsConflictDialogViewModel(
        IReadOnlyList<KnownHostsImportConflict> conflicts,
        LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(localizer);

        DialogTitle = localizer["DialogImportConflictTitle"];
        DialogHint = localizer["DialogImportConflictHint"];
        ApplyLabel = localizer["BtnImportConflictApply"];
        CancelLabel = localizer["BtnCancel"];
        KeepExistingLabel = localizer["OptImportConflictKeepExisting"];
        ReplaceLabel = localizer["OptImportConflictReplace"];

        Items = new ObservableCollection<ImportKnownHostsConflictRowViewModel>(
            conflicts.Select(conflict => new ImportKnownHostsConflictRowViewModel(
                conflict,
                localizer.Format(
                    "A11yImportConflictRow",
                    HostKeyFormats.MakeKey(conflict.Host, conflict.Port),
                    conflict.Algorithm))));
    }

    public event Action<bool>? CloseRequested;

    public string DialogTitle { get; }

    public string DialogHint { get; }

    public string ApplyLabel { get; }

    public string CancelLabel { get; }

    public string KeepExistingLabel { get; }

    public string ReplaceLabel { get; }

    public ObservableCollection<ImportKnownHostsConflictRowViewModel> Items { get; }

    public ImportKnownHostsConflictResolution? Result { get; private set; }

    [RelayCommand]
    private void Apply()
    {
        Result = new ImportKnownHostsConflictResolution(
            Items.Select(static row => new ImportKnownHostsConflictSelection(
                row.Host,
                row.Port,
                row.ImportedFingerprint,
                row.Algorithm,
                row.ReplaceWithImported))
                .ToList());
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(false);
    }
}

public sealed partial class ImportKnownHostsConflictRowViewModel : ObservableObject, IAccessibleItemViewModel
{
    internal ImportKnownHostsConflictRowViewModel(
        KnownHostsImportConflict conflict,
        string accessibleName)
    {
        Host = conflict.Host;
        Port = conflict.Port;
        HostPort = HostKeyFormats.MakeKey(conflict.Host, conflict.Port);
        ExistingFingerprint = conflict.ExistingFingerprint;
        ImportedFingerprint = conflict.ImportedFingerprint;
        ExistingFingerprintDisplay = TruncateFingerprint(conflict.ExistingFingerprint);
        ImportedFingerprintDisplay = TruncateFingerprint(conflict.ImportedFingerprint);
        Algorithm = conflict.Algorithm;
        AccessibleName = accessibleName;
    }

    public string Host { get; }

    public int Port { get; }

    public string HostPort { get; }

    public string ExistingFingerprint { get; }

    public string ImportedFingerprint { get; }

    public string ExistingFingerprintDisplay { get; }

    public string ImportedFingerprintDisplay { get; }

    public string Algorithm { get; }

    /// <inheritdoc />
    public string AccessibleName { get; }

    /// <inheritdoc />
    public string? AccessibleHelpText => null;


    public bool KeepExisting
    {
        get => !ReplaceWithImported;
        set
        {
            if (value)
            {
                ReplaceWithImported = false;
            }
        }
    }

    [ObservableProperty]
    private bool _replaceWithImported;

    partial void OnReplaceWithImportedChanged(bool value) => OnPropertyChanged(nameof(KeepExisting));

    private static string TruncateFingerprint(string fingerprint)
        => fingerprint.Length <= 16 ? fingerprint : fingerprint[..16] + "...";
}
