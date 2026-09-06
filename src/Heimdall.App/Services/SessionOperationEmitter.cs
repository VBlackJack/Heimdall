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

using System.Diagnostics;

namespace Heimdall.App.Services;

/// <summary>
/// Gated emitter for file-transfer operation records produced OUTSIDE the
/// <see cref="LoggingRemoteBrowser"/> decorator - specifically the SFTP sudo fallbacks, which bypass
/// <see cref="IRemoteBrowser"/> by running privileged commands over a raw SSH exec channel. Each
/// <c>RunXxxAsync</c> helper times the privileged body, awaits it, and emits exactly one
/// <see cref="SessionOperationRecord"/> (success / error / cancelled) when the live gate permits,
/// always rethrowing so the caller's error handling is preserved.
/// </summary>
/// <remarks>
/// This intentionally duplicates the small gate-check and run pattern of the P2 decorator rather than
/// refactoring it; the decorator stays untouched. Use <see cref="Disabled"/> as a no-op when no
/// operations log is wired: it runs the body and rethrows but never emits.
/// </remarks>
public sealed class SessionOperationEmitter
{
    private readonly ISessionOperationLog? _sink;
    private readonly Func<bool> _sessionLoggingEnabledProvider;
    private readonly string _protocol;
    private readonly string _host;
    private readonly bool? _sessionLoggingOverride;

    /// <summary>
    /// Creates an emitter that records sudo operations to <paramref name="sink"/> when the live gate
    /// permits.
    /// </summary>
    /// <param name="sink">The shared operations log sink (enqueue-only; never blocks).</param>
    /// <param name="sessionLoggingEnabledProvider">Reads the LIVE global session-logging toggle.</param>
    /// <param name="protocol">Transfer protocol label: "SFTP" or "FTP".</param>
    /// <param name="host">Target host (defensively stripped of any leading "user@").</param>
    public SessionOperationEmitter(
        ISessionOperationLog sink,
        Func<bool> sessionLoggingEnabledProvider,
        string protocol,
        string host,
        bool? sessionLoggingOverride = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(sessionLoggingEnabledProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        _sink = sink;
        _sessionLoggingEnabledProvider = sessionLoggingEnabledProvider;
        _protocol = protocol;
        _host = GraphicalSessionEventHelpers.ResolveHost(host, host);
        _sessionLoggingOverride = sessionLoggingOverride;
    }

    private SessionOperationEmitter()
    {
        // No-op instance: the null sink short-circuits the gate, so the operation still runs and
        // rethrows but nothing is ever recorded. Protocol/host are unused in this mode.
        _sink = null;
        _sessionLoggingEnabledProvider = static () => false;
        _protocol = string.Empty;
        _host = string.Empty;
        _sessionLoggingOverride = null;
    }

    /// <summary>A shared no-op emitter that runs operations but never records (logging not wired).</summary>
    public static SessionOperationEmitter Disabled { get; } = new();

    /// <summary>Runs a privileged mkdir, recording one record on completion.</summary>
    public Task RunMkdirAsync(string path, Func<Task> operation, bool privileged = false)
        => RunCoreAsync(
            operation,
            ms => SessionOperationRecord.Mkdir.Success(_protocol, _host, path, ms, privileged),
            ms => SessionOperationRecord.Mkdir.Cancelled(_protocol, _host, path, ms, privileged),
            (ms, category) => SessionOperationRecord.Mkdir.Error(_protocol, _host, path, ms, category, privileged));

    /// <summary>Runs a privileged delete, recording one record on completion.</summary>
    public Task RunDeleteAsync(string path, Func<Task> operation, bool privileged = false)
        => RunCoreAsync(
            operation,
            ms => SessionOperationRecord.Delete.Success(_protocol, _host, path, ms, privileged),
            ms => SessionOperationRecord.Delete.Cancelled(_protocol, _host, path, ms, privileged),
            (ms, category) => SessionOperationRecord.Delete.Error(_protocol, _host, path, ms, category, privileged));

    /// <summary>Runs a privileged chmod, recording one record on completion.</summary>
    public Task RunChmodAsync(string path, Func<Task> operation, bool privileged = false)
        => RunCoreAsync(
            operation,
            ms => SessionOperationRecord.Chmod.Success(_protocol, _host, path, ms, privileged),
            ms => SessionOperationRecord.Chmod.Cancelled(_protocol, _host, path, ms, privileged),
            (ms, category) => SessionOperationRecord.Chmod.Error(_protocol, _host, path, ms, category, privileged));

    /// <summary>Runs a privileged rename, recording one record on completion.</summary>
    public Task RunRenameAsync(string oldPath, string newPath, Func<Task> operation, bool privileged = false)
        => RunCoreAsync(
            operation,
            ms => SessionOperationRecord.Rename.Success(_protocol, _host, oldPath, newPath, ms, privileged),
            ms => SessionOperationRecord.Rename.Cancelled(_protocol, _host, oldPath, newPath, ms, privileged),
            (ms, category) => SessionOperationRecord.Rename.Error(_protocol, _host, oldPath, newPath, ms, category, privileged));

    /// <summary>
    /// Runs a privileged download, recording one record on completion. <paramref name="bytesOnSuccess"/>
    /// is evaluated only after the body succeeds (the committed local file then exists).
    /// </summary>
    public Task RunDownloadAsync(
        string remotePath, string localPath, Func<Task> operation, Func<long> bytesOnSuccess, bool privileged = false)
        => RunCoreAsync(
            operation,
            ms => SessionOperationRecord.Download.Success(_protocol, _host, remotePath, localPath, bytesOnSuccess(), ms, privileged),
            ms => SessionOperationRecord.Download.Cancelled(_protocol, _host, remotePath, localPath, ms, privileged),
            (ms, category) => SessionOperationRecord.Download.Error(_protocol, _host, remotePath, localPath, ms, category, privileged));

    /// <summary>
    /// Runs a privileged upload, recording one record on completion. <paramref name="bytesOnSuccess"/>
    /// is evaluated only after the body succeeds (the local source size).
    /// </summary>
    public Task RunUploadAsync(
        string localPath, string remotePath, Func<Task> operation, Func<long> bytesOnSuccess, bool privileged = false)
        => RunCoreAsync(
            operation,
            ms => SessionOperationRecord.Upload.Success(_protocol, _host, remotePath, localPath, bytesOnSuccess(), ms, privileged),
            ms => SessionOperationRecord.Upload.Cancelled(_protocol, _host, remotePath, localPath, ms, privileged),
            (ms, category) => SessionOperationRecord.Upload.Error(_protocol, _host, remotePath, localPath, ms, category, privileged));

    /// <summary>
    /// Records a privileged upload whose work has ALREADY completed elsewhere (e.g. the editor
    /// sudo-save signal, which runs over its own SSH clients). No operation is run and no duration is
    /// measured (<c>durationMs</c> is 0). On success the byte count is read via
    /// <paramref name="bytesOnSuccess"/> only when the gate permits; on failure the category is
    /// <see cref="OperationErrorClassifier.Other"/> because the signal carries only success/failure.
    /// </summary>
    public void EmitUploadCompleted(
        string localPath,
        string remotePath,
        bool success,
        Func<long> bytesOnSuccess,
        bool privileged = false)
    {
        ArgumentNullException.ThrowIfNull(bytesOnSuccess);

        if (!ShouldLog())
        {
            return;
        }

        SafeEmit(() => success
            ? SessionOperationRecord.Upload.Success(
                _protocol, _host, remotePath, localPath, bytesOnSuccess(), durationMs: 0, privileged)
            : SessionOperationRecord.Upload.Error(
                _protocol, _host, remotePath, localPath, durationMs: 0, OperationErrorClassifier.Other, privileged));
    }

    // Times the body, emits exactly one record when the gate permits, and always rethrows. The gate is
    // read once, live, at the start. When the gate is closed the body still runs (and rethrows) with no
    // timing or record, so disabling logging never alters control flow.
    private async Task RunCoreAsync(
        Func<Task> operation,
        Func<long, SessionOperationRecord> onSuccess,
        Func<long, SessionOperationRecord> onCancel,
        Func<long, string, SessionOperationRecord> onError)
    {
        if (!ShouldLog())
        {
            await operation().ConfigureAwait(false);
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            await operation().ConfigureAwait(false);
            stopwatch.Stop();
            SafeEmit(() => onSuccess(stopwatch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            SafeEmit(() => onCancel(stopwatch.ElapsedMilliseconds));
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            string category = OperationErrorClassifier.Classify(ex);
            SafeEmit(() => onError(stopwatch.ElapsedMilliseconds, category));
            throw;
        }
    }

    private bool ShouldLog()
    {
        bool enabled = SessionLoggingResolver.ResolveSessionLogging(
            _sessionLoggingOverride,
            _sessionLoggingEnabledProvider());
        return _sink is not null && SessionOperationGatePolicy.ShouldLog(enabled, _protocol);
    }

    // Builds and enqueues a record without ever throwing into the operation thread. Logging is
    // best-effort: a build or enqueue failure is swallowed with a single diagnostic rather than masking
    // the operation's own outcome.
    private void SafeEmit(Func<SessionOperationRecord> build)
    {
        try
        {
            _sink!.LogOperation(build());
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"SessionOperationEmitter: failed to record {_protocol} operation: {ex.Message}");
        }
    }
}
