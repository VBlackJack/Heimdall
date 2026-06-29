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
using System.Text;

namespace Heimdall.Ssh;

/// <summary>
/// Lightweight SSH reachability probe. Opens TCP and reads the protocol banner
/// only; it does not authenticate or trigger host-key verification.
/// </summary>
public static class SshConnectionProbe
{
    private const int MaxBannerBytes = 512;

    public const string MessageKeyMissingBanner = "SshProbeMissingBanner";
    public const string MessageKeyNonSshBanner = "SshProbeNonSshBanner";
    public const string MessageKeyConnectionTimedOut = "SshProbeConnectionTimedOut";
    public const string MessageKeyConnectionRefused = "SshProbeConnectionRefused";
    public const string MessageKeyNetworkUnreachable = "SshProbeNetworkUnreachable";
    public const string MessageKeyConnectionReset = "SshProbeConnectionReset";
    public const string MessageKeyUnknownFailure = "SshProbeUnknownFailure";

    public sealed record ProbeResult(
        bool Success,
        string? Banner,
        SshFailureCode? FailureCode,
        string? MessageKey,
        IReadOnlyList<object?> MessageArguments)
    {
        public ProbeResult(
            bool success,
            string? banner,
            SshFailureCode? failureCode,
            string? messageKey)
            : this(success, banner, failureCode, messageKey, Array.Empty<object?>())
        {
        }
    }

    public static async Task<ProbeResult> ProbeAsync(
        string host,
        int port,
        int timeoutMs,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);

            await using var stream = client.GetStream();
            var banner = await ReadBannerAsync(stream, linkedCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(banner))
            {
                return new ProbeResult(
                    false,
                    banner,
                    SshFailureCode.ProtocolError,
                    MessageKeyMissingBanner);
            }

            var trimmed = banner.Trim();
            if (!trimmed.StartsWith("SSH-", StringComparison.Ordinal))
            {
                return new ProbeResult(
                    false,
                    trimmed,
                    SshFailureCode.ProtocolError,
                    MessageKeyNonSshBanner);
            }

            return new ProbeResult(true, trimmed, null, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return new ProbeResult(
                false,
                null,
                SshFailureCode.NetworkTimedOut,
                MessageKeyConnectionTimedOut);
        }
        catch (SocketException ex)
        {
            return ClassifySocketException(ex);
        }
    }

    private static async Task<string?> ReadBannerAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        var buffer = new byte[128];
        var line = new StringBuilder();
        string? firstNonEmptyLine = null;
        var totalRead = 0;

        while (totalRead < MaxBannerBytes)
        {
            var bytesToRead = Math.Min(buffer.Length, MaxBannerBytes - totalRead);
            var bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(0, bytesToRead),
                    ct)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;

            for (var i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (TryCaptureBannerLine(line.ToString(), ref firstNonEmptyLine, out var sshBanner))
                    {
                        return sshBanner;
                    }

                    line.Clear();
                    continue;
                }

                line.Append((char)buffer[i]);
            }
        }

        if (line.Length > 0
            && TryCaptureBannerLine(line.ToString(), ref firstNonEmptyLine, out var partialSshBanner))
        {
            return partialSshBanner;
        }

        return firstNonEmptyLine;
    }

    private static bool TryCaptureBannerLine(
        string rawLine,
        ref string? firstNonEmptyLine,
        out string? sshBanner)
    {
        var line = rawLine.TrimEnd('\r');
        if (!string.IsNullOrWhiteSpace(line) && firstNonEmptyLine is null)
        {
            firstNonEmptyLine = line;
        }

        if (line.StartsWith("SSH-", StringComparison.Ordinal))
        {
            sshBanner = line;
            return true;
        }

        sshBanner = null;
        return false;
    }

    private static ProbeResult ClassifySocketException(SocketException ex)
    {
        return ex.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => new ProbeResult(
                false,
                null,
                SshFailureCode.NetworkRefused,
                MessageKeyConnectionRefused),
            SocketError.TimedOut => new ProbeResult(
                false,
                null,
                SshFailureCode.NetworkTimedOut,
                MessageKeyConnectionTimedOut),
            SocketError.HostNotFound
                or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable => new ProbeResult(
                    false,
                    null,
                    SshFailureCode.NetworkUnreachable,
                    MessageKeyNetworkUnreachable,
                    new object?[] { ex.Message }),
            SocketError.ConnectionReset => new ProbeResult(
                false,
                null,
                SshFailureCode.NetworkReset,
                MessageKeyConnectionReset),
            _ => new ProbeResult(
                false,
                null,
                SshFailureCode.Unknown,
                MessageKeyUnknownFailure,
                new object?[] { ex.Message })
        };
    }
}
