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
using System.IO;
using Heimdall.Sftp;

namespace Heimdall.App.Services;

/// <summary>
/// Transparent <see cref="IRemoteBrowser"/> decorator that records each of the six file-transfer
/// operations (upload / download / delete / rename / mkdir / copy) to the shared operations log. Every
/// call is forwarded verbatim to the inner browser, and every exception is rethrown so the existing
/// view-model error handling is preserved. When the session-logging gate is open, each operation emits
/// exactly one <see cref="SessionOperationRecord"/> (success / error / cancelled); when it is closed the
/// operation is still forwarded and no record is emitted at all. The gate silences the journal, never
/// the transfer. A copy emits a single Copy record because it is a
/// single high-level operation: for SFTP there is no download-and-upload roundtrip to fall back to any
/// more. That says nothing about whether the server was reached - a transport that refuses fail-closed
/// records its Copy without any server-side operation having taken place.
/// </summary>
/// <remarks>
/// <para>
/// This seam only sees operations that flow through <see cref="IRemoteBrowser"/> - the NON-sudo path.
/// The sudo fallbacks (view-model sudo mkdir/mv/rm/chmod, sudo base64 download, sudo upload to a temp
/// path, and the sudo editor save) bypass this interface entirely and are logged separately in Lot 3
/// Prompt 3; their absence here is by design, not a coverage gap.
/// </para>
/// <para>
/// The decorator never owns the inner browser's lifecycle: <see cref="Dispose"/> does not dispose the
/// inner browser (the view's teardown path disposes the raw browser exactly once). Events are
/// pass-through (subscribers attach directly to the inner browser), so there is nothing to unsubscribe.
/// </para>
/// </remarks>
public sealed class LoggingRemoteBrowser
    : IRemoteBrowser, IRemoteNoClobberCapability, IRemoteClipboardEndpointIdentity
{
    private readonly IRemoteBrowser _inner;
    private readonly ISessionOperationLog _sink;
    private readonly Func<bool> _sessionLoggingEnabledProvider;
    private readonly string _protocol;
    private readonly string _host;
    private readonly bool? _sessionLoggingOverride;

    /// <summary>
    /// Wraps <paramref name="inner"/> so its transfer operations are recorded to
    /// <paramref name="sink"/> when the live gate permits.
    /// </summary>
    /// <param name="inner">The real browser whose operations are forwarded and logged.</param>
    /// <param name="sink">The shared operations log sink (enqueue-only; never blocks).</param>
    /// <param name="sessionLoggingEnabledProvider">Reads the LIVE global session-logging toggle.</param>
    /// <param name="protocol">Transfer protocol label: "SFTP" or "FTP".</param>
    /// <param name="host">Target host (defensively stripped of any leading "user@").</param>
    public LoggingRemoteBrowser(
        IRemoteBrowser inner,
        ISessionOperationLog sink,
        Func<bool> sessionLoggingEnabledProvider,
        string protocol,
        string host,
        bool? sessionLoggingOverride = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(sessionLoggingEnabledProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        _inner = inner;
        _sink = sink;
        _sessionLoggingEnabledProvider = sessionLoggingEnabledProvider;
        _protocol = protocol;
        _host = GraphicalSessionEventHelpers.ResolveHost(host, host);
        _sessionLoggingOverride = sessionLoggingOverride;

        // Asked of the inner browser, never claimed on this decorator's own behalf. Claiming a
        // guarantee the inner cannot honour would hand the caller a publisher that cannot publish,
        // and the caller's refusal gate would then be deciding on a capability that does not exist.
        IRemoteNoClobberPublisher? innerPublisher =
            (inner as IRemoteNoClobberCapability)?.NoClobberPublisher;
        NoClobberPublisher = innerPublisher is null
            ? null
            : new LoggingNoClobberPublisher(this, innerPublisher);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Mirrors the inner browser's answer: <c>null</c> whenever the inner cannot publish without
    /// replacing, so a caller that refuses on a null capability refuses for the right reason.
    /// </remarks>
    public IRemoteNoClobberPublisher? NoClobberPublisher { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Resolved through the inner browser on every read rather than copied once at construction. A
    /// wrapper around a wrapper therefore still reaches the raw browser, and an identity that only
    /// becomes known after the session connects is not frozen as absent.
    /// </remarks>
    string? IRemoteClipboardEndpointIdentity.ClipboardEndpointKey => _inner switch
    {
        IRemoteClipboardEndpointIdentity inner => inner.ClipboardEndpointKey,
        FtpBrowser ftpBrowser => NullIfEmpty(RemoteClipboardEndpointKey.FromFtp(ftpBrowser)),
        _ => null,
    };

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <inheritdoc />
    public event Action<string>? DirectoryChanged
    {
        add => _inner.DirectoryChanged += value;
        remove => _inner.DirectoryChanged -= value;
    }

    /// <inheritdoc />
    public event Action<SftpTransferProgress>? TransferProgress
    {
        add => _inner.TransferProgress += value;
        remove => _inner.TransferProgress -= value;
    }

    /// <inheritdoc />
    public event Action<RemoteOperationWarning>? OperationWarningRaised
    {
        add => _inner.OperationWarningRaised += value;
        remove => _inner.OperationWarningRaised -= value;
    }

    /// <inheritdoc />
    public event Action<string?>? Disconnected
    {
        add => _inner.Disconnected += value;
        remove => _inner.Disconnected -= value;
    }

    /// <inheritdoc />
    public string CurrentDirectory => _inner.CurrentDirectory;

    /// <inheritdoc />
    public bool IsConnected => _inner.IsConnected;

    /// <inheritdoc />
    public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
        => _inner.ListDirectoryAsync(path, ct);

    /// <inheritdoc />
    public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
        => _inner.GetCurrentDirectoryAsync(ct);

    /// <inheritdoc />
    public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
        => _inner.ChangeDirectoryAsync(path, ct);

    /// <inheritdoc />
    public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
        => RunLoggedAsync(
            () => _inner.DownloadFileAsync(remotePath, localPath, ct),
            ms => SessionOperationRecord.Download.Success(_protocol, _host, remotePath, localPath, FileLength(localPath), ms),
            ms => SessionOperationRecord.Download.Cancelled(_protocol, _host, remotePath, localPath, ms),
            (ms, category) => SessionOperationRecord.Download.Error(_protocol, _host, remotePath, localPath, ms, category));

    /// <inheritdoc />
    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
        => RunLoggedAsync(
            () => _inner.UploadFileAsync(localPath, remotePath, ct),
            ms => SessionOperationRecord.Upload.Success(_protocol, _host, remotePath, localPath, FileLength(localPath), ms),
            ms => SessionOperationRecord.Upload.Cancelled(_protocol, _host, remotePath, localPath, ms),
            (ms, category) => SessionOperationRecord.Upload.Error(_protocol, _host, remotePath, localPath, ms, category));

    /// <inheritdoc />
    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        => RunLoggedAsync(
            () => _inner.CreateDirectoryAsync(path, ct),
            ms => SessionOperationRecord.Mkdir.Success(_protocol, _host, path, ms),
            ms => SessionOperationRecord.Mkdir.Cancelled(_protocol, _host, path, ms),
            (ms, category) => SessionOperationRecord.Mkdir.Error(_protocol, _host, path, ms, category));

    /// <inheritdoc />
    public Task DeleteAsync(string path, CancellationToken ct = default)
        => RunLoggedAsync(
            () => _inner.DeleteAsync(path, ct),
            ms => SessionOperationRecord.Delete.Success(_protocol, _host, path, ms),
            ms => SessionOperationRecord.Delete.Cancelled(_protocol, _host, path, ms),
            (ms, category) => SessionOperationRecord.Delete.Error(_protocol, _host, path, ms, category));

    /// <inheritdoc />
    public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
        => RunLoggedAsync(
            () => _inner.ChmodAsync(path, mode, ct),
            ms => SessionOperationRecord.Chmod.Success(_protocol, _host, path, ms),
            ms => SessionOperationRecord.Chmod.Cancelled(_protocol, _host, path, ms),
            (ms, category) => SessionOperationRecord.Chmod.Error(_protocol, _host, path, ms, category));

    /// <inheritdoc />
    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
        => RunLoggedAsync(
            () => _inner.RenameAsync(oldPath, newPath, ct),
            ms => SessionOperationRecord.Rename.Success(_protocol, _host, oldPath, newPath, ms),
            ms => SessionOperationRecord.Rename.Cancelled(_protocol, _host, oldPath, newPath, ms),
            (ms, category) => SessionOperationRecord.Rename.Error(_protocol, _host, oldPath, newPath, ms, category));

    /// <inheritdoc />
    public Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default)
        => RunLoggedAsync(
            () => _inner.CopyAsync(sourcePath, destinationPath, recursive, ct),
            ms => SessionOperationRecord.Copy.Success(_protocol, _host, sourcePath, destinationPath, ms),
            ms => SessionOperationRecord.Copy.Cancelled(_protocol, _host, sourcePath, destinationPath, ms),
            (ms, category) => SessionOperationRecord.Copy.Error(_protocol, _host, sourcePath, destinationPath, ms, category));

    /// <inheritdoc />
    public void Disconnect() => _inner.Disconnect();

    /// <inheritdoc />
    public void Dispose()
    {
        // Intentionally does NOT dispose the inner browser: the view's teardown path owns and disposes
        // the raw browser exactly once. Events are pass-through, so there is nothing to unsubscribe.
    }

    // Runs the inner operation under a stopwatch, emitting exactly one record when the gate permits and
    // always rethrowing so the caller's error handling is preserved. The gate is read once, live, at the
    // start of the operation.
    private async Task RunLoggedAsync(
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
        return SessionOperationGatePolicy.ShouldLog(enabled, _protocol);
    }

    // Builds and enqueues a record without ever throwing into the operation thread. Logging is
    // best-effort: a failure to build (e.g. a transient FileInfo read) or enqueue is swallowed with a
    // single diagnostic rather than masking the operation's own outcome.
    private void SafeEmit(Func<SessionOperationRecord> build)
    {
        try
        {
            _sink.LogOperation(build());
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"LoggingRemoteBrowser: failed to record {_protocol} operation: {ex.Message}");
        }
    }

    private static long FileLength(string localPath) => new FileInfo(localPath).Length;

    /// <summary>
    /// Records a no-clobber publication through the same single-record path as every other operation.
    /// </summary>
    /// <remarks>
    /// The logged remote path is the final destination the caller asked for. The staging path the inner
    /// browser reserves is never visible at this seam and must not be: a record naming the staging file
    /// would describe a name that no longer exists once the publication succeeded.
    /// <para>
    /// A refusal and an unconfirmed outcome both arrive as exceptions, so both are recorded as errors.
    /// Neither may ever be recorded as a success: an operator reading a success line would conclude the
    /// destination now holds this file, which is exactly what an unconfirmed publication cannot say.
    /// </para>
    /// </remarks>
    private sealed class LoggingNoClobberPublisher : IRemoteNoClobberPublisher
    {
        private readonly LoggingRemoteBrowser _owner;
        private readonly IRemoteNoClobberPublisher _inner;

        internal LoggingNoClobberPublisher(LoggingRemoteBrowser owner, IRemoteNoClobberPublisher inner)
        {
            _owner = owner;
            _inner = inner;
        }

        /// <inheritdoc />
        public Task PublishFileIfAbsentAsync(
            string localPath,
            string remotePath,
            CancellationToken ct = default)
            => _owner.RunLoggedAsync(
                () => _inner.PublishFileIfAbsentAsync(localPath, remotePath, ct),
                ms => SessionOperationRecord.Upload.Success(
                    _owner._protocol, _owner._host, remotePath, localPath, FileLength(localPath), ms),
                ms => SessionOperationRecord.Upload.Cancelled(
                    _owner._protocol, _owner._host, remotePath, localPath, ms),
                (ms, category) => SessionOperationRecord.Upload.Error(
                    _owner._protocol, _owner._host, remotePath, localPath, ms, category));

        /// <inheritdoc />
        public Task CreateDirectoryExclusiveAsync(string remotePath, CancellationToken ct = default)
            => _owner.RunLoggedAsync(
                () => _inner.CreateDirectoryExclusiveAsync(remotePath, ct),
                ms => SessionOperationRecord.Mkdir.Success(_owner._protocol, _owner._host, remotePath, ms),
                ms => SessionOperationRecord.Mkdir.Cancelled(_owner._protocol, _owner._host, remotePath, ms),
                (ms, category) => SessionOperationRecord.Mkdir.Error(
                    _owner._protocol, _owner._host, remotePath, ms, category));
    }
}
