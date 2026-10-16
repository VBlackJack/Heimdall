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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services.SharedSessions;

/// <summary>
/// Reads the shared MobaXterm session files of the settings, sanitizes what they hold and keeps
/// it in memory, read-only.
/// </summary>
/// <remarks>
/// <para>Each source is read with the decoding rule of Settings > Import
/// (<see cref="MobaXtermFileDecoder"/>), parsed with the identifier prefix of its own namespace
/// (<see cref="SharedSessionIds.PrefixFor"/>) and the user's variables, and passed through
/// <see cref="SharedProfileSanitizer"/>. A profile the sanitizer refuses, or whose gateway the
/// file does not define, is dropped and counted rather than loaded without its bastion.</para>
/// <para>The file is opened for reading only and with every sharing mode allowed, so MobaXterm and
/// the sync client can keep writing it while it is read. Nothing here ever opens it for writing.</para>
/// </remarks>
public sealed class SharedSessionProvider : ISharedSessionProvider
{
    /// <summary>A session file larger than this is not read: no real file comes close.</summary>
    internal const long MaxSourceFileBytes = 16L * 1024 * 1024;

    private const int ReadBufferSize = 81920;

    private readonly LocalizationManager _localizer;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public SharedSessionProvider(LocalizationManager localizer)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public IReadOnlyList<SharedSessionSourceState> Sources => _snapshot.Sources;

    /// <inheritdoc />
    public IReadOnlyList<ServerProfileDto> Profiles =>
        _snapshot.Profiles.Select(profile => profile.CloneFaithfully()).ToList();

    /// <inheritdoc />
    public IReadOnlyList<SshGatewayDto> Gateways =>
        _snapshot.Gateways.Select(gateway => gateway.CloneFaithfully()).ToList();

    /// <inheritdoc />
    public ServerProfileDto? FindProfile(string profileId)
    {
        return !string.IsNullOrEmpty(profileId)
            && _snapshot.ProfilesById.TryGetValue(profileId, out ServerProfileDto? profile)
                ? profile.CloneFaithfully()
                : null;
    }

    /// <inheritdoc />
    public async Task LoadAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Func<string, string?> resolver = MobaXtermVariableResolver.FromVariables(settings.MobaXtermUserVariables);
            List<SharedSessionSourceState> states = [];
            List<ServerProfileDto> profiles = [];
            List<SshGatewayDto> gateways = [];
            HashSet<string> seenSourceIds = new(StringComparer.Ordinal);

            foreach (SharedSessionSourceDto? source in settings.SharedSessionSources ?? [])
            {
                if (source is null)
                {
                    continue;
                }

                SourceLoad load = await LoadSourceAsync(source, seenSourceIds, resolver, cancellationToken)
                    .ConfigureAwait(false);
                states.Add(load.State);
                profiles.AddRange(load.Profiles);
                gateways.AddRange(load.Gateways);
            }

            _snapshot = new Snapshot(
                states,
                profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal),
                profiles,
                gateways);
        }
        finally
        {
            _loadGate.Release();
        }

        Changed?.Invoke();
    }

    private async Task<SourceLoad> LoadSourceAsync(
        SharedSessionSourceDto source,
        HashSet<string> seenSourceIds,
        Func<string, string?> resolver,
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

        byte[]? bytes;
        try
        {
            bytes = await ReadSourceFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Failed(source, name, path, _localizer.Format("SharedSessionSourceErrorMissing", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            return Failed(source, name, path, _localizer.Format("SharedSessionSourceErrorUnreadable", path, ex.Message));
        }

        if (bytes is null)
        {
            return Failed(source, name, path, _localizer.Format("SharedSessionSourceErrorTooLarge", path));
        }

        MobaXtermImportResult parsed = MobaXtermImporter.Parse(
            MobaXtermFileDecoder.Decode(bytes),
            new MobaXtermImportOptions
            {
                IdPrefix = SharedSessionIds.PrefixFor(source.Id),
                VariableResolver = resolver,
            });

        Dictionary<string, SshGatewayDto> gatewaysById = new(StringComparer.Ordinal);
        foreach (SshGatewayDto parsedGateway in parsed.Gateways)
        {
            if (SharedProfileSanitizer.Sanitize(parsedGateway) is { } gateway)
            {
                gatewaysById.TryAdd(gateway.Id, gateway);
            }
        }

        List<ServerProfileDto> profiles = [];
        int dropped = 0;
        foreach (ServerProfileDto parsedProfile in parsed.Servers)
        {
            ServerProfileDto? profile = SharedProfileSanitizer.Sanitize(parsedProfile);
            if (profile is null
                || (SshGatewayRoute.IsAssigned(profile.SshGatewayId) && !gatewaysById.ContainsKey(profile.SshGatewayId!)))
            {
                dropped++;
                continue;
            }

            profiles.Add(profile);
        }

        FileLogger.Info(
            $"Shared sessions: source '{name}' ({source.Id}) loaded {profiles.Count} profile(s), "
            + $"{gatewaysById.Count} gateway(s), {parsed.Warnings.Count} warning(s), {dropped} dropped, from {path}.");

        return new SourceLoad(
            new SharedSessionSourceState(
                source.Id,
                name,
                path,
                SharedSessionSourceStatus.Loaded,
                null,
                profiles.Count,
                gatewaysById.Count,
                parsed.Warnings,
                dropped),
            profiles,
            [.. gatewaysById.Values]);
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
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private sealed record SourceLoad(
        SharedSessionSourceState State,
        IReadOnlyList<ServerProfileDto> Profiles,
        IReadOnlyList<SshGatewayDto> Gateways);

    private sealed record Snapshot(
        IReadOnlyList<SharedSessionSourceState> Sources,
        IReadOnlyDictionary<string, ServerProfileDto> ProfilesById,
        IReadOnlyList<ServerProfileDto> Profiles,
        IReadOnlyList<SshGatewayDto> Gateways)
    {
        public static Snapshot Empty { get; } = new([], new Dictionary<string, ServerProfileDto>(), [], []);
    }
}
