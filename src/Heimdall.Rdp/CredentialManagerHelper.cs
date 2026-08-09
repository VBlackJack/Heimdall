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
using System.Text;

namespace Heimdall.Rdp;

/// <summary>
/// Manages RDP credentials in Windows Credential Manager for mstsc.exe.
/// Writes DOMAIN_PASSWORD credentials (CRED_TYPE=2) in the TERMSRV/host format
/// recognized by the Windows RDP client for automatic login.
/// </summary>
public static class CredentialManagerHelper
{
    #region Constants

    internal const uint CredTypeGeneric = 1;
    internal const uint CredTypeDomainPassword = 2;
    private const uint CredPersistSession = 1;
    private const int CredMaxCredentialBlobSize = 512;
    internal const string DomainCredentialOwnershipPrefix = "Heimdall:RDP:";
    internal const int ErrorNotFound = 1168;
    private const int ErrorInvalidData = 13;

    #endregion

    #region P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredentialPointers
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint SecretSize;
        public IntPtr SecretPointer;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    #endregion

    /// <summary>
    /// Creates a per-launch marker used to prove ownership of an RDP credential.
    /// </summary>
    public static string CreateDomainCredentialOwnershipMarker()
    {
        return $"{DomainCredentialOwnershipPrefix}{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Stores a DOMAIN_PASSWORD credential (CRED_TYPE=2) recognized by mstsc.exe / RDP
    /// only when the target is absent or already carries a Heimdall ownership marker.
    /// Target must follow the TERMSRV/host format for RDP auto-login.
    /// Persist is set to Session — credential lives only until logoff and is cleaned
    /// up by the caller after the RDP session launches (defense-in-depth).
    /// </summary>
    public static bool WriteDomainCredential(
        string targetName,
        string username,
        string password,
        string ownershipMarker,
        out bool credentialWritten,
        out string? error)
    {
        return WriteDomainCredential(
            targetName,
            username,
            password,
            ownershipMarker,
            (target, marker) => ProbeCredential(target, CredTypeDomainPassword, marker, exactMarker: false),
            WriteCredential,
            out credentialWritten,
            out error);
    }

    /// <summary>
    /// Deletes the DOMAIN_PASSWORD credential only when it still carries this launch's marker.
    /// </summary>
    public static bool DeleteCredential(
        string targetName,
        string ownershipMarker,
        out bool credentialDeleted,
        out string? error)
    {
        return DeleteCredential(
            targetName,
            ownershipMarker,
            (target, marker) => ProbeCredential(target, CredTypeDomainPassword, marker, exactMarker: true),
            (target, type) =>
            {
                bool deleted = CredDelete(target, type, 0);
                return new CredentialDeleteResult(
                    deleted,
                    deleted ? 0 : Marshal.GetLastWin32Error());
            },
            out credentialDeleted,
            out error);
    }

    internal static bool WriteDomainCredential(
        string targetName,
        string username,
        string password,
        string ownershipMarker,
        Func<string, string, CredentialProbeResult> probeCredential,
        CredentialWriteOperation writeCredential,
        out bool credentialWritten,
        out string? error)
    {
        credentialWritten = false;
        error = null;

        if (string.IsNullOrWhiteSpace(targetName))
        {
            error = "Credential target cannot be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ownershipMarker) ||
            !ownershipMarker.StartsWith(DomainCredentialOwnershipPrefix, StringComparison.Ordinal))
        {
            error = "Credential ownership marker is invalid.";
            return false;
        }

        ArgumentNullException.ThrowIfNull(probeCredential);
        ArgumentNullException.ThrowIfNull(writeCredential);

        CredentialProbeResult probe = probeCredential(targetName, DomainCredentialOwnershipPrefix);
        if (!probe.Success)
        {
            error = probe.Error;
            return false;
        }

        if (probe.Exists && !probe.MarkerMatches)
        {
            return true;
        }

        bool written = writeCredential(
            targetName,
            username,
            password,
            CredTypeDomainPassword,
            CredPersistSession,
            ownershipMarker,
            out error);
        credentialWritten = written;
        return written;
    }

    internal static bool DeleteCredential(
        string targetName,
        string ownershipMarker,
        Func<string, string, CredentialProbeResult> probeCredential,
        Func<string, uint, CredentialDeleteResult> deleteCredential,
        out bool credentialDeleted,
        out string? error)
    {
        credentialDeleted = false;
        error = null;

        if (string.IsNullOrWhiteSpace(targetName))
        {
            error = "Credential target cannot be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ownershipMarker))
        {
            error = "Credential ownership marker cannot be empty.";
            return false;
        }

        ArgumentNullException.ThrowIfNull(probeCredential);
        ArgumentNullException.ThrowIfNull(deleteCredential);

        CredentialProbeResult probe = probeCredential(targetName, ownershipMarker);
        if (!probe.Success)
        {
            error = probe.Error;
            return false;
        }

        if (!probe.Exists || !probe.MarkerMatches)
        {
            return true;
        }

        CredentialDeleteResult result = deleteCredential(targetName, CredTypeDomainPassword);
        if (!result.Success && result.ErrorCode != ErrorNotFound)
        {
            error = $"WIN32_ERROR_{result.ErrorCode}";
            return false;
        }

        credentialDeleted = result.Success;
        return true;
    }

    internal delegate bool CredentialWriteOperation(
        string target,
        string userName,
        string secret,
        uint credType,
        uint credPersist,
        string? comment,
        out string? error);

    internal delegate bool CredentialReadOperation(
        string targetName,
        uint credentialType,
        out IntPtr credentialPointer,
        out int errorCode);

    internal readonly record struct CredentialProbeResult(
        bool Success,
        bool Exists,
        bool MarkerMatches,
        string? Error);

    internal readonly record struct CredentialDeleteResult(bool Success, int ErrorCode);

    internal static CredentialProbeResult ProbeCredential(
        string targetName,
        uint credentialType,
        string marker,
        bool exactMarker,
        CredentialReadOperation readCredential,
        Action<IntPtr> freeCredential,
        Func<IntPtr, string?> readComment)
    {
        ArgumentNullException.ThrowIfNull(readCredential);
        ArgumentNullException.ThrowIfNull(freeCredential);
        ArgumentNullException.ThrowIfNull(readComment);

        IntPtr credentialPointer = IntPtr.Zero;
        try
        {
            bool read = readCredential(
                targetName,
                credentialType,
                out credentialPointer,
                out int errorCode);
            if (!read)
            {
                return errorCode == ErrorNotFound
                    ? new CredentialProbeResult(true, false, false, null)
                    : new CredentialProbeResult(false, false, false, $"WIN32_ERROR_{errorCode}");
            }

            if (credentialPointer == IntPtr.Zero)
            {
                return new CredentialProbeResult(false, false, false, $"WIN32_ERROR_{ErrorInvalidData}");
            }

            string? comment = readComment(credentialPointer);
            bool markerMatches = exactMarker
                ? string.Equals(comment, marker, StringComparison.Ordinal)
                : comment?.StartsWith(marker, StringComparison.Ordinal) == true;
            return new CredentialProbeResult(true, true, markerMatches, null);
        }
        finally
        {
            if (credentialPointer != IntPtr.Zero)
            {
                freeCredential(credentialPointer);
            }
        }
    }

    private static CredentialProbeResult ProbeCredential(
        string targetName,
        uint credentialType,
        string marker,
        bool exactMarker)
    {
        return ProbeCredential(
            targetName,
            credentialType,
            marker,
            exactMarker,
            (string target, uint type, out IntPtr credentialPointer, out int errorCode) =>
            {
                bool read = CredRead(target, type, 0, out credentialPointer);
                errorCode = read ? 0 : Marshal.GetLastWin32Error();
                return read;
            },
            CredFree,
            ReadCredentialComment);
    }

    private static string? ReadCredentialComment(IntPtr credentialPointer)
    {
        int typeOffset = Marshal.OffsetOf<NativeCredentialPointers>(
            nameof(NativeCredentialPointers.Type)).ToInt32();
        int targetNameOffset = Marshal.OffsetOf<NativeCredentialPointers>(
            nameof(NativeCredentialPointers.TargetName)).ToInt32();
        int userNameOffset = Marshal.OffsetOf<NativeCredentialPointers>(
            nameof(NativeCredentialPointers.UserName)).ToInt32();
        int commentOffset = Marshal.OffsetOf<NativeCredentialPointers>(
            nameof(NativeCredentialPointers.Comment)).ToInt32();

        _ = Marshal.ReadInt32(credentialPointer, typeOffset);
        _ = Marshal.PtrToStringUni(Marshal.ReadIntPtr(credentialPointer, targetNameOffset));
        _ = Marshal.PtrToStringUni(Marshal.ReadIntPtr(credentialPointer, userNameOffset));
        return Marshal.PtrToStringUni(Marshal.ReadIntPtr(credentialPointer, commentOffset));
    }

    /// <summary>
    /// Shared implementation for writing a Windows credential via CredWriteW.
    /// Validates inputs, encodes the secret, marshals to native memory, and
    /// zeroes all sensitive byte arrays in the finally block.
    /// </summary>
    private static bool WriteCredential(
        string target,
        string userName,
        string secret,
        uint credType,
        uint credPersist,
        string? comment,
        out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(target))
        {
            error = "Credential target cannot be empty.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(userName))
        {
            error = "Credential username cannot be empty.";
            return false;
        }
        if (secret is null)
        {
            error = "Credential secret cannot be null.";
            return false;
        }

        byte[]? secretBytes = null;
        IntPtr secretPtr = IntPtr.Zero;

        try
        {
            secretBytes = Encoding.Unicode.GetBytes(secret);
            if (secretBytes.Length > CredMaxCredentialBlobSize)
            {
                error = $"Credential secret exceeds {CredMaxCredentialBlobSize} bytes.";
                return false;
            }

            secretPtr = Marshal.StringToCoTaskMemUni(secret);

            NativeCredential credential = new NativeCredential
            {
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                Comment = comment,
                TargetAlias = null,
                Type = credType,
                Persist = credPersist,
                CredentialBlobSize = (uint)secretBytes.Length,
                TargetName = target,
                CredentialBlob = secretPtr,
                UserName = userName
            };

            bool written = CredWrite(ref credential, 0);
            if (!written)
            {
                error = $"WIN32_ERROR_{Marshal.GetLastWin32Error()}";
            }

            return written;
        }
        finally
        {
            // Zero and release sensitive memory
            if (secretPtr != IntPtr.Zero)
            {
                Marshal.ZeroFreeCoTaskMemUnicode(secretPtr);
            }
            if (secretBytes is not null)
            {
                Array.Clear(secretBytes, 0, secretBytes.Length);
            }
        }
    }
}
