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
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The shared session sources: MobaXterm files other people edit, whose sessions the tree lists
/// read-only.
/// </summary>
/// <remarks>
/// <para>Edited like the MobaXterm variables: the user's own entries, which the value comparison
/// cannot read back, kept by a factory reset, and validated row by row, a row in error refusing
/// the save.</para>
/// <para>The status of each row comes from the shared provider. Reload reads the saved sources
/// again and the tree follows; a save that changes the sources or the variables reloads them too,
/// through the session list, which watches the settings.</para>
/// <para>The post-connect approvals of shared sessions are not a panel value: they are never
/// exported, never imported, and a factory reset clears them at the next save.</para>
/// <para>Nor are the passwords a user chose to remember for a source: they live in their own
/// store, which an export, an import or a factory reset never touches. A row forgets them at once,
/// and a saved removal of the source forgets them through the provider.</para>
/// </remarks>
public partial class SettingsViewModel
{
    private readonly ISharedSessionProvider? _sharedSessions;
    private readonly SharedSessionSecretStore? _sharedSecrets;

    [ObservableProperty]
    private ObservableCollection<SharedSessionSourceItemViewModel> _sharedSessionSources = new();

    /// <summary>Set by a save refused over a source row; cleared by the next edit to the list.</summary>
    private string? _sharedSessionSourcesValidationError;

    /// <summary>
    /// Set by a factory reset: the next save also clears the post-connect approvals of shared
    /// sessions. Cleared by any reload of the panel from settings.
    /// </summary>
    private bool _clearSharedApprovalsOnSave;

    /// <summary>Whether a reload of the shared sources is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadSharedSessionSourcesCommand))]
    private bool _isReloadingSharedSessionSources;

    /// <summary>The task of the last reload; tests await it.</summary>
    internal Task SharedSessionSourcesReload { get; private set; } = Task.CompletedTask;

    /// <summary>Whether the next save clears the shared post-connect approvals.</summary>
    internal bool ClearsSharedApprovalsOnSave => _clearSharedApprovalsOnSave;

    /// <summary>Whether any row is in error, as last validated.</summary>
    public bool HasInvalidSharedSessionSources => SharedSessionSources.Any(row => !row.IsValid);

    /// <summary>Whether this panel can reload the shared sources at all.</summary>
    public bool CanReloadSharedSessions => _sharedSessions is not null;

    [RelayCommand]
    private void AddSharedSessionSource()
    {
        SharedSessionSources.Add(NewSharedSessionSourceRow(SharedSessionIds.NewSourceId(), "", "", enabled: true));
        MarkEditTheComparisonCannotSee();
    }

    [RelayCommand]
    private void RemoveSharedSessionSource(SharedSessionSourceItemViewModel? row)
    {
        if (row is null || !SharedSessionSources.Remove(row))
        {
            return;
        }

        MarkEditTheComparisonCannotSee();
    }

    private bool CanReloadSharedSessionSources() => _sharedSessions is not null && !IsReloadingSharedSessionSources;

    /// <summary>
    /// Reads the saved sources again; the tree follows through the provider's change event.
    /// </summary>
    /// <remarks>
    /// The saved sources, not the rows on screen: what the tree shows must be what the next start
    /// shows, and a row not saved yet would vanish then.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanReloadSharedSessionSources))]
    private Task ReloadSharedSessionSourcesAsync(CancellationToken cancellationToken)
    {
        SharedSessionSourcesReload = ReloadSharedSessionSourcesCoreAsync(cancellationToken);
        return SharedSessionSourcesReload;
    }

    private async Task ReloadSharedSessionSourcesCoreAsync(CancellationToken cancellationToken)
    {
        if (_sharedSessions is null)
        {
            return;
        }

        IsReloadingSharedSessionSources = true;
        try
        {
            AppSettings saved = await _configManager.LoadSettingsAsync();
            await _sharedSessions.LoadAsync(saved, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Reloading the shared session sources failed: {ex.Message}");
        }
        finally
        {
            IsReloadingSharedSessionSources = false;
        }

        RefreshSharedSessionSourceStatuses();
    }

    /// <summary>Puts the plain text of an import report on the clipboard; replaced by tests.</summary>
    internal Services.IClipboardService ReportClipboard { get; set; } = new Services.WpfClipboardService();

    /// <summary>Asks where to export an import report as CSV; replaced by tests.</summary>
    internal Func<string, string?>? ReportCsvPathPicker { get; set; }

    /// <summary>
    /// Shows the import report of a row's source, as of the source's last successful read.
    /// </summary>
    [RelayCommand]
    private Task ShowSharedSourceReportAsync(SharedSessionSourceItemViewModel? row)
    {
        if (row is null
            || _sharedSessions?.Sources.FirstOrDefault(
                state => string.Equals(state.SourceId, row.Id, StringComparison.Ordinal))?.Report is not { } report)
        {
            return Task.CompletedTask;
        }

        return _dialogService.ShowSharedSessionImportReportAsync(new Dialogs.SharedSessionImportReportDialogViewModel(
            report,
            _localizer,
            ReportClipboard,
            ReportCsvPathPicker
                ?? (proposed => Dialogs.SharedSessionImportReportDialogViewModel.PickCsvPathWithDialog(_localizer, proposed))));
    }

    /// <summary>
    /// Forgets the passwords remembered for a row's source on this computer. Not a panel value:
    /// it happens now, and Cancel does not bring them back.
    /// </summary>
    [RelayCommand]
    private async Task ForgetSharedSourcePasswordsAsync(SharedSessionSourceItemViewModel? row)
    {
        if (row is null || _sharedSecrets is null)
        {
            return;
        }

        await _sharedSecrets.ForgetSourceAsync(row.Id);
        RefreshSharedSessionSourceStatuses();
    }

    /// <summary>Replaces the rows with the sources in <paramref name="settings"/>.</summary>
    private void LoadSharedSessionSources(AppSettings settings)
    {
        UnsubscribeSharedSessionSourceTracking();

        SharedSessionSources = new ObservableCollection<SharedSessionSourceItemViewModel>(
            (settings.SharedSessionSources ?? [])
                .Where(source => source is not null)
                .Select(source => NewSharedSessionSourceRow(
                    // An identifier the provider would refuse is replaced once, here, so the save
                    // that follows writes one it accepts.
                    SharedSessionIds.IsValidSourceId(source.Id) ? source.Id : SharedSessionIds.NewSourceId(),
                    source.Name ?? "",
                    source.Path ?? "",
                    source.Enabled)));

        SubscribeSharedSessionSourceTracking();
        _sharedSessionSourcesValidationError = null;
        _clearSharedApprovalsOnSave = false;
        RevalidateSharedSessionSources();
        RefreshSharedSessionSourceStatuses();
    }

    private static SharedSessionSourceItemViewModel NewSharedSessionSourceRow(
        string id,
        string name,
        string path,
        bool enabled) =>
        new() { Id = id, Name = name, Path = path, Enabled = enabled };

    /// <summary>The sources Save writes: the valid rows, trimmed.</summary>
    /// <remarks>
    /// Reads the rows and changes nothing: Save calls it inside the settings write, which may run
    /// off the UI thread, so it decides validity itself rather than through the row errors.
    /// </remarks>
    private List<SharedSessionSourceDto> BuildSharedSessionSources()
    {
        List<SharedSessionSourceItemViewModel> rows = SharedSessionSources.ToList();
        List<(string? NameErrorKey, string? PathErrorKey)> errors = SharedSessionSourceErrorKeys(rows);
        List<SharedSessionSourceDto> sources = [];
        for (int index = 0; index < rows.Count; index++)
        {
            if (errors[index] is (null, null))
            {
                sources.Add(new SharedSessionSourceDto
                {
                    Id = rows[index].Id,
                    Name = rows[index].Name.Trim(),
                    Path = rows[index].Path.Trim(),
                    Enabled = rows[index].Enabled,
                });
            }
        }

        return sources;
    }

    /// <summary>
    /// The locale key of each row's name and path error, in row order; null where there is none.
    /// A name is required and must not repeat another row's name in any case; a path is required.
    /// </summary>
    internal static List<(string? NameErrorKey, string? PathErrorKey)> SharedSessionSourceErrorKeys(
        IReadOnlyList<SharedSessionSourceItemViewModel> rows)
    {
        Dictionary<string, int> nameCounts = rows
            .Select(row => row.Name.Trim())
            .Where(name => name.Length > 0)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return rows
            .Select(row =>
            {
                string name = row.Name.Trim();
                string? nameErrorKey = name.Length == 0
                    ? "SettingsSharedSourceNameRequired"
                    : nameCounts.TryGetValue(name, out int count) && count > 1
                        ? "SettingsSharedSourceNameDuplicate"
                        : null;
                string? pathErrorKey = string.IsNullOrWhiteSpace(row.Path)
                    ? "SettingsSharedSourcePathRequired"
                    : null;
                return (nameErrorKey, pathErrorKey);
            })
            .ToList();
    }

    /// <summary>Sets the localized error of every row from <see cref="SharedSessionSourceErrorKeys"/>.</summary>
    private void RevalidateSharedSessionSources()
    {
        List<SharedSessionSourceItemViewModel> rows = SharedSessionSources.ToList();
        List<(string? NameErrorKey, string? PathErrorKey)> errors = SharedSessionSourceErrorKeys(rows);
        for (int index = 0; index < rows.Count; index++)
        {
            rows[index].NameError = errors[index].NameErrorKey is { } nameKey ? _localizer[nameKey] : null;
            rows[index].PathError = errors[index].PathErrorKey is { } pathKey ? _localizer[pathKey] : null;
        }

        OnPropertyChanged(nameof(HasInvalidSharedSessionSources));
    }

    /// <summary>
    /// The banner text when a row is in error, or null; recorded for the summary that a refused
    /// save shows.
    /// </summary>
    private string? ValidateSharedSessionSources()
    {
        RevalidateSharedSessionSources();
        return HasInvalidSharedSessionSources
            ? _localizer["ValidationSharedSessionSourcesInvalid"]
            : null;
    }

    /// <summary>
    /// Writes each row's status from what the provider holds for its identifier: the number of
    /// sessions read, the reason it could not be read, or that it has not been read yet.
    /// </summary>
    internal void RefreshSharedSessionSourceStatuses()
    {
        Dictionary<string, SharedSessionSourceState> states = (_sharedSessions?.Sources ?? [])
            .GroupBy(state => state.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        foreach (SharedSessionSourceItemViewModel row in SharedSessionSources)
        {
            row.SavedPasswordCount = _sharedSecrets?.CountFor(row.Id) ?? 0;
            if (!states.TryGetValue(row.Id, out SharedSessionSourceState? state))
            {
                row.StatusDescription = _localizer["SettingsSharedSourceStatusPending"];
                row.HasStatusError = false;
                row.HasStatusWarning = false;
                row.IssueCount = 0;
                continue;
            }

            row.IssueCount = state.IssueCount;

            row.HasStatusWarning = state.Status == SharedSessionSourceStatus.Loaded && state.Notice is not null;

            (row.StatusDescription, row.HasStatusError) = state.Status switch
            {
                SharedSessionSourceStatus.Loaded => (
                    // The same notice as the tree's root: an offline copy says from when, a kept
                    // version says why the file was not taken.
                    _localizer.FormatCount(
                        state.ProfileCount,
                        "SettingsSharedSourceStatusLoadedOne",
                        "SettingsSharedSourceStatusLoaded",
                        state.ProfileCount)
                    + (state.Notice is null ? "" : System.Environment.NewLine + state.Notice),
                    false),
                SharedSessionSourceStatus.Error => (
                    string.IsNullOrWhiteSpace(state.ErrorMessage)
                        ? _localizer["SharedSessionSourceErrorUnknown"]
                        : state.ErrorMessage,
                    true),
                _ => (_localizer["SettingsSharedSourceStatusDisabled"], false),
            };
        }
    }

    private void SubscribeToSharedSessionProvider()
    {
        if (_sharedSessions is not null)
        {
            _sharedSessions.Changed += OnSharedSessionProviderChanged;
        }
    }

    private void UnsubscribeFromSharedSessionProvider()
    {
        if (_sharedSessions is not null)
        {
            _sharedSessions.Changed -= OnSharedSessionProviderChanged;
        }
    }

    /// <summary>
    /// A reload from elsewhere - the session list after a save, the startup load - updates the
    /// statuses on screen. Raised on whatever thread the load finished on.
    /// </summary>
    private void OnSharedSessionProviderChanged()
    {
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RefreshSharedSessionSourceStatuses();
            return;
        }

        _ = dispatcher.BeginInvoke(RefreshSharedSessionSourceStatuses);
    }

    private void SubscribeSharedSessionSourceTracking()
    {
        foreach (SharedSessionSourceItemViewModel row in SharedSessionSources)
        {
            row.PropertyChanged += OnSharedSessionSourcePropertyChanged;
        }

        SharedSessionSources.CollectionChanged += OnSharedSessionSourcesCollectionChanged;
    }

    private void UnsubscribeSharedSessionSourceTracking()
    {
        if (SharedSessionSources is null)
        {
            return;
        }

        foreach (SharedSessionSourceItemViewModel row in SharedSessionSources)
        {
            row.PropertyChanged -= OnSharedSessionSourcePropertyChanged;
        }

        SharedSessionSources.CollectionChanged -= OnSharedSessionSourcesCollectionChanged;
    }

    private void OnSharedSessionSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the fields the user edits are edits; the errors and the status this panel sets on
        // the row raise changes too, and reacting to those would loop.
        if (e.PropertyName is not (nameof(SharedSessionSourceItemViewModel.Name)
            or nameof(SharedSessionSourceItemViewModel.Path)
            or nameof(SharedSessionSourceItemViewModel.Enabled)))
        {
            return;
        }

        MarkEditTheComparisonCannotSee();
        OnSharedSessionSourcesEdited();
    }

    private void OnSharedSessionSourcesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SharedSessionSourceItemViewModel row in e.OldItems)
            {
                row.PropertyChanged -= OnSharedSessionSourcePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (SharedSessionSourceItemViewModel row in e.NewItems)
            {
                row.PropertyChanged += OnSharedSessionSourcePropertyChanged;
            }
        }

        OnSharedSessionSourcesEdited();
        RefreshSharedSessionSourceStatuses();
    }

    /// <summary>
    /// Revalidates every row, since one row's name decides whether another is a duplicate, and
    /// withdraws the banner a refused save raised once the list changes.
    /// </summary>
    private void OnSharedSessionSourcesEdited()
    {
        RevalidateSharedSessionSources();
        if (_sharedSessionSourcesValidationError is null)
        {
            return;
        }

        _sharedSessionSourcesValidationError = null;
        RefreshValidationSummary();
    }
}
