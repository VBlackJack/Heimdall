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
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    ssh.Connect();
                }, ct).ConfigureAwait(false);

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
