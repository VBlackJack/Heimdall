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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.ViewModels.Settings;

/// <summary>
/// Settings sub-panel listing every durable RDP certificate trust decision, and revoking one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The screen is the missing half of the store.</b> <see cref="RdpCertificateTrustStore"/>
/// has been able to forget since it was written, and nothing asked it to: a user who approved a
/// certificate by reflex, or for a machine that has since been rebuilt, had no way back other
/// than hand-editing settings.json. The SSH host keys panel is the same screen for a different
/// protocol, and this one deliberately mirrors it.
/// </para>
/// <para>
/// <b>The store keys by profile identifier, and an identifier tells a user nothing</b>, so each
/// row resolves its own name from the server inventory. The fallback is not cosmetic: a trust
/// decision that outlives the profile which made it is exactly the entry that needs cleaning
/// up, so it is shown under its raw identifier and flagged rather than dropped or blanked.
/// </para>
/// <para>
/// <b>Persisting the removal is not this class's job, and must still be proven.</b>
/// <see cref="RdpCertificateTrustStore.Remove"/> raises
/// <see cref="RdpCertificateTrustStore.TrustChanged"/> with the set as it now stands, and the
/// application's startup wiring writes that set back through
/// <c>App.PersistTrustedRdpCertificatesAsync</c> - the same path a new approval takes. A screen
/// that forgot only until the next launch would look identical from here, which is why the
/// suite asserts the reload from disk rather than a call on a double.
/// </para>
/// </remarks>
public sealed partial class TrustedRdpCertificatesSettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Visual severity passed to the confirmation: forgetting is destructive.</summary>
    private const string ConfirmSeverityWarning = "warning";

    /// <summary>Characters of a thumbprint shown in the grid before it is elided.</summary>
    private const int ThumbprintDisplayLength = 20;

    private readonly RdpCertificateTrustStore _store;
    private readonly Func<Task<IReadOnlyList<ServerProfileDto>>> _loadProfiles;
    private readonly LocalizationManager _localizer;
    private readonly IDialogService _dialogService;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<TrustedRdpCertificateRowViewModel> _allRows = [];
    private Dictionary<string, string> _profileNames = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the name map came from an inventory that could actually be read, and is therefore
    /// evidence about which profiles still exist.
    /// </summary>
    /// <remarks>
    /// An identifier the map does not name means "this profile was deleted" only when the map is
    /// a reading of the inventory. When the inventory could not be read the map says nothing
    /// about anything, and the row must not turn that silence into a deletion.
    /// </remarks>
    private bool _profileNamesAreEvidence;
    private bool _disposed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private TrustedRdpCertificateRowViewModel? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    /// <summary>Initializes a new instance for the running application.</summary>
    /// <param name="store">The trust store this screen reads and revokes from.</param>
    /// <param name="configManager">Source of the server inventory, for the profile names.</param>
    /// <param name="localizer">Resolves every string this screen shows.</param>
    /// <param name="dialogService">Asks the user before anything is forgotten.</param>
    /// <param name="dispatcher">Marshals store notifications onto the UI thread.</param>
    public TrustedRdpCertificatesSettingsViewModel(
        RdpCertificateTrustStore store,
        IConfigManager configManager,
        LocalizationManager localizer,
        IDialogService dialogService,
        IUiDispatcher dispatcher)
        : this(
            store,
            async () => (IReadOnlyList<ServerProfileDto>)await configManager.LoadServersAsync()
                .ConfigureAwait(false),
            localizer,
            dialogService,
            dispatcher)
    {
    }

    /// <summary>Initializes a new instance over an arbitrary inventory source.</summary>
    /// <remarks>
    /// The inventory is a delegate so a test can supply one without a configuration file, and
    /// can make it fail - the degradation path, where the names are lost but the trust
    /// decisions are still listed, is the one that matters and it cannot be reached otherwise.
    /// </remarks>
    internal TrustedRdpCertificatesSettingsViewModel(
        RdpCertificateTrustStore store,
        Func<Task<IReadOnlyList<ServerProfileDto>>> loadProfiles,
        LocalizationManager localizer,
        IDialogService dialogService,
        IUiDispatcher dispatcher)
    {
        _store = store;
        _loadProfiles = loadProfiles;
        _localizer = localizer;
        _dialogService = dialogService;
        _dispatcher = dispatcher;

        _store.TrustChanged += OnTrustChanged;
        _localizer.LocaleChanged += OnLocaleChanged;
    }

    /// <summary>The rows the grid shows, after the search box has been applied.</summary>
    public ObservableCollection<TrustedRdpCertificateRowViewModel> Rows { get; } = [];

    /// <summary>Whether any certificate is trusted at all, search box ignored.</summary>
    public bool HasRows => _allRows.Count > 0;

    /// <summary>Whether any row survives the search box.</summary>
    public bool HasVisibleRows => Rows.Count > 0;

    /// <summary>
    /// Whether the "nothing is trusted" panel replaces the grid.
    /// </summary>
    /// <remarks>
    /// Derived from the unfiltered set on purpose. A search that matches nothing leaves an
    /// empty grid, not an empty state: the empty state tells the user to go and connect
    /// somewhere, which is the wrong advice while rows are sitting behind a filter.
    /// </remarks>
    public bool IsEmptyStateVisible => !HasRows;

    /// <summary>Whether the status line under the grid has anything to say.</summary>
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Re-reads the server inventory and rebuilds every row from the store.</summary>
    /// <remarks>
    /// Asynchronous because the inventory lives in a file. A failure to read it degrades the
    /// names to identifiers and says so, rather than leaving the screen blank: hiding a trust
    /// decision is worse than showing it under an unfriendly name.
    /// </remarks>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        (Dictionary<string, string>? names, string failure) =
            await TryLoadProfileNamesAsync().ConfigureAwait(true);

        // An explicit refresh that cannot read the inventory drops the names it was holding and
        // says so. The names are what the user asked to have re-read; keeping the old ones and
        // staying quiet would answer a question nobody asked.
        _profileNames = names ?? new Dictionary<string, string>(StringComparer.Ordinal);

        // Dropping the names is not the same as learning that every profile is gone. The empty
        // map below is what the failure left behind, not a reading of the inventory, so the rows
        // it builds must say only what the status line says: the names could not be read.
        _profileNamesAreEvidence = names is not null;
        RebuildRows();
        StatusMessage = failure;
    }

    /// <summary>Reads the server inventory into a fresh identifier-to-name map.</summary>
    /// <returns>
    /// The map and an empty string, or <see langword="null"/> and the text to show the user.
    /// </returns>
    private async Task<(Dictionary<string, string>? Names, string Failure)> TryLoadProfileNamesAsync()
    {
        try
        {
            IReadOnlyList<ServerProfileDto> profiles =
                await _loadProfiles().ConfigureAwait(true) ?? [];

            Dictionary<string, string> names = new(StringComparer.Ordinal);
            foreach (ServerProfileDto profile in profiles)
            {
                if (string.IsNullOrWhiteSpace(profile?.Id))
                {
                    continue;
                }

                names[profile.Id] = string.IsNullOrWhiteSpace(profile.DisplayName)
                    ? profile.RemoteServer
                    : profile.DisplayName;
            }

            return (names, string.Empty);
        }
        catch (Exception ex)
        {
            // Deliberately unfiltered. The inventory reaches this class through a delegate, so
            // the exception types are the caller's, not this screen's, to enumerate; and every
            // one of them has the same correct answer - keep the list, lose the names.
            FileLogger.Warn(
                $"Trusted RDP certificates: the server inventory could not be read: {ex.Message}");
            return (null, _localizer.Format("ToastTrustedRdpCertificatesLoadFailed", ex.Message));
        }
    }

    /// <summary>Revokes one certificate, once the user has confirmed it.</summary>
    /// <param name="row">The row to forget; ignored when null.</param>
    /// <remarks>
    /// <para>The confirmation is not a formality. Forgetting is destructive and it is silent: the
    /// row leaves the grid and nothing else on the screen changes, so a mis-click here costs the
    /// user a trust decision they will not see undone.</para>
    /// <para><b>What it does not promise is the next question</b>, and the confirmation copy must
    /// not promise it either. Revoking removes this certificate from this profile's trust set,
    /// full stop. Whether the next connection asks again depends on which certificate the endpoint
    /// presents - <see cref="Heimdall.Core.Certificates.RdpCertificateVerifier"/> evaluates the
    /// store for the thumbprint it actually probed, so a sibling certificate still trusted for the
    /// same profile makes the pre-flight silent - and on the pre-flight reaching the endpoint at
    /// all, which a gateway-routed profile does not. Anyone reworking this wording, or documenting
    /// this panel, starts from that sentence and not from an older one.</para>
    /// </remarks>
    [RelayCommand]
    private async Task ForgetAsync(TrustedRdpCertificateRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        bool confirmed;
        try
        {
            confirmed = await _dialogService.ShowConfirmAsync(
                _localizer["DialogTrustedRdpCertificateForgetTitle"],
                _localizer.Format(
                    "DialogTrustedRdpCertificateForgetMessage",
                    row.ProfileDisplay,
                    row.Thumbprint),
                ConfirmSeverityWarning,
                _localizer["DialogTrustedRdpCertificateForgetConfirm"],
                _localizer["DialogTrustedRdpCertificateForgetDecline"]).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A confirmation that could not be shown is not a yes. Reading a failure as consent
            // would revoke trust the user never agreed to revoke, and the dialog layer is
            // free to throw for reasons this screen has no list of - an absent owner window
            // among them - so the catch is unfiltered on purpose.
            FileLogger.Warn(
                $"Trusted RDP certificates: the confirmation could not be shown, so "
                + $"{row.Thumbprint} was kept: {ex.Message}");
            StatusMessage = _localizer.Format("ToastTrustedRdpCertificateForgetFailed", ex.Message);
            return;
        }

        if (!confirmed)
        {
            return;
        }

        // Remove raises TrustChanged, which the startup wiring turns into the settings write and
        // which this screen turns into the row disappearing. Nothing else has to be done here.
        if (_store.Remove(row.Key, row.Thumbprint))
        {
            StatusMessage = _localizer.Format(
                "ToastTrustedRdpCertificateForgotten",
                row.ProfileDisplay);
        }
    }

    /// <summary>Revokes the row the grid has selected, so Delete can be bound to it.</summary>
    [RelayCommand]
    private Task ForgetSelectedAsync() => ForgetAsync(SelectedRow);

    private void RebuildRows()
    {
        _allRows.Clear();
        foreach ((RdpTrustKey key, IReadOnlyCollection<RdpCertificateEntry> entries)
            in _store.GetAllApproved())
        {
            foreach (RdpCertificateEntry entry in entries)
            {
                _allRows.Add(CreateRow(key, entry));
            }
        }

        // Grouped by the server the user recognises, then newest decision first inside it. The
        // grid's own column sorting takes over from here; this is only the order it opens in.
        _allRows.Sort(static (left, right) =>
        {
            int byProfile = string.Compare(
                left.ProfileDisplay,
                right.ProfileDisplay,
                StringComparison.CurrentCultureIgnoreCase);
            return byProfile != 0 ? byProfile : right.FirstTrusted.CompareTo(left.FirstTrusted);
        });

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<TrustedRdpCertificateRowViewModel> visible = string.IsNullOrWhiteSpace(SearchText)
            ? _allRows
            : _allRows.Where(row => row.Matches(SearchText));

        Rows.Clear();
        foreach (TrustedRdpCertificateRowViewModel row in visible)
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasVisibleRows));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
    }

    private TrustedRdpCertificateRowViewModel CreateRow(RdpTrustKey key, RdpCertificateEntry entry)
    {
        if (key.Scope == RdpTrustScope.TypedDestination)
        {
            // A destination typed by hand is its host and nothing else: no profile owns it, so
            // the inventory is not consulted and the "profile deleted" badge can never apply.
            // The grid shows the host and a badge of its own saying what kind of owner this is.
            return new TrustedRdpCertificateRowViewModel(
                key,
                key.Identity,
                isProfileMissing: false,
                entry,
                Describe(entry.Subject),
                Describe(entry.Issuer),
                entry.FirstTrusted.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                ThumbprintDisplayLength);
        }

        string profileId = key.Identity;
        bool known = _profileNames.TryGetValue(profileId, out string? name)
            && !string.IsNullOrWhiteSpace(name);

        // "Not in the inventory" and "the inventory could not be read" both leave the row under
        // its raw identifier, and they are different facts: the first says the profile was
        // deleted and this trust decision outlived it, the second says nothing about the profile
        // at all. Only the first earns the deletion badge, so a user who corrupts or locks
        // servers.json is not told that every server they own has been deleted.
        bool missing = !known && _profileNamesAreEvidence;

        // The raw identifier is the whole fallback, with no localized parenthetical folded into
        // it: the grid puts a separate "profile deleted" badge beside it. Folding the two would
        // make the cell unsortable against the named rows, unsearchable by the identifier the
        // user reads in settings.json, and untranslatable once copied out.
        return new TrustedRdpCertificateRowViewModel(
            key,
            known ? name! : profileId,
            missing,
            entry,
            Describe(entry.Subject),
            Describe(entry.Issuer),
            entry.FirstTrusted.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            ThumbprintDisplayLength);
    }

    private string Describe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? _localizer["LblTrustedRdpCertificateFieldUnknown"]
            : value;

    /// <summary>Rebuilds the rows after a trust decision taken elsewhere in the application.</summary>
    /// <remarks>
    /// The inventory is re-read first, and that is the whole reason this goes through a method
    /// rather than straight to <c>RebuildRows</c>. The name map is filled when the settings
    /// panel loads, which for a running session means once at startup - adding a server does not
    /// go back through it. Rebuilding from that snapshot made the screen assert "profile
    /// deleted" over a machine the user had created and connected to minutes earlier: a false
    /// statement about the one entry on the screen that was beyond suspicion, pointing at the
    /// wrong row to revoke. The symmetric case, a profile deleted after the snapshot, kept its
    /// friendly name and lost its badge.
    /// </remarks>
    private void OnTrustChanged(RdpTrustKey key, IReadOnlyCollection<RdpCertificateEntry> entries)
        => _dispatcher.Invoke(() => _ = ReloadProfileNamesAndRebuildAsync());

    private async Task ReloadProfileNamesAndRebuildAsync()
    {
        (Dictionary<string, string>? names, _) =
            await TryLoadProfileNamesAsync().ConfigureAwait(true);

        // A failed read leaves the previous names standing rather than clearing them. They are
        // stale, but clearing would badge every row "profile deleted" at once, which is the same
        // false statement this path exists to stop. The status line is left alone too: this runs
        // off a store event, and the line it would overwrite is the confirmation the user is
        // reading for the removal that raised that event.
        if (names is not null)
        {
            _profileNames = names;
        }

        // Kept names are still shown, but a map that could not be refreshed has stopped being a
        // reading of the inventory: it can no longer be cited as proof that a profile is gone.
        _profileNamesAreEvidence = names is not null;
        RebuildRows();
    }

    private void OnLocaleChanged(string locale) => _dispatcher.Invoke(RebuildRows);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.TrustChanged -= OnTrustChanged;
        _localizer.LocaleChanged -= OnLocaleChanged;
    }
}

/// <summary>One trusted certificate, as the settings grid shows it.</summary>
/// <remarks>
/// Immutable and rebuilt rather than mutated: a locale change and a store change both replace
/// the whole set, and there is nothing on this row a user edits in place.
/// </remarks>
public sealed class TrustedRdpCertificateRowViewModel
{
    internal TrustedRdpCertificateRowViewModel(
        RdpTrustKey key,
        string profileDisplay,
        bool isProfileMissing,
        RdpCertificateEntry entry,
        string subjectDisplay,
        string issuerDisplay,
        string firstTrustedDisplay,
        int thumbprintDisplayLength)
    {
        Key = key;
        ProfileId = key.Identity;
        IsTypedDestination = key.Scope == RdpTrustScope.TypedDestination;
        ProfileDisplay = profileDisplay;
        IsProfileMissing = isProfileMissing;
        Entry = entry;
        Thumbprint = entry.Thumbprint;
        ThumbprintDisplay = Elide(entry.Thumbprint, thumbprintDisplayLength);
        Subject = entry.Subject;
        Issuer = entry.Issuer;
        SubjectDisplay = subjectDisplay;
        IssuerDisplay = issuerDisplay;
        FirstTrusted = entry.FirstTrusted;
        FirstTrustedDisplay = firstTrustedDisplay;
    }

    /// <summary>The owner the store keys this decision by, scope and identity together.</summary>
    /// <remarks>
    /// What a revocation must name. Two rows can show the same identity string under two
    /// scopes - a saved profile holding a quick-connect identifier, and the host typed by hand -
    /// and forgetting one must not touch the other.
    /// </remarks>
    public RdpTrustKey Key { get; }

    /// <summary>The identity half of <see cref="Key"/>: a profile identifier, or a host.</summary>
    public string ProfileId { get; }

    /// <summary>Whether this decision belongs to a destination typed by hand rather than a profile.</summary>
    public bool IsTypedDestination { get; }

    /// <summary>The server's name, or its identifier when the profile is gone.</summary>
    public string ProfileDisplay { get; }

    /// <summary>Whether the profile that approved this certificate no longer exists.</summary>
    public bool IsProfileMissing { get; }

    /// <summary>The stored entry, kept whole so a later detail view needs no new plumbing.</summary>
    public RdpCertificateEntry Entry { get; }

    /// <summary>The full thumbprint, which is what identifies the certificate.</summary>
    public string Thumbprint { get; }

    /// <summary>The thumbprint as the grid shows it, elided to fit its column.</summary>
    public string ThumbprintDisplay { get; }

    /// <summary>The subject as stored, null when no certificate was ever inspected.</summary>
    public string? Subject { get; }

    /// <summary>The issuer as stored, null when no certificate was ever inspected.</summary>
    public string? Issuer { get; }

    /// <summary>The subject, or the localized stand-in when it was never recorded.</summary>
    public string SubjectDisplay { get; }

    /// <summary>The issuer, or the localized stand-in when it was never recorded.</summary>
    public string IssuerDisplay { get; }

    /// <summary>When the user first approved this certificate.</summary>
    public DateTimeOffset FirstTrusted { get; }

    /// <summary>The approval date in the current culture's short form.</summary>
    public string FirstTrustedDisplay { get; }

    /// <summary>Whether the search box's text appears anywhere a user would look for it.</summary>
    /// <param name="text">The raw contents of the search box.</param>
    internal bool Matches(string text)
        => Contains(ProfileDisplay, text)
            || Contains(ProfileId, text)
            || Contains(Thumbprint, text)
            || Contains(SubjectDisplay, text)
            || Contains(IssuerDisplay, text);

    private static bool Contains(string? candidate, string text)
        => candidate is not null
            && candidate.Contains(text, StringComparison.CurrentCultureIgnoreCase);

    private static string Elide(string value, int length)
        => value.Length <= length ? value : value[..length] + "...";
}
