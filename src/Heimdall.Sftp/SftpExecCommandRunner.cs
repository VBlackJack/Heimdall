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

using System.Net.Sockets;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.Sftp;

internal interface ISftpExecCommandRunner
{
    Task<SftpExecResult> ExecuteAsync(string command, CancellationToken ct);
}

internal sealed record SftpExecResult(int ExitStatus, string StandardError);

/// <summary>
/// Executes SFTP helper commands directly over an already-authenticated companion
/// <see cref="SshClient"/> session (eliminating extra TCP/SSH handshakes and duplicate MFA
/// prompts), falling back transparently to <paramref name="fallback"/> if the shared client
/// is unavailable or disconnected.
/// </summary>
internal sealed class SharedSshClientExecCommandRunner : ISftpExecCommandRunner
{
    private readonly Func<SshClient?> _sharedClientAccessor;
    private readonly ISftpExecCommandRunner _fallback;

    public SharedSshClientExecCommandRunner(
        Func<SshClient?> sharedClientAccessor,
        ISftpExecCommandRunner fallback)
    {
        _sharedClientAccessor = sharedClientAccessor ?? throw new ArgumentNullException(nameof(sharedClientAccessor));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public async Task<SftpExecResult> ExecuteAsync(string command, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ct.ThrowIfCancellationRequested();

        SshClient? sharedClient;
        try
        {
            sharedClient = _sharedClientAccessor();
        }
        catch (ObjectDisposedException)
        {
            sharedClient = null;
        }

        if (sharedClient is null || !IsClientConnected(sharedClient))
        {
            return await _fallback.ExecuteAsync(command, ct).ConfigureAwait(false);
        }

        try
        {
            using SshCommand sshCommand = sharedClient.CreateCommand(command);
            using CancellationTokenRegistration cancelReg = ct.Register(static state =>
            {
                try { ((SshCommand)state!).CancelAsync(); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }, sshCommand);

            await sshCommand.ExecuteAsync(ct).ConfigureAwait(false);
            return new SftpExecResult(
                sshCommand.ExitStatus ?? -1,
                sshCommand.Error ?? string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or SshConnectionException)
        {
            ct.ThrowIfCancellationRequested();
            return await _fallback.ExecuteAsync(command, ct).ConfigureAwait(false);
        }
    }

    private static bool IsClientConnected(SshClient client)
    {
        try
        {
            return client.IsConnected;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}

internal sealed class SshNetSftpExecCommandRunner : ISftpExecCommandRunner
{
    private readonly SshConnectionParams _connectionParams;
    private readonly PinnedFingerprintVerifier _pinnedHostKeyVerifier;

    public SshNetSftpExecCommandRunner(
        SshConnectionParams connectionParams,
        PinnedFingerprintVerifier pinnedHostKeyVerifier)
    {
        _connectionParams = connectionParams ?? throw new ArgumentNullException(nameof(connectionParams));
        _pinnedHostKeyVerifier = pinnedHostKeyVerifier
            ?? throw new ArgumentNullException(nameof(pinnedHostKeyVerifier));
    }

    public async Task<SftpExecResult> ExecuteAsync(string command, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ct.ThrowIfCancellationRequested();

        SshClient ssh = SshConnectionFactory.CreateSshClient(_connectionParams);
        try
        {
            SshConnectionFactory.AttachPinnedHostKeyVerification(
                ssh,
                _connectionParams,
                _pinnedHostKeyVerifier);

            using CancellationTokenRegistration cancellationRegistration =
                ct.Register(static state => ((SshClient)state!).Dispose(), ssh);

            try
            {
                // The registration above stays: it tears the client down on cancel. What
                // changes is that the handshake itself now observes the token instead of
                // running on to the connect timeout while nothing could interrupt it.
                await SshConnectionFactory.ConnectWithCancellationAsync(ssh, ct).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();
                using SshCommand sshCommand = ssh.CreateCommand(command);
                await sshCommand.ExecuteAsync(ct).ConfigureAwait(false);

                return new SftpExecResult(
                    sshCommand.ExitStatus ?? -1,
                    sshCommand.Error ?? string.Empty);
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (SshException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
        }
        finally
        {
            ssh.Dispose();
        }
    }
}
