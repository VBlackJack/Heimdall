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

namespace Heimdall.Core.Certificates;

/// <summary>What the server answered when asked to speak TLS.</summary>
internal enum RdpNegotiationOutcome
{
    /// <summary>The server selected TLS; a handshake may begin on the same socket.</summary>
    TlsSelected,

    /// <summary>The server answered, but keeps standard RDP security - there is no certificate.</summary>
    TlsNotOffered,

    /// <summary>
    /// The server explicitly refused the requested protocol. It is not the same answer as
    /// <see cref="TlsNotOffered"/>: a refusal can mean the server demands MORE than TLS,
    /// so it says nothing about whether a certificate exists.
    /// </summary>
    Refused,

    /// <summary>The answer was not a connection confirm this code can read.</summary>
    Malformed,
}

/// <summary>
/// The X.224 exchange that has to happen before an RDP server will speak TLS.
/// </summary>
/// <remarks>
/// <b>An RDP endpoint does not answer a bare TLS handshake.</b> The client first sends an
/// X.224 Connection Request carrying an RDP negotiation request, the server answers with a
/// Connection Confirm naming the protocol it selected, and only then does the TLS handshake
/// run over the same socket.
/// <para>
/// This matters because the two certificate probes already in this codebase -
/// <c>CertInspectorEngine</c> and <c>TlsAuditEngine</c> - open an <c>SslStream</c> directly
/// on the socket. They are reusable for INSPECTING a certificate once a TLS stream exists,
/// and they cannot REACH an RDP server at all. Reusing either here would have failed
/// against every real endpoint, and only on a machine that has one.
/// </para>
/// </remarks>
internal static class RdpSecurityNegotiation
{
    /// <summary>PROTOCOL_SSL - plain TLS, which is all a probe needs.</summary>
    /// <remarks>
    /// Deliberately not requesting HYBRID: CredSSP would carry credentials, and this
    /// exchange exists only to look at a certificate.
    /// </remarks>
    internal const uint ProtocolSsl = 0x0000_0001;

    /// <summary>Standard RDP security, meaning no certificate to inspect.</summary>
    internal const uint ProtocolRdp = 0x0000_0000;

    private const byte TpktVersion = 0x03;
    private const byte TpduConnectionRequest = 0xE0;
    private const byte TpduConnectionConfirm = 0xD0;
    private const byte TypeRdpNegRsp = 0x02;
    private const byte TypeRdpNegFailure = 0x03;

    // RDP_NEG_FAILURE codes, MS-RDPBCGR 2.2.1.2.2.
    private const uint SslRequiredByServer = 0x0000_0001;
    private const uint SslNotAllowedByServer = 0x0000_0002;
    private const uint SslCertNotOnServer = 0x0000_0003;
    private const uint InconsistentFlags = 0x0000_0004;
    private const uint HybridRequiredByServer = 0x0000_0005;
    private const uint SslWithUserAuthRequiredByServer = 0x0000_0006;

    /// <summary>Length of a connection confirm carrying no negotiation response.</summary>
    private const int BareConfirmLength = 11;

    /// <summary>Length of a connection confirm carrying one.</summary>
    private const int NegotiatedConfirmLength = 19;

    /// <summary>Builds the X.224 Connection Request asking the server for TLS.</summary>
    /// <remarks>
    /// Nineteen bytes: a four-byte TPKT header, a seven-byte X.224 connection request, and
    /// an eight-byte RDP negotiation request. The two length fields disagree on purpose -
    /// TPKT counts the whole frame and is big-endian, the negotiation request counts only
    /// itself and is little-endian - and getting either backwards produces a server that
    /// simply never answers.
    /// </remarks>
    internal static byte[] BuildConnectionRequest()
    {
        byte[] frame = new byte[NegotiatedConfirmLength];

        frame[0] = TpktVersion;
        frame[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), NegotiatedConfirmLength);

        // Length indicator: everything in the X.224 layer after this byte.
        frame[4] = NegotiatedConfirmLength - 4 - 1;
        frame[5] = TpduConnectionRequest;
        frame[6] = 0x00;
        frame[7] = 0x00;
        frame[8] = 0x00;
        frame[9] = 0x00;
        frame[10] = 0x00;

        frame[11] = 0x01;
        frame[12] = 0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(13, 2), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(15, 4), ProtocolSsl);

        return frame;
    }

    /// <summary>Reads the server answer and says whether TLS may begin.</summary>
    /// <param name="frame">The whole TPKT frame, header included.</param>
    /// <remarks>
    /// A confirm with no negotiation response is not malformed: it is a server that keeps
    /// standard RDP security and has no certificate to show. Treating that as an error
    /// would report a protocol fault where the honest answer is "there is nothing here to
    /// verify".
    /// </remarks>
    internal static RdpNegotiationOutcome ParseConnectionConfirm(ReadOnlySpan<byte> frame)
        => ParseConnectionConfirm(frame, out _);

    /// <summary>Reads the server answer, keeping the reason when it refused.</summary>
    /// <param name="frame">The whole TPKT frame, header included.</param>
    /// <param name="failureCode">
    /// The RDP_NEG_FAILURE code when the outcome is <see cref="RdpNegotiationOutcome.Refused"/>,
    /// zero otherwise.
    /// </param>
    /// <remarks>
    /// The code is the whole content of a refusal - SSL_NOT_ALLOWED_BY_SERVER and
    /// HYBRID_REQUIRED_BY_SERVER are opposite facts about the server - so discarding it
    /// leaves a log line that can only say that something was refused.
    /// </remarks>
    internal static RdpNegotiationOutcome ParseConnectionConfirm(
        ReadOnlySpan<byte> frame,
        out uint failureCode)
    {
        failureCode = 0;

        if (frame.Length < BareConfirmLength
            || frame[0] != TpktVersion
            || BinaryPrimitives.ReadUInt16BigEndian(frame[2..4]) != frame.Length
            || frame[5] != TpduConnectionConfirm)
        {
            return RdpNegotiationOutcome.Malformed;
        }

        if (frame.Length < NegotiatedConfirmLength)
        {
            return RdpNegotiationOutcome.TlsNotOffered;
        }

        switch (frame[11])
        {
            case TypeRdpNegFailure:
                // The failure code sits where a response would carry the selected protocol.
                failureCode = SelectedProtocol(frame);
                return RdpNegotiationOutcome.Refused;

            case TypeRdpNegRsp:
                return SelectedProtocol(frame) == ProtocolRdp
                    ? RdpNegotiationOutcome.TlsNotOffered
                    : RdpNegotiationOutcome.TlsSelected;

            default:
                return RdpNegotiationOutcome.Malformed;
        }
    }

    /// <summary>Names an RDP_NEG_FAILURE code, for the log.</summary>
    /// <param name="failureCode">The code the server answered with.</param>
    /// <remarks>
    /// Named rather than numbered because the names are what the fact is: a reader who has
    /// to look up 0x00000005 to learn that the server DEMANDS CredSSP will read past it.
    /// The number is kept beside the name for a code this list does not know.
    /// </remarks>
    internal static string DescribeFailure(uint failureCode)
    {
        string name = failureCode switch
        {
            SslRequiredByServer => "SSL_REQUIRED_BY_SERVER",
            SslNotAllowedByServer => "SSL_NOT_ALLOWED_BY_SERVER",
            SslCertNotOnServer => "SSL_CERT_NOT_ON_SERVER",
            InconsistentFlags => "INCONSISTENT_FLAGS",
            HybridRequiredByServer => "HYBRID_REQUIRED_BY_SERVER",
            SslWithUserAuthRequiredByServer => "SSL_WITH_USER_AUTH_REQUIRED_BY_SERVER",
            _ => "unknown failure code",
        };

        return $"{name} (0x{failureCode:X8})";
    }

    /// <summary>Total frame length announced by a TPKT header.</summary>
    /// <param name="header">At least the four header bytes.</param>
    internal static int ReadTpktLength(ReadOnlySpan<byte> header)
        => header.Length < 4 || header[0] != TpktVersion
            ? -1
            : BinaryPrimitives.ReadUInt16BigEndian(header[2..4]);

    private static uint SelectedProtocol(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt32LittleEndian(frame[15..19]);
}
