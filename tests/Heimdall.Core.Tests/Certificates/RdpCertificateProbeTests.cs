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

using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Heimdall.Core.Certificates;

namespace Heimdall.Core.Tests.Certificates;

/// <summary>
/// Reading the certificate an RDP endpoint presents, before connecting to it.
/// </summary>
/// <remarks>
/// The frame assertions are exact bytes rather than round trips through this same code:
/// a builder and a parser that agree with each other prove nothing about what a server
/// expects. The end-to-end test is what actually establishes that the X.224 preamble is
/// right - without it the bytes would be verified only by the arithmetic that produced
/// them.
/// </remarks>
public sealed class RdpCertificateProbeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [Fact]
    public void BuildConnectionRequest_IsTheNineteenBytesAnRdpServerExpects()
    {
        byte[] frame = RdpSecurityNegotiation.BuildConnectionRequest();

        // Written out rather than computed, so a change to the builder has to be a
        // deliberate change to this literal. The two length fields disagree on purpose:
        // TPKT counts the whole frame big-endian (00 13 = 19), the negotiation request
        // counts only itself little-endian (08 00 = 8).
        Assert.Equal(
            new byte[]
            {
                0x03, 0x00, 0x00, 0x13,
                0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x01, 0x00, 0x08, 0x00, 0x01, 0x00, 0x00, 0x00,
            },
            frame);
    }

    [Fact]
    public void ParseConnectionConfirm_ServerSelectedTls_SaysSo()
        => Assert.Equal(
            RdpNegotiationOutcome.TlsSelected,
            RdpSecurityNegotiation.ParseConnectionConfirm(Confirm(negType: 0x02, selected: 1)));

    [Fact]
    public void ParseConnectionConfirm_ServerKeptStandardSecurity_IsNotAnError()
    {
        // A server that answers PROTOCOL_RDP has no certificate to show. Reporting a
        // protocol fault here would name the wrong problem: there is nothing to verify,
        // which is a different thing from a broken exchange.
        Assert.Equal(
            RdpNegotiationOutcome.TlsNotOffered,
            RdpSecurityNegotiation.ParseConnectionConfirm(Confirm(negType: 0x02, selected: 0)));
    }

    [Fact]
    public void ParseConnectionConfirm_BareConfirmWithoutNegotiation_IsNotAnError()
    {
        // Older servers answer the request by ignoring it entirely. Same meaning: standard
        // RDP security, no certificate.
        Assert.Equal(
            RdpNegotiationOutcome.TlsNotOffered,
            RdpSecurityNegotiation.ParseConnectionConfirm(BareConfirm()));
    }

    [Fact]
    public void ParseConnectionConfirm_ServerRefused_IsReportedAsRefused()
        => Assert.Equal(
            RdpNegotiationOutcome.Refused,
            RdpSecurityNegotiation.ParseConnectionConfirm(Confirm(negType: 0x03, selected: 0)));

    [Theory]
    [InlineData(0, 0x04)]  // not a TPKT frame
    [InlineData(5, 0xE0)]  // a connection REQUEST answering a request
    public void ParseConnectionConfirm_NotAConfirm_IsMalformed(int index, byte value)
    {
        byte[] frame = Confirm(negType: 0x02, selected: 1);
        frame[index] = value;

        Assert.Equal(
            RdpNegotiationOutcome.Malformed,
            RdpSecurityNegotiation.ParseConnectionConfirm(frame));
    }

    [Fact]
    public void ParseConnectionConfirm_AnnouncedLengthDisagreesWithTheFrame_IsMalformed()
    {
        byte[] frame = Confirm(negType: 0x02, selected: 1);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 42);

        // A frame whose header lies about its own size is the shape a truncated read
        // produces, and reading past it would be reading someone else's bytes.
        Assert.Equal(
            RdpNegotiationOutcome.Malformed,
            RdpSecurityNegotiation.ParseConnectionConfirm(frame));
    }

    [Fact]
    public async Task ProbeAsync_ServerSpeaksTls_ReturnsThatServerThumbprint()
    {
        using X509Certificate2 certificate = CreateServerCertificate();
        using CancellationTokenSource cts = new(Bound);
        int port = StartFakeRdpServer(certificate, offerTls: true, cts.Token);

        RdpProbeResult result = await new RdpCertificateProbe(Bound)
            .ProbeAsync("127.0.0.1", port, cts.Token);

        // THE test of this lot. It is the only thing that establishes the X.224 preamble
        // is right: a bare SslStream on this socket - which is what the two certificate
        // probes already in this codebase do - never gets here.
        Assert.Equal(RdpProbeOutcome.CertificateObtained, result.Outcome);
        Assert.Equal(CertificateFingerprint.ComputeSha256(certificate), result.Thumbprint);
        Assert.Contains("rdp-probe-test", result.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_ServerKeepsStandardSecurity_SaysThereIsNothingToVerify()
    {
        using X509Certificate2 certificate = CreateServerCertificate();
        using CancellationTokenSource cts = new(Bound);
        int port = StartFakeRdpServer(certificate, offerTls: false, cts.Token);

        RdpProbeResult result = await new RdpCertificateProbe(Bound)
            .ProbeAsync("127.0.0.1", port, cts.Token);

        // Distinct from a failure on purpose: a caller must not offer to trust a
        // certificate that does not exist.
        Assert.Equal(RdpProbeOutcome.TlsNotOffered, result.Outcome);
        Assert.Null(result.Thumbprint);
    }

    [Fact]
    public async Task ProbeAsync_NothingListening_IsUnreachableRatherThanThrowing()
    {
        using CancellationTokenSource cts = new(Bound);
        int port = ReserveUnusedPort();

        RdpProbeResult result = await new RdpCertificateProbe(TimeSpan.FromSeconds(2))
            .ProbeAsync("127.0.0.1", port, cts.Token);

        // A probe that threw would take down the connection attempt it is supposed to
        // guard, turning a verification step into a new way to fail.
        Assert.Equal(RdpProbeOutcome.Unreachable, result.Outcome);
    }

    private static byte[] Confirm(byte negType, uint selected)
    {
        byte[] frame = new byte[19];
        frame[0] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 19);
        frame[4] = 14;
        frame[5] = 0xD0;
        frame[11] = negType;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(13, 2), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(15, 4), selected);
        return frame;
    }

    private static byte[] BareConfirm()
    {
        byte[] frame = new byte[11];
        frame[0] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 11);
        frame[4] = 6;
        frame[5] = 0xD0;
        return frame;
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=rdp-probe-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        // Re-imported through a PKCS#12 blob because a server handshake needs a key the
        // platform will actually use, which an ephemeral self-signed certificate does not
        // carry on Windows.
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx, "probe"),
            "probe",
            X509KeyStorageFlags.Exportable);
    }

    private static int ReserveUnusedPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static int StartFakeRdpServer(
        X509Certificate2 certificate,
        bool offerTls,
        CancellationToken cancellationToken)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                    await using NetworkStream stream = client.GetStream();

                    byte[] header = new byte[4];
                    await stream.ReadExactlyAsync(header, cancellationToken);

                    // Validated against the protocol, not against the builder that produced
                    // it. Without this the server would answer any nineteen bytes, and a
                    // mutant that corrupted the request would still make this test pass -
                    // the end-to-end run would prove only that TLS works over a socket.
                    int announced = (header[2] << 8) | header[3];
                    byte[] request = new byte[announced];
                    header.CopyTo(request, 0);
                    await stream.ReadExactlyAsync(
                        request.AsMemory(4, announced - 4),
                        cancellationToken);

                    bool wellFormed = header[0] == 0x03
                        && announced == 19
                        && request[4] == announced - 5
                        && request[5] == 0xE0
                        && request[11] == 0x01
                        && (BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(15, 4)) & 1) != 0;

                    if (!wellFormed)
                    {
                        return;
                    }

                    byte[] confirm = offerTls
                        ? Confirm(negType: 0x02, selected: 1)
                        : Confirm(negType: 0x02, selected: 0);
                    await stream.WriteAsync(confirm, cancellationToken);
                    await stream.FlushAsync(cancellationToken);

                    if (!offerTls)
                    {
                        return;
                    }

                    using SslStream tls = new(stream, leaveInnerStreamOpen: true);
                    await tls.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions { ServerCertificate = certificate },
                        cancellationToken);
                }
                catch (Exception)
                {
                    // The probe closes as soon as it has what it came for, so the tail of
                    // this exchange failing is the normal ending, not a fault.
                }
                finally
                {
                    listener.Stop();
                }
            },
            cancellationToken);

        return port;
    }
}
