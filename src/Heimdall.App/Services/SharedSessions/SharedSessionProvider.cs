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
using System.IO;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services.SharedSessions;

/// <summary>
/// Reads the shared MobaXterm session files of the settings, sanitizes what they hold and keeps
/// it in memory, read-only; reads a file again when it changes, and falls back on its offline copy
/// when it cannot be read.
/// </summary>
/// <remarks>
/// <para>Each source is read with the decoding rule of Settings > Import
/// (<see cref="MobaXtermFileDecoder"/>), parsed with the identifier prefix of its own namespace
/// (<see cref="SharedSessionIds.PrefixFor"/>) and the user's variables, and passed through
/// <see cref="SharedProfileSanitizer"/>. A profile the sanitizer refuses, or whose gateway the
/// file does not define, is dropped and counted rather than loaded without its bastion.</para>
/// <para>The file is opened for reading only and with every sharing mode allowed, so MobaXterm and
/// the sync client can keep writing it while it is read. Nothing here ever opens it for writing.</para>
/// <para>A source that worked is never replaced by a worse one. When the file cannot be read, does
/// not parse, or suddenly holds no session at all, the sessions loaded last stay, with a notice
/// saying why; with none in memory, the offline copy of the last good file is read instead, through
/// the same parser and sanitizer as the file itself.</para>
/// </remarks>
public sealed class SharedSessionProvider : ISharedSessionProvider, IDisposable
{
    /// <summary>A session file larger than this is not read: no real file comes close.</summary>
    internal const long MaxSourceFileBytes = 16L * 1024 * 1024;

    private const int ReadBufferSize = 81920;

    private readonly LocalizationManager _localizer;
    private readonly SharedSessionSourceCache? _cache;
    private readonly SharedSessionSecretStore? _secrets;
    private readonly SharedPostConnectTrust? _approvals;
    private readonly TimeProvider _timeProvider;
    private readonly SharedSessionSourceWatcher? _watcher;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Dictionary<string, SourceLoad> _lastGood = new(StringComparer.Ordinal);
    private volatile Snapshot _snapshot = Snapshot.Empty;

    /// <summary>
    /// The reload debounce, in ticks, of the settings last handed to <see cref="LoadAsync"/> or
    /// <see cref="ApplyReloadDebounce"/>. Recorded on the caller's thread, in the order of the
    /// calls, so a load that only gets to its watches later does not bring back an older value.
    /// </summary>
    private long _reloadDebounceTicks;
    private AppSettings? _settings;
    private bool _disposed;

    /// <param name="localizer">Words the reasons a source shows.</param>
    /// <param name="cache">Where the offline copies are kept; null keeps none.</param>
    /// <param name="timeProvider">The clock of the reload debounce and of the retries.</param>
    /// <param name="watchSourceFiles">Whether the files are watched and read again when they change.</param>
    /// <param name="secrets">
    /// The passwords remembered for shared sessions, forgotten with their source; null keeps none.
    /// </param>
    /// <param name="approvals">
    /// The post-connect approvals of shared sessions, forgotten with their source; null forgets none.
    /// </param>
    public SharedSessionProvider(
        LocalizationManager localizer,
        SharedSessionSourceCache? cache = null,
        TimeProvider? timeProvider = null,
        bool watchSourceFiles = false,
        SharedSessionSecretStore? secrets = null,
        SharedPostConnectTrust? approvals = null)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _cache = cache;
        _secrets = secrets;
        _approvals = approvals;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (watchSourceFiles)
        {
            _watcher = new SharedSessionSourceWatcher(sourceId => ReloadSourceAsync(sourceId), _timeProvider);
        }
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public event Action<SharedSessionSourceReload>? SourceReloaded;

    /// <inheritdoc />
    public IReadOnlyList<SharedSessionSourceState> Sources =>
        [.. _snapshot.Loads.Select(load => load.State)];

    /// <inheritdoc />
    public IReadOnlyList<ServerProfileDto> Profiles =>
        _snapshot.Profiles.Select(profile => profile.CloneFaithfully()).ToList();

    /// <inheritdoc />
    public IReadOnlyList<SshGatewayDto> Gateways =>
        _snapshot.Gateways.Select(gateway => gateway.CloneFaithfully()).ToList();

    /// <summary>The watcher of the source files, when they are watched.</summary>
    internal SharedSessionSourceWatcher? Watcher => _watcher;

    /// <inheritdoc />
    public ServerProfileDto? FindProfile(string profileId)
    {
        return !string.IsNullOrEmpty(profileId)
            && _snapshot.ProfilesById.TryGetValue(profileId, out ServerProfileDto? profile)
                ? profile.CloneFaithfully()
                : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs on the thread pool whatever thread calls it. Checking that a file exists and opening it
    /// are synchronous calls, and on a network share that cannot be reached they wait out the SMB
    /// timeout: called from the interface at startup, before any real await, they froze the window.
    /// </remarks>
    public Task LoadAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Interlocked.Exchange(ref _reloadDebounceTicks, ReloadDebounceOf(settings).Ticks);
        return Task.Run(() => LoadCoreAsync(settings, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public void ApplyReloadDebounce(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        TimeSpan debounce = ReloadDebounceOf(settings);
        Interlocked.Exchange(ref _reloadDebounceTicks, debounce.Ticks);
        _watcher?.SetDebounce(debounce);
    }

    private static TimeSpan ReloadDebounceOf(AppSettings settings) =>
        TimeSpan.FromMilliseconds(
            SettingRanges.Of(nameof(AppSettings.SharedSourceReloadDebounceMs)).Clamp(settings.SharedSourceReloadDebounceMs));

    /// <summary>Reads a source file; replaced by tests to observe where and how it is read.</summary>
    internal Func<string, CancellationToken, Task<byte[]?>> SourceFileReader { get; set; } = ReadSourceFileAsync;

    private async Task LoadCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _settings = settings;
            Func<string, string?> resolver = MobaXtermVariableResolver.FromVariables(settings.MobaXtermUserVariables);
            List<SourceLoad> loads = [];
            HashSet<string> seenSourceIds = new(StringComparer.Ordinal);

            foreach (SharedSessionSourceDto? source in settings.SharedSessionSources ?? [])
            {
                if (source is null)
                {
                    continue;
                }

                loads.Add(await LoadSourceAsync(source, seenSourceIds, resolver, settings, isReload: false, cancellationToken)
                    .ConfigureAwait(false));
            }

            _snapshot = Snapshot.From(loads);
            await ForgetRemovedSourcesAsync(settings).ConfigureAwait(false);
            UpdateWatches(loads);
        }
        finally
        {
            _loadGate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Reads the file of <paramref name="sourceId"/> again and replaces that source alone; the
    /// other sources are left as they are. Called by the watcher once a changed file is quiet.
    /// </summary>
    /// <returns>The state the source is in afterwards, or null when no such source is loaded.</returns>
    public async Task<SharedSessionSourceState?> ReloadSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        SharedSessionSourceState? state = null;
        int previousIssueCount = 0;
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings? settings = _settings;
            SharedSessionSourceDto? source = settings?.SharedSessionSources?
                .FirstOrDefault(candidate => candidate is not null && string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
            if (_disposed || settings is null || source is null || !source.Enabled)
            {
                return null;
            }

            List<SourceLoad> loads = [.. _snapshot.Loads];
            int index = loads.FindIndex(load => string.Equals(load.State.SourceId, sourceId, StringComparison.Ordinal));
            if (index < 0)
            {
                return null;
            }

            previousIssueCount = loads[index].State.IssueCount;
            HashSet<string> seenSourceIds = [.. loads.Where((_, position) => position != index).Select(load => load.State.SourceId)];
            SourceLoad reloaded = await LoadSourceAsync(
                    source,
                    seenSourceIds,
                    MobaXtermVariableResolver.FromVariables(settings.MobaXtermUserVariables),
                    settings,
                    isReload: true,
                    cancellationToken)
                .ConfigureAwait(false);
            loads[index] = reloaded;
            _snapshot = Snapshot.From(loads);
            state = reloaded.State;
        }
        finally
        {
            _loadGate.Release();
        }

        Changed?.Invoke();
        if (state is not null)
        {
            SourceReloaded?.Invoke(new SharedSessionSourceReload(state, previousIssueCount));
        }

        return state;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
    }

    private async Task<SourceLoad> LoadSourceAsync(
        SharedSessionSourceDto source,
        HashSet<string> seenSourceIds,
        Func<string, string?> resolver,
        AppSettings settings,
        bool isReload,
        CancellationToken cancellationToken)
    {
        string name = source.Name ?? string.Empty;
        string path = Environment.ExpandEnvironmentVariables(source.Path ?? string.Empty).Trim();

        if (!SharedSessionIds.IsValidSourceId(source.Id))
        {
            return Failed(source, name, path, _localizer.Format("SharedSessionSourceErrorInvalidId", source.Id ?? string.Empty));
        }

        if (!seenSourceIds.Add(source.Id))
        {
            return Failed(source, name, path, _localizer.Format("SharedSessionSourceErrorDuplicateId", source.Id));
        }

        if (!source.Enabled)
        {
            return new SourceLoad(
                new SharedSessionSourceState(source.Id, name, path, SharedSessionSourceStatus.Disabled, null, 0, 0, [], 0),
                [],
                []);
        }

        if (path.Length == 0)
        {
            return Failed(source, name, path, _localizer["SharedSessionSourceErrorNoPath"]);
        }

        // The last good load of this very file: what a failed read or a worse file falls back on.
        SourceLoad? previous = _lastGood.TryGetValue(source.Id, out SourceLoad? kept)
            && string.Equals(kept.State.ExpandedPath, path, StringComparison.OrdinalIgnoreCase)
                ? kept with { State = kept.State with { Name = name } }
                : null;

        int attempts = isReload
            ? 1 + SettingRanges.Of(nameof(AppSettings.SharedSourceReloadRetryCount)).Clamp(settings.SharedSourceReloadRetryCount)
            : 1;
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(
            SettingRanges.Of(nameof(AppSettings.SharedSourceReloadRetryDelayMs)).Clamp(settings.SharedSourceReloadRetryDelayMs));

        SharedSessionImportReportBuilder.Options reportOptions = SharedSessionImportReportBuilder.Options.FromSettings(settings);
        ReadOutcome read = await ReadWithRetriesAsync(path, attempts, retryDelay, cancellationToken).ConfigureAwait(false);
        string reason;
        if (read.Content is { } content)
        {
            Parsed? parsed = TryParse(source.Id, name, content, resolver, reportOptions, out string? parseError);
            if (parsed is not null
                && (parsed.Profiles.Count > 0 || previous is null || previous.Profiles.Count == 0))
            {
                SourceLoad loaded = new(
                    new SharedSessionSourceState(
                        source.Id,
                        name,
                        path,
                        SharedSessionSourceStatus.Loaded,
                        null,
                        parsed.Profiles.Count,
                        parsed.Gateways.Count,
                        parsed.Warnings,
                        parsed.Dropped,
                        Report: parsed.Report),
                    parsed.Profiles,
                    parsed.Gateways);
                _lastGood[source.Id] = loaded;
                FileLogger.Info(
                    $"Shared sessions: source '{name}' ({source.Id}) loaded {parsed.Profiles.Count} profile(s), "
                    + $"{parsed.Gateways.Count} gateway(s), {parsed.Warnings.Count} warning(s), {parsed.Dropped} dropped, from {path}.");

                if (_cache is not null)
                {
                    await _cache.StoreAsync(source.Id, path, content, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken)
                        .ConfigureAwait(false);
                }

                return loaded;
            }

            reason = parsed is null
                ? _localizer.Format("SharedSessionSourceNoticeUnparsable", parseError ?? string.Empty)
                : _localizer["SharedSessionSourceNoticeEmpty"];
        }
        else
        {
            reason = DescribeReadFailure(read.Failure, path);
        }

        if (previous is not null && previous.Profiles.Count > 0)
        {
            FileLogger.Warn($"Shared sessions: source '{name}' ({source.Id}) keeps its previous sessions: {reason}");
            return previous with
            {
                State = previous.State with
                {
                    Notice = previous.State.IsOfflineCopy
                        ? OfflineNotice(previous.State.OfflineCopyFromUtc!.Value, reason)
                        : _localizer.Format("SharedSessionSourceNoticeKeptPrevious", reason),
                },
            };
        }

        if (await LoadOfflineCopyAsync(source, name, path, resolver, reportOptions, reason, cancellationToken).ConfigureAwait(false) is { } offline)
        {
            _lastGood[source.Id] = offline;
            return offline;
        }

        return Failed(source, name, path, reason);
    }

    /// <summary>
    /// The sessions of the offline copy of <paramref name="source"/>, parsed and sanitized like a
    /// file read from the share, or null when there is no usable copy.
    /// </summary>
    private async Task<SourceLoad?> LoadOfflineCopyAsync(
        SharedSessionSourceDto source,
        string name,
        string path,
        Func<string, string?> resolver,
        SharedSessionImportReportBuilder.Options reportOptions,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_cache is null
            || await _cache.TryReadAsync(source.Id, cancellationToken).ConfigureAwait(false) is not { } copy)
        {
            return null;
        }

        if (!string.Equals(copy.OriginalPath, path, StringComparison.OrdinalIgnoreCase))
        {
            FileLogger.Warn(
                $"Shared sessions: the offline copy of source {source.Id} was taken from {copy.OriginalPath}, not {path}; ignored.");
            return null;
        }

        Parsed? parsed = TryParse(source.Id, name, copy.Content, resolver, reportOptions, out string? parseError);
        if (parsed is null || parsed.Profiles.Count == 0)
        {
            FileLogger.Warn($"Shared sessions: the offline copy of source {source.Id} holds no usable session {parseError}");
            return null;
        }

        FileLogger.Warn(
            $"Shared sessions: source '{name}' ({source.Id}) loaded {parsed.Profiles.Count} profile(s) from its offline copy "
            + $"of {copy.LoadedAtUtc:O}: {reason}");
        return new SourceLoad(
            new SharedSessionSourceState(
                source.Id,
                name,
                path,
                SharedSessionSourceStatus.Loaded,
                null,
                parsed.Profiles.Count,
                parsed.Gateways.Count,
                parsed.Warnings,
                parsed.Dropped,
                OfflineNotice(copy.LoadedAtUtc, reason),
                copy.LoadedAtUtc,
                parsed.Report),
            parsed.Profiles,
            parsed.Gateways);
    }

    private string OfflineNotice(DateTime takenAtUtc, string reason) =>
        _localizer.Format(
            "SharedSessionSourceNoticeOffline",
            takenAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            reason);

    /// <summary>
    /// Parses and sanitizes <paramref name="content"/> for <paramref name="sourceId"/>; null when
    /// the content cannot be decoded or parsed at all.
    /// </summary>
    private static Parsed? TryParse(
        string sourceId,
        string sourceName,
        byte[] content,
        Func<string, string?> resolver,
        SharedSessionImportReportBuilder.Options reportOptions,
        out string? error)
    {
        MobaXtermImportResult result;
        string text;
        try
        {
            text = MobaXtermFileDecoder.Decode(content);
            result = MobaXtermImporter.Parse(
                text,
                new MobaXtermImportOptions
                {
                    IdPrefix = SharedSessionIds.PrefixFor(sourceId),
                    VariableResolver = resolver,
                });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Half a file written by a sync client can fail in ways no list of types foresees; the
            // caller keeps the previous sessions whatever the cause.
            error = ex.Message;
            return null;
        }

        Dictionary<string, SshGatewayDto> gatewaysById = new(StringComparer.Ordinal);
        foreach (SshGatewayDto parsedGateway in result.Gateways)
        {
            if (SharedProfileSanitizer.Sanitize(parsedGateway) is { } gateway)
            {
                gatewaysById.TryAdd(gateway.Id, gateway);
            }
        }

        List<ServerProfileDto> profiles = [];
        List<SharedRefusedSession> refused = [];
        foreach (ServerProfileDto parsedProfile in result.Servers)
        {
            ServerProfileDto? profile = SharedProfileSanitizer.Sanitize(parsedProfile);
            if (profile is null)
            {
                SharedProfileRefusal reason = SharedProfileSanitizer.RefusalOf(parsedProfile) ?? SharedProfileRefusal.ForeignId;
                refused.Add(new SharedRefusedSession(parsedProfile, reason, RefusedValue(parsedProfile, reason)));
                continue;
            }

            if (SshGatewayRoute.IsAssigned(profile.SshGatewayId) && !gatewaysById.ContainsKey(profile.SshGatewayId!))
            {
                // The parser defines every gateway it references; one missing here was dropped by
                // the sanitizer for its host, which is what the report names.
                string gateway = result.Gateways
                    .FirstOrDefault(candidate => string.Equals(candidate.Id, profile.SshGatewayId, StringComparison.Ordinal))?.Host
                    ?? profile.SshGatewayId!;
                refused.Add(new SharedRefusedSession(parsedProfile, SharedProfileRefusal.UnknownGateway, gateway));
                continue;
            }

            profiles.Add(profile);
        }

        error = null;
        List<SshGatewayDto> gateways = [.. gatewaysById.Values];
        return new Parsed(
            profiles,
            gateways,
            result.Warnings,
            refused.Count,
            SharedSessionImportReportBuilder.Build(sourceId, sourceName, text, result.Warnings, profiles, gateways, refused, reportOptions));
    }

    /// <summary>The value of <paramref name="profile"/> the shared filter refused it for.</summary>
    private static string RefusedValue(ServerProfileDto profile, SharedProfileRefusal reason) => reason switch
    {
        SharedProfileRefusal.UnsupportedProtocol => profile.ConnectionType ?? string.Empty,
        SharedProfileRefusal.InvalidHost => profile.RemoteServer ?? string.Empty,
        SharedProfileRefusal.ForeignGateway => profile.SshGatewayId ?? string.Empty,
        SharedProfileRefusal.InvalidRdGateway => profile.RdpGateway ?? string.Empty,
        _ => profile.Id ?? string.Empty,
    };

    /// <summary>
    /// Reads the file up to <paramref name="attempts"/> times, waiting <paramref name="retryDelay"/>
    /// between two: a sync client holds a file it is writing, and deletes and recreates it.
    /// </summary>
    private async Task<ReadOutcome> ReadWithRetriesAsync(
        string path,
        int attempts,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(retryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                byte[]? content = await SourceFileReader(path, cancellationToken).ConfigureAwait(false);
                return content is null
                    ? new ReadOutcome(null, new FileTooLargeException())
                    : new ReadOutcome(content, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
            {
                failure = ex;
                if (attempt < attempts)
                {
                    FileLogger.Info($"Shared sessions: {path} could not be read (attempt {attempt} of {attempts}): {ex.Message}");
                }
            }
        }

        return new ReadOutcome(null, failure);
    }

    private string DescribeReadFailure(Exception? failure, string path) => failure switch
    {
        FileTooLargeException => _localizer.Format("SharedSessionSourceErrorTooLarge", path),
        FileNotFoundException or DirectoryNotFoundException => _localizer.Format("SharedSessionSourceErrorMissing", path),
        _ => _localizer.Format("SharedSessionSourceErrorUnreadable", path, failure?.Message ?? string.Empty),
    };

    /// <summary>
    /// Drops what is remembered of a source no longer in the settings, its offline copy, the
    /// passwords saved for it and the post-connect approvals of its sessions included.
    /// </summary>
    private async Task ForgetRemovedSourcesAsync(AppSettings settings)
    {
        HashSet<string> kept = (settings.SharedSessionSources ?? [])
            .Where(source => source is not null && SharedSessionIds.IsValidSourceId(source.Id))
            .Select(source => source.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string sourceId in _lastGood.Keys.Where(sourceId => !kept.Contains(sourceId)).ToList())
        {
            _lastGood.Remove(sourceId);
        }

        _cache?.DeleteAllExcept(kept);
        if (_secrets is not null)
        {
            await _secrets.ForgetAllExceptAsync(kept).ConfigureAwait(false);
        }

        if (_approvals is not null)
        {
            try
            {
                await _approvals.ForgetAllExceptAsync(kept).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The sessions are loaded already; an approval left behind is tried again at the next load.
                FileLogger.Warn($"Shared sessions: the post-connect approvals of removed sources could not be forgotten: {ex.Message}");
            }
        }
    }

    /// <summary>Watches the file of every enabled source that has one, and nothing else.</summary>
    private void UpdateWatches(IReadOnlyList<SourceLoad> loads)
    {
        if (_watcher is null)
        {
            return;
        }

        Dictionary<string, string> watched = loads
            .Where(load => load.State.Status != SharedSessionSourceStatus.Disabled
                && SharedSessionIds.IsValidSourceId(load.State.SourceId)
                && load.State.ExpandedPath.Length > 0)
            .GroupBy(load => load.State.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().State.ExpandedPath, StringComparer.Ordinal);
        _watcher.Update(watched, TimeSpan.FromTicks(Interlocked.Read(ref _reloadDebounceTicks)));
    }

    private static SourceLoad Failed(SharedSessionSourceDto source, string name, string path, string message)
    {
        FileLogger.Warn($"Shared sessions: source '{name}' ({source.Id}) not loaded: {message}");
        return new SourceLoad(
            new SharedSessionSourceState(
                source.Id ?? string.Empty,
                name,
                path,
                SharedSessionSourceStatus.Error,
                message,
                0,
                0,
                [],
                0),
            [],
            []);
    }

    /// <summary>
    /// Reads the file for reading only, letting every other process keep it open for writing.
    /// Null when the file is too large to be a session file.
    /// </summary>
    private static async Task<byte[]?> ReadSourceFileAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException(null, path);
        }

        if (info.Length > MaxSourceFileBytes)
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ReadBufferSize,
            useAsync: true);
        return await ReadBoundedAsync(stream, MaxSourceFileBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <paramref name="stream"/> to its end, or null as soon as it holds more than
    /// <paramref name="maxBytes"/>.
    /// </summary>
    /// <remarks>
    /// The length checked before opening is only what the file was then: the file stays open for
    /// writing to every other process, and one that keeps appending to it would otherwise be read
    /// into memory however far it grows.
    /// </remarks>
    internal static async Task<byte[]?> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[ReadBufferSize];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Marks a file refused for its size; never thrown.</summary>
    private sealed class FileTooLargeException : Exception
    {
    }

    private sealed record ReadOutcome(byte[]? Content, Exception? Failure);

    private sealed record Parsed(
        IReadOnlyList<ServerProfileDto> Profiles,
        IReadOnlyList<SshGatewayDto> Gateways,
        IReadOnlyList<MobaXtermImportWarning> Warnings,
        int Dropped,
        SharedSessionImportReport Report);

    private sealed record SourceLoad(
        SharedSessionSourceState State,
        IReadOnlyList<ServerProfileDto> Profiles,
        IReadOnlyList<SshGatewayDto> Gateways);

    private sealed record Snapshot(
        IReadOnlyList<SourceLoad> Loads,
        IReadOnlyDictionary<string, ServerProfileDto> ProfilesById,
        IReadOnlyList<ServerProfileDto> Profiles,
        IReadOnlyList<SshGatewayDto> Gateways)
    {
        public static Snapshot Empty { get; } = new([], new Dictionary<string, ServerProfileDto>(), [], []);

        public static Snapshot From(IReadOnlyList<SourceLoad> loads)
        {
            List<ServerProfileDto> profiles = [.. loads.SelectMany(load => load.Profiles)];
            return new Snapshot(
                loads,
                profiles
                    .GroupBy(profile => profile.Id, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal),
                profiles,
                [.. loads.SelectMany(load => load.Gateways)]);
        }
    }
}
