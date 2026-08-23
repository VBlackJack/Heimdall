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

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Heimdall.Core.Certificates;

/// <summary>How a probe ended.</summary>
public enum RdpProbeOutcome
{
    /// <summary>A certificate was read; <see cref="RdpProbeResult.Thumbprint"/> is set.</summary>
    CertificateObtained,

    /// <summary>The server answered but keeps standard RDP security - no certificate exists.</summary>
    TlsNotOffered,

    /// <summary>The endpoint could not be reached, or did not answer in time.</summary>
    Unreachable,

    /// <summary>The TLS handshake failed after the server had selected TLS.</summary>
    HandshakeFailed,

    /// <summary>The server answered something this code cannot read.</summary>
    ProtocolUnexpected,
}

/// <summary>What one probe of an RDP endpoint found.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Thumbprint">SHA-256 thumbprint, when a certificate was read.</param>
/// <param name="Subject">Certificate subject, when one was read.</param>
/// <param name="Issuer">Certificate issuer, when one was read.</param>
/// <param name="Detail">Short reason, for the log, when something went wrong.</param>
public sealed record RdpProbeResult(
    RdpProbeOutcome Outcome,
    string? Thumbprint = null,
    string? Subject = null,
    string? Issuer = null,
    string? Detail = null);

/// <summary>Reads the certificate an RDP endpoint presents, before connecting to it.</summary>
public interface IRdpCertificateProbe
{
    /// <summary>Opens a throwaway connection and reads the server certificate.</summary>
    /// <param name="host">Host to reach.</param>
    /// <param name="port">Port to reach.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    Task<RdpProbeResult> ProbeAsync(string host, int port, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the certificate an RDP endpoint presents, by speaking just enough RDP to get it.
/// </summary>
/// <remarks>
/// <b>This probe answers "what certificate is at this address right now", and nothing
/// more.</b> Two limits are structural and must not be papered over by a caller:
/// <list type="number">
/// <item>
/// It opens its OWN connection. Nothing guarantees the session that follows reaches the
/// same machine - on a pool of servers behind one name, which is the case this whole
/// feature exists for, it is the normal outcome that it does not. That is exactly why
/// trust is held as a SET: whichever member answers next, its certificate was approved
/// individually. A single-valued store would turn this limit into a hole.
/// </item>
/// <item>
/// It must run before EVERY connection, never once when a profile is created. The value it
/// returns describes one moment.
/// </item>
/// </list>
/// </remarks>
public sealed class RdpCertificateProbe : IRdpCertificateProbe
{
    private readonly TimeSpan _timeout;

    /// <summary>Initializes a new instance of the <see cref="RdpCertificateProbe"/> class.</summary>
    /// <param name="timeout">How long the whole probe may take; five seconds by default.</param>
    public RdpCertificateProbe(TimeSpan? timeout = null)
        => _timeout = timeout ?? TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    public async Task<RdpProbeResult> ProbeAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        using CancellationTokenSource bounded =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(_timeout);

        try
        {
            using TcpClient tcp = new();
            await tcp.ConnectAsync(host, port, bounded.Token);
            await using NetworkStream stream = tcp.GetStream();

            RdpNegotiationOutcome negotiated =
                await NegotiateAsync(stream, bounded.Token);

            if (negotiated != RdpNegotiationOutcome.TlsSelected)
            {
                return new RdpProbeResult(
                    negotiated switch
                    {
                        RdpNegotiationOutcome.TlsNotOffered => RdpProbeOutcome.TlsNotOffered,
                        RdpNegotiationOutcome.Refused => RdpProbeOutcome.TlsNotOffered,
                        _ => RdpProbeOutcome.ProtocolUnexpected,
                    },
                    Detail: negotiated.ToString());
            }

            return await ReadCertificateAsync(stream, host, bounded.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RdpProbeResult(RdpProbeOutcome.Unreachable, Detail: "timed out");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return new RdpProbeResult(RdpProbeOutcome.Unreachable, Detail: ex.Message);
        }
    }

    private static async Task<RdpNegotiationOutcome> NegotiateAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(RdpSecurityNegotiation.BuildConnectionRequest(), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);

        int total = RdpSecurityNegotiation.ReadTpktLength(header);
        if (total < 4 || total > 512)
        {
            return RdpNegotiationOutcome.Malformed;
        }

        byte[] frame = new byte[total];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(4, total - 4), cancellationToken);

        return RdpSecurityNegotiation.ParseConnectionConfirm(frame);
    }

    private static async Task<RdpProbeResult> ReadCertificateAsync(
        NetworkStream stream,
        string host,
        CancellationToken cancellationToken)
    {
        X509Certificate2? captured = null;

        // Accepts anything on purpose. This handshake exists ONLY to obtain the
        // certificate so it can be compared against what the user approved; trusting the
        // chain here would answer a question nobody asked, and refusing an unknown one
        // would make the probe unable to report the very case it exists for. The decision
        // is taken afterwards, against the profile's set.
        using SslStream tls = new(
            stream,
            leaveInnerStreamOpen: true,
            (_, certificate, _, _) =>
            {
                if (certificate is not null)
                {
                    captured = new X509Certificate2(certificate);
                }

                return true;
            });

        try
        {
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = host },
                cancellationToken);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            return captured is null
                ? new RdpProbeResult(RdpProbeOutcome.HandshakeFailed, Detail: ex.Message)
                : Describe(captured);
        }

        return captured is null
            ? new RdpProbeResult(RdpProbeOutcome.HandshakeFailed, Detail: "no certificate offered")
            : Describe(captured);
    }

    private static RdpProbeResult Describe(X509Certificate2 certificate)
    {
        using (certificate)
        {
            return new RdpProbeResult(
                RdpProbeOutcome.CertificateObtained,
                CertificateFingerprint.ComputeSha256(certificate),
                certificate.Subject,
                certificate.Issuer);
        }
    }
}
