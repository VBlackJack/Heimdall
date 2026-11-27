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

using Heimdall.Ssh;

namespace Heimdall.App.Services;

internal interface IPlinkHostKeyProbe
{
    Task<PlinkHostKeyPresentation?> ProbeAsync(
        string plinkPath,
        string host,
        int port,
        string? username,
        int timeoutMs,
        CancellationToken ct);
}

internal sealed class DefaultPlinkHostKeyProbe : IPlinkHostKeyProbe
{
    public async Task<PlinkHostKeyPresentation?> ProbeAsync(
        string plinkPath,
        string host,
        int port,
        string? username,
        int timeoutMs,
        CancellationToken ct)
    {
        // Prefer an in-process SSH.NET pre-authentication probe first: it never invokes
        // GSSAPI/Kerberos before host-key trust is confirmed and captures the raw public key
        // blob so known_hosts export works for accepted keys. Fall back to the Plink probe only
        // when SSH.NET cannot negotiate the server's key exchange or host-key algorithms.
        try
        {
            var probeParams = new SshConnectionParams
            {
                Host = host,
                Port = port,
                Username = string.Empty,
                ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs))
            };
            var presentation = await SshConnectionFactory.ProbeHostKeyAsync(
                    probeParams,
                    host,
                    port,
                    ct)
                .ConfigureAwait(false);
            return new PlinkHostKeyPresentation(
                presentation.Algorithm,
                presentation.Fingerprint,
                Convert.ToBase64String(presentation.HostKey));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Debug(
                $"SSH.NET host key probe fell back to Plink for {host}:{port}: {ex.Message}");
        }

        return await PlinkHostKeyProbe.ProbeAsync(
                plinkPath,
                host,
                port,
                username,
                timeoutMs,
                ct)
            .ConfigureAwait(false);
    }
}
