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

using System.Globalization;
using Heimdall.App.Services;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The root indicator of the pane: a badge that stays once the pane ran anything as root, and the
/// list of what it ran. It only displays what the <see cref="SftpRootActivityLog"/> holds.
/// </summary>
public sealed partial class EmbeddedSftpViewModel
{
    /// <summary>The standard long time pattern, for the time column of the list.</summary>
    private const string RootActTimeFormat = "T";

    private SftpPrivilegedActRecorder _privilegedActs;
    private int _rootPublishQueued;
    private LocalizationManager? _rootLocaleSource;
    private bool _hasRootActivity;
    private bool _hasRootOperations;
    private string _rootOperationCountText = string.Empty;
    private string _rootActivitySummary = string.Empty;
    private IReadOnlyList<SftpRootActivityEntry> _rootActivityEntries = [];

    /// <summary>What this pane ran as root, kept until the pane is rebuilt.</summary>
    internal SftpRootActivityLog RootActivity { get; }

    /// <summary>Whether the pane ran anything as root, a directory listing included.</summary>
    public bool HasRootActivity
    {
        get => _hasRootActivity;
        private set => SetProperty(ref _hasRootActivity, value);
    }

    /// <summary>Whether the pane ran an operation as root; the number is hidden while it did not.</summary>
    public bool HasRootOperations
    {
        get => _hasRootOperations;
        private set => SetProperty(ref _hasRootOperations, value);
    }

    /// <summary>The number of operations the pane ran as root, listings not counted.</summary>
    public string RootOperationCountText
    {
        get => _rootOperationCountText;
        private set => SetProperty(ref _rootOperationCountText, value);
    }

    /// <summary>The sentence that says what the badge stands for, for its tooltip and its name.</summary>
    public string RootActivitySummary
    {
        get => _rootActivitySummary;
        private set => SetProperty(ref _rootActivitySummary, value);
    }

    /// <summary>The lines of the list the badge opens: the listings first, then the latest operations.</summary>
    public IReadOnlyList<SftpRootActivityEntry> RootActivityEntries
    {
        get => _rootActivityEntries;
        private set => SetProperty(ref _rootActivityEntries, value);
    }

    // Raised from whatever thread ran the act. The publication is coalesced: a burst of acts
    // (a recursive delete, a batch) posts one refresh, not one per act.
    private void OnRootActivityChanged()
    {
        if (_disposed || Interlocked.Exchange(ref _rootPublishQueued, 1) != 0)
        {
            return;
        }

        // The flag stays set until a publication ran. A dispatch that throws at once, or whose task
        // ends without running (cancelled at shutdown, faulted), must clear it, or every later act
        // would be taken for one already queued and the badge would stop moving.
        try
        {
            Task publication = RunOnUiAsync(PublishRootActivity);
            _ = publication.ContinueWith(
                static (ended, state) =>
                {
                    if (ended.IsCompletedSuccessfully)
                    {
                        return;
                    }

                    EmbeddedSftpViewModel owner = (EmbeddedSftpViewModel)state!;
                    Interlocked.Exchange(ref owner._rootPublishQueued, 0);
                    Core.Logging.FileLogger.Warn(
                        $"EmbeddedSftpViewModel: the root indicator could not be refreshed: {ended.Exception?.GetBaseException().Message}");
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _rootPublishQueued, 0);
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSftpViewModel: the root indicator could not be refreshed: {ex.Message}");
        }
    }

    private void OnRootActivityLocaleChanged(string locale)
    {
        _ = locale;
        if (_disposed || !HasRootActivity)
        {
            return;
        }

        _ = RunOnUiAsync(PublishRootActivity);
    }

    private void DetachRootActivity()
    {
        RootActivity.Changed -= OnRootActivityChanged;
        if (_rootLocaleSource is not null)
        {
            _rootLocaleSource.LocaleChanged -= OnRootActivityLocaleChanged;
            _rootLocaleSource = null;
        }
    }

    // On the UI thread. Reads one snapshot and publishes every property from it.
    private void PublishRootActivity()
    {
        Interlocked.Exchange(ref _rootPublishQueued, 0);
        if (_disposed)
        {
            return;
        }

        SftpRootActivitySnapshot snapshot = RootActivity.Snapshot();
        ObserveRootActivityLocale();

        HasRootActivity = snapshot.OperationCount > 0 || snapshot.ListingCount > 0;
        HasRootOperations = snapshot.OperationCount > 0;
        RootOperationCountText = snapshot.OperationCount.ToString(CultureInfo.CurrentCulture);
        RootActivitySummary = HasRootActivity ? BuildRootActivitySummary(snapshot) : string.Empty;
        RootActivityEntries = BuildRootActivityEntries(snapshot);
    }

    private void ObserveRootActivityLocale()
    {
        if (_localizer is null || ReferenceEquals(_rootLocaleSource, _localizer))
        {
            return;
        }

        if (_rootLocaleSource is not null)
        {
            _rootLocaleSource.LocaleChanged -= OnRootActivityLocaleChanged;
        }

        _rootLocaleSource = _localizer;
        _rootLocaleSource.LocaleChanged += OnRootActivityLocaleChanged;
    }

    private string BuildRootActivitySummary(SftpRootActivitySnapshot snapshot)
    {
        string counts = string.Empty;
        if (snapshot.OperationCount > 0)
        {
            counts = JoinRootSummaryPart(
                counts,
                LFC(
                    snapshot.OperationCount,
                    SftpRootActivityLocaleKeys.SummaryOperationsOne,
                    SftpRootActivityLocaleKeys.SummaryOperations,
                    snapshot.OperationCount));
        }

        if (snapshot.FailedOperationCount > 0)
        {
            counts = JoinRootSummaryPart(
                counts,
                LFC(
                    snapshot.FailedOperationCount,
                    SftpRootActivityLocaleKeys.SummaryFailedOne,
                    SftpRootActivityLocaleKeys.SummaryFailed,
                    snapshot.FailedOperationCount));
        }

        if (snapshot.ListingCount > 0)
        {
            counts = JoinRootSummaryPart(
                counts,
                LFC(
                    snapshot.ListingCount,
                    SftpRootActivityLocaleKeys.SummaryListingsOne,
                    SftpRootActivityLocaleKeys.SummaryListings,
                    snapshot.ListingCount));
        }

        // With no operation the number is not shown, so the sentence about it is left out.
        string key = snapshot.OperationCount > 0
            ? SftpRootActivityLocaleKeys.Summary
            : SftpRootActivityLocaleKeys.SummaryListingsOnly;
        return LF(key, counts);
    }

    private string JoinRootSummaryPart(string joined, string part)
        => joined.Length == 0 ? part : LF(SftpRootActivityLocaleKeys.SummaryJoin, joined, part);

    private IReadOnlyList<SftpRootActivityEntry> BuildRootActivityEntries(SftpRootActivitySnapshot snapshot)
    {
        List<SftpRootActivityEntry> entries = [];

        if (snapshot.LastListing is { } lastListing)
        {
            string line = LFC(
                snapshot.ListingCount,
                SftpRootActivityLocaleKeys.ListingLineOne,
                SftpRootActivityLocaleKeys.ListingLine,
                snapshot.ListingCount,
                lastListing.Path);
            entries.Add(new SftpRootActivityEntry(
                line,
                FormatRootActTime(lastListing),
                line,
                IsSummary: true));
        }

        foreach (SftpRootAct act in snapshot.RecentOperations)
        {
            string what = LF(
                SftpRootActivityLocaleKeys.ForKind(act.Kind),
                act.Path,
                act.TargetPath ?? string.Empty);
            string outcome = L10n(SftpRootActivityLocaleKeys.ForOutcome(act.Outcome));
            string time = FormatRootActTime(act);
            entries.Add(new SftpRootActivityEntry(
                LF(SftpRootActivityLocaleKeys.ActLine, what, outcome),
                time,
                LF(SftpRootActivityLocaleKeys.ActRowName, what, outcome, time),
                IsSummary: false));
        }

        return entries;
    }

    private static string FormatRootActTime(SftpRootAct act)
        => act.Timestamp.ToString(RootActTimeFormat, CultureInfo.CurrentCulture);
}
