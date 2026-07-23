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

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Heimdall.Ssh.Pageant;

/// <summary>
/// Communicates with PuTTY Pageant via shared memory IPC to access loaded SSH keys.
/// Supports SSH agent protocol operations: listing identities and signing data.
/// Windows-only by construction (Pageant is a Windows GUI process).
/// </summary>
public sealed class PageantClient : IPageantClient
{
    private const byte SSH2_AGENTC_REQUEST_IDENTITIES = 11;
    private const byte SSH2_AGENT_IDENTITIES_ANSWER = 12;
    private const byte SSH2_AGENTC_SIGN_REQUEST = 13;
    private const byte SSH2_AGENT_SIGN_RESPONSE = 14;
    private const int WM_COPYDATA = 0x004A;
    internal const int AgentMaxMessageLength = 262144;

    /// <summary>
    /// Magic value required by Pageant in COPYDATASTRUCT.dwData.
    /// Defined as AGENT_COPYDATA_ID in the PuTTY source (windows/utils/agent_named_pipe.c).
    /// Pageant silently rejects WM_COPYDATA messages that don't carry this value.
    /// </summary>
    private static readonly IntPtr AgentCopyDataId = new(0x804e50ba);

    /// <summary>
    /// Check whether the Pageant SSH agent window is reachable.
    /// </summary>
    public static bool IsAvailable()
    {
        return NativeMethods.FindWindow("Pageant", "Pageant") != IntPtr.Zero;
    }

    /// <summary>
    /// Retrieves all SSH identity key blobs from the Pageant agent.
    /// </summary>
    /// <returns>List of loaded keys with their type, blob, and comment.</returns>
    /// <exception cref="InvalidOperationException">Pageant is not running or returned an invalid response.</exception>
    public List<PageantKey> GetIdentities()
    {
        // Build request: [length:4 big-endian][SSH2_AGENTC_REQUEST_IDENTITIES:1]
        var request = new byte[5];
        WriteBigEndianUInt32(request, 0, 1); // payload length = 1
        request[4] = SSH2_AGENTC_REQUEST_IDENTITIES;

        var response = SendMessage(request);
        return ParseIdentitiesResponse(response);
    }

    /// <summary>
    /// Requests the Pageant agent to sign data with a specific key.
    /// Used during SSH authentication to prove possession of the private key.
    /// </summary>
    /// <param name="keyBlob">Public key blob identifying which key to use for signing.</param>
    /// <param name="data">Data to sign (typically the session hash + auth request).</param>
    /// <param name="flags">Agent signature flags (0 for default behavior).</param>
    /// <returns>The full SSH signature blob from the agent (algorithm name + signature, each length-prefixed) — not raw signature bytes.</returns>
    /// <exception cref="InvalidOperationException">Pageant is not running or signing failed.</exception>
    public byte[] SignData(byte[] keyBlob, byte[] data, uint flags = 0)
    {
        ArgumentNullException.ThrowIfNull(keyBlob);
        ArgumentNullException.ThrowIfNull(data);

        // Build request: [length:4][SSH2_AGENTC_SIGN_REQUEST:1]
        //                [key_blob_length:4][key_blob]
        //                [data_length:4][data]
        //                [flags:4]
        int payloadLength = 1 + 4 + keyBlob.Length + 4 + data.Length + 4;
        var request = new byte[4 + payloadLength];
        int offset = 0;

        WriteBigEndianUInt32(request, offset, (uint)payloadLength);
        offset += 4;

        request[offset++] = SSH2_AGENTC_SIGN_REQUEST;

        WriteBigEndianUInt32(request, offset, (uint)keyBlob.Length);
        offset += 4;
        Array.Copy(keyBlob, 0, request, offset, keyBlob.Length);
        offset += keyBlob.Length;

        WriteBigEndianUInt32(request, offset, (uint)data.Length);
        offset += 4;
        Array.Copy(data, 0, request, offset, data.Length);
        offset += data.Length;

        WriteBigEndianUInt32(request, offset, flags);

        var response = SendMessage(request);
        return ParseSignResponse(response);
    }

    /// <summary>
    /// Known legitimate Pageant host process names (case-insensitive).
    /// </summary>
    private static readonly string[] TrustedPageantProcessNames =
        ["pageant", "putty", "plink", "pscp", "psftp", "kitty", "winscp", "keepassxc-proxy", "keepassxc"];

    /// <summary>
    /// Sends a message to Pageant via shared memory IPC and returns the response.
    /// Validates that the Pageant window is owned by a trusted process before IPC.
    /// </summary>
    /// <param name="request">Raw SSH agent protocol request bytes.</param>
    /// <returns>Raw response bytes from Pageant.</returns>
    internal byte[] SendMessage(byte[] request)
    {
        var hwnd = NativeMethods.FindWindow("Pageant", "Pageant");
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Pageant is not running.");
        }

        // Verify the owning process is a legitimate Pageant host to prevent
        // IPC spoofing via a malicious window with the same class/title
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pageantPid);
        if (pageantPid > 0)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pageantPid);
                var processName = proc.ProcessName;
                if (!TrustedPageantProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Pageant window owned by untrusted process '{processName}' (PID {pageantPid}). " +
                        "Aborting IPC to prevent credential theft.");
                }
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("Pageant process exited before identity verification.");
            }
        }

        if (request.Length > AgentMaxMessageLength)
        {
            throw new ArgumentException(
                $"Request exceeds maximum agent message length of {AgentMaxMessageLength} bytes.");
        }

        // The mapping name is per-process unique with a cryptographic random suffix
        // to defeat enumeration by another userland process running in the same session.
        // GetHexString(16) returns 16 hex chars = 64 bits of entropy; combined with
        // the self-only DACL applied below this is sufficient defense in depth.
        var mapName = $"PageantRequest{Environment.ProcessId:X8}{RandomNumberGenerator.GetHexString(16)}";
        var mappingSize = (uint)Math.Max(request.Length, AgentMaxMessageLength);

        // Pageant only runs on Windows; this guard keeps the analyzer aware that
        // the SecurityAttributesScope (Windows-only by construction) is reachable
        // only from a Windows runtime path.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Pageant IPC is only supported on Windows.");
        }

        // Apply a self-only DACL to the file mapping so that no other process,
        // even running under the same user, can OpenFileMapping by guessing the name.
        // Pageant itself uses the calling user's token to read the mapping, so a
        // current-user-only ACL is sufficient.
        using var securityScope = SecurityAttributesScope.CreateSelfOnly();
        using var fileMapping = NativeMethods.CreateFileMapping(
            NativeMethods.InvalidHandleValue,
            securityScope.Pointer,
            NativeMethods.PAGE_READWRITE,
            0,
            mappingSize,
            mapName);

        if (fileMapping.IsInvalid)
        {
            throw new InvalidOperationException(
                $"Failed to create file mapping. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        var view = NativeMethods.MapViewOfFile(
            fileMapping.DangerousGetHandle(),
            NativeMethods.FILE_MAP_ALL_ACCESS,
            0, 0, UIntPtr.Zero);

        if (view == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Failed to map view of file. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        try
        {
            // Write request to shared memory
            Marshal.Copy(request, 0, view, request.Length);

            // Send WM_COPYDATA to Pageant window with the mapping name
            var mapNameBytes = Encoding.UTF8.GetBytes(mapName + '\0');
            var lpData = Marshal.AllocHGlobal(mapNameBytes.Length);

            try
            {
                Marshal.Copy(mapNameBytes, 0, lpData, mapNameBytes.Length);

                var copyData = new NativeMethods.COPYDATASTRUCT
                {
                    dwData = AgentCopyDataId,
                    cbData = mapNameBytes.Length,
                    lpData = lpData
                };

                var result = NativeMethods.SendMessage(hwnd, WM_COPYDATA, IntPtr.Zero, ref copyData);
                if (result == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Pageant rejected the agent request.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(lpData);
            }

            // Read response length from shared memory (first 4 bytes, big-endian)
            var lengthBytes = new byte[sizeof(uint)];
            Marshal.Copy(view, lengthBytes, 0, lengthBytes.Length);
            uint responsePayloadLength = ReadBigEndianUInt32(lengthBytes, 0);
            int totalResponseLength = GetValidatedTotalResponseLength(responsePayloadLength);
            var response = new byte[totalResponseLength];
            Marshal.Copy(view, response, 0, totalResponseLength);

            return response;
        }
        finally
        {
            NativeMethods.UnmapViewOfFile(view);
        }
    }

    internal static int GetValidatedTotalResponseLength(uint responsePayloadLength)
    {
        uint maximumPayloadLength = AgentMaxMessageLength - sizeof(uint);
        if (responsePayloadLength == 0 || responsePayloadLength > maximumPayloadLength)
        {
            throw new InvalidOperationException(
                $"Pageant returned invalid response length: {responsePayloadLength}");
        }

        return sizeof(uint) + (int)responsePayloadLength;
    }

    /// <summary>
    /// Parses an SSH2_AGENT_IDENTITIES_ANSWER response.
    /// Wire format: [length:4][type:1][count:4][{blob_len:4, blob, comment_len:4, comment}...]
    /// </summary>
    internal static List<PageantKey> ParseIdentitiesResponse(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Length < 5)
        {
            throw new InvalidOperationException("Response too short to contain a valid agent message.");
        }

        int offset = 4; // skip length prefix
        byte messageType = response[offset++];

        if (messageType != SSH2_AGENT_IDENTITIES_ANSWER)
        {
            throw new InvalidOperationException(
                $"Unexpected agent response type: {messageType} (expected {SSH2_AGENT_IDENTITIES_ANSWER}).");
        }

        if (offset + 4 > response.Length)
        {
            throw new InvalidOperationException("Response too short to contain key count.");
        }

        uint keyCount = ReadBigEndianUInt32(response, offset);
        offset += 4;

        var keys = new List<PageantKey>((int)Math.Min(keyCount, 256));

        for (uint i = 0; i < keyCount; i++)
        {
            // Read key blob
            if (offset + 4 > response.Length)
            {
                throw new InvalidOperationException($"Response truncated reading key blob length at key {i}.");
            }

            int blobLength = (int)ReadBigEndianUInt32(response, offset);
            offset += 4;

            if (blobLength <= 0 || offset + blobLength > response.Length)
            {
                throw new InvalidOperationException($"Invalid key blob length {blobLength} at key {i}.");
            }

            var blob = new byte[blobLength];
            Array.Copy(response, offset, blob, 0, blobLength);
            offset += blobLength;

            // Read comment
            if (offset + 4 > response.Length)
            {
                throw new InvalidOperationException($"Response truncated reading comment length at key {i}.");
            }

            int commentLength = (int)ReadBigEndianUInt32(response, offset);
            offset += 4;

            if (commentLength < 0 || offset + commentLength > response.Length)
            {
                throw new InvalidOperationException($"Invalid comment length {commentLength} at key {i}.");
            }

            string comment = Encoding.UTF8.GetString(response, offset, commentLength);
            offset += commentLength;

            // Extract key type from blob (first string field in the blob)
            string keyType = ExtractKeyTypeFromBlob(blob);

            keys.Add(new PageantKey(blob, comment, keyType));
        }

        return keys;
    }

    /// <summary>
    /// Parses an SSH2_AGENT_SIGN_RESPONSE message.
    /// Wire format: [length:4][type:1][signature_len:4][signature]
    /// </summary>
    internal static byte[] ParseSignResponse(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Length < 5)
        {
            throw new InvalidOperationException("Response too short to contain a valid sign response.");
        }

        int offset = 4; // skip length prefix
        byte messageType = response[offset++];

        if (messageType != SSH2_AGENT_SIGN_RESPONSE)
        {
            throw new InvalidOperationException(
                $"Unexpected agent response type: {messageType} (expected {SSH2_AGENT_SIGN_RESPONSE}). " +
                "Signing may have been denied by the agent.");
        }

        if (offset + 4 > response.Length)
        {
            throw new InvalidOperationException("Response too short to contain signature length.");
        }

        int signatureLength = (int)ReadBigEndianUInt32(response, offset);
        offset += 4;

        if (signatureLength <= 0 || offset + signatureLength > response.Length)
        {
            throw new InvalidOperationException($"Invalid signature length: {signatureLength}.");
        }

        var signature = new byte[signatureLength];
        Array.Copy(response, offset, signature, 0, signatureLength);

        return signature;
    }

    /// <summary>
    /// Extracts the SSH key type string from a public key blob.
    /// The blob starts with [type_len:4][type_string].
    /// </summary>
    internal static string ExtractKeyTypeFromBlob(byte[] blob)
    {
        if (blob.Length < 4)
        {
            return "unknown";
        }

        int typeLength = (int)ReadBigEndianUInt32(blob, 0);
        if (typeLength <= 0 || typeLength > blob.Length - 4)
        {
            return "unknown";
        }

        return Encoding.ASCII.GetString(blob, 4, typeLength);
    }

    /// <summary>
    /// Writes a 32-bit unsigned integer in big-endian byte order.
    /// </summary>
    internal static void WriteBigEndianUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    /// <summary>
    /// Reads a 32-bit unsigned integer from big-endian byte order.
    /// </summary>
    internal static uint ReadBigEndianUInt32(byte[] buffer, int offset)
    {
        return ((uint)buffer[offset] << 24)
             | ((uint)buffer[offset + 1] << 16)
             | ((uint)buffer[offset + 2] << 8)
             | buffer[offset + 3];
    }

}
