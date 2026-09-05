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
    private const char OwnershipMarkerFieldSeparator = ':';
    private const int OwnershipMarkerFieldCount = 3;

    /// <summary>
    /// Window during which a marker written by this process still describes a launch that
    /// may not have read the credential yet. Above the 60 s ceiling the settings schema
    /// allows for the RDP artifact cleanup delay, so a launch whose deferred cleanup is
    /// still pending is never mistaken for an abandoned entry.
    /// </summary>
    internal static readonly TimeSpan LiveLaunchMarkerWindow = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Enumeration filter matching every credential the RDP client reads for a host.
    /// </summary>
    internal const string RdpCredentialTargetFilter = "TERMSRV/*";

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

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(
        string? filter,
        uint flags,
        out uint count,
        out IntPtr credentials);

    #endregion

    #region Stale credential sweep

    /// <summary>
    /// What the sweep needs to know about one stored credential: enough to decide, and no secret.
    /// </summary>
    internal readonly record struct StoredCredentialSummary(string TargetName, uint Type, string? Comment);

    /// <summary>
    /// Lists the credentials matching a target filter. Returns false with a Win32 error code when
    /// the enumeration itself failed; an empty list when nothing matched.
    /// </summary>
    internal delegate bool CredentialEnumerateOperation(
        string filter,
        out IReadOnlyList<StoredCredentialSummary> credentials,
        out int errorCode);

    /// <summary>
    /// Removes the <c>TERMSRV/*</c> entries an earlier launch wrote and never cleaned up.
    /// </summary>
    /// <remarks>
    /// The deferred cleanup that follows an external launch only runs while the process that
    /// scheduled it lives. A crash, a kill or an exit inside that window strands the entry, and
    /// a stranded <c>CRED_PERSIST_SESSION</c> entry stays readable through <c>CredRead</c> until
    /// the Windows session ends. The temporary .rdp files have had a janitor for this since
    /// v2026.090201; this is the same janitor for the half that carries the password.
    /// </remarks>
    /// <returns>The number of entries deleted.</returns>
    public static int SweepStaleOwnedCredentials(Action<string>? warn = null)
    {
        return SweepStaleOwnedCredentials(
            DateTime.UtcNow,
            EnumerateCredentials,
            (target, type) =>
            {
                bool deleted = CredDelete(target, type, 0);
                return new CredentialDeleteResult(
                    deleted,
                    deleted ? 0 : Marshal.GetLastWin32Error());
            },
            warn);
    }

    internal static int SweepStaleOwnedCredentials(
        DateTime utcNow,
        CredentialEnumerateOperation enumerateCredentials,
        Func<string, uint, CredentialDeleteResult> deleteCredential,
        Action<string>? warn)
    {
        ArgumentNullException.ThrowIfNull(enumerateCredentials);
        ArgumentNullException.ThrowIfNull(deleteCredential);

        if (!enumerateCredentials(
                RdpCredentialTargetFilter,
                out IReadOnlyList<StoredCredentialSummary> credentials,
                out int errorCode))
        {
            if (errorCode != ErrorNotFound)
            {
                warn?.Invoke($"Stale RDP credential sweep: enumeration failed with WIN32_ERROR_{errorCode}");
            }

            return 0;
        }

        int deleted = 0;
        foreach (StoredCredentialSummary credential in credentials)
        {
            if (credential.Type != CredTypeDomainPassword
                || !IsStaleOwnedMarker(credential.Comment, utcNow))
            {
                continue;
            }

            CredentialDeleteResult result = deleteCredential(credential.TargetName, credential.Type);
            if (result.Success)
            {
                deleted++;
            }
            else if (result.ErrorCode != ErrorNotFound)
            {
                // The target is a host name, which is not a secret; the comment and the blob
                // are never logged.
                warn?.Invoke(
                    $"Stale RDP credential sweep: could not delete '{credential.TargetName}': WIN32_ERROR_{result.ErrorCode}");
            }
        }

        return deleted;
    }

    /// <summary>
    /// Decides whether a stored ownership marker describes a launch that can no longer be
    /// waiting on the credential, whichever process wrote it.
    /// </summary>
    /// <remarks>
    /// <para>Only a marker carrying a timestamp can be judged. The pre-existing single-field
    /// format has none, and deleting it on the strength of its prefix alone would reach into a
    /// launch an older build may still be running; those entries are left to the next launch to
    /// the same host, which overwrites them as it always has.</para>
    /// <para>The process identifier is deliberately not compared. A marker older than the live
    /// window belongs to a cleanup that has either run or been lost, and the window is above
    /// the longest cleanup delay the settings schema allows, so this process's own pending
    /// cleanup is never raced.</para>
    /// </remarks>
    internal static bool IsStaleOwnedMarker(string? marker, DateTime utcNow)
    {
        if (marker is null ||
            !marker.StartsWith(DomainCredentialOwnershipPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] fields = marker[DomainCredentialOwnershipPrefix.Length..]
            .Split(OwnershipMarkerFieldSeparator);
        if (fields.Length != OwnershipMarkerFieldCount)
        {
            return false;
        }

        if (!long.TryParse(
                fields[1],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out long ticks) ||
            ticks < 0 ||
            ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        TimeSpan age = utcNow - new DateTime(ticks, DateTimeKind.Utc);
        return age >= LiveLaunchMarkerWindow;
    }

    private static bool EnumerateCredentials(
        string filter,
        out IReadOnlyList<StoredCredentialSummary> credentials,
        out int errorCode)
    {
        credentials = [];
        errorCode = 0;

        IntPtr credentialArray = IntPtr.Zero;
        try
        {
            if (!CredEnumerate(filter, 0, out uint count, out credentialArray))
            {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }

            List<StoredCredentialSummary> summaries = new((int)count);
            for (int index = 0; index < count; index++)
            {
                IntPtr credentialPointer = Marshal.ReadIntPtr(credentialArray, index * IntPtr.Size);
                if (credentialPointer == IntPtr.Zero)
                {
                    continue;
                }

                summaries.Add(ReadCredentialSummary(credentialPointer));
            }

            credentials = summaries;
            return true;
        }
        finally
        {
            if (credentialArray != IntPtr.Zero)
            {
                CredFree(credentialArray);
            }
        }
    }

    private static StoredCredentialSummary ReadCredentialSummary(IntPtr credentialPointer)
    {
        int typeOffset = Marshal.OffsetOf<NativeCredentialPointers>(
            nameof(NativeCredentialPointers.Type)).ToInt32();
        int targetNameOffset = Marshal.OffsetOf<NativeCredentialPointers>(
            nameof(NativeCredentialPointers.TargetName)).ToInt32();
        int commentOffset = Marshal.OffsetOf<NativeCredentialPointers>(
            nameof(NativeCredentialPointers.Comment)).ToInt32();

        uint type = unchecked((uint)Marshal.ReadInt32(credentialPointer, typeOffset));
        string targetName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(credentialPointer, targetNameOffset))
            ?? string.Empty;
        string? comment = Marshal.PtrToStringUni(Marshal.ReadIntPtr(credentialPointer, commentOffset));
        return new StoredCredentialSummary(targetName, type, comment);
    }

    #endregion

    /// <summary>
    /// Creates a per-launch marker used to prove ownership of an RDP credential. The marker
    /// carries the writing process and the instant of the write so a later launch can tell
    /// an abandoned Heimdall entry from one a launch still in flight depends on.
    /// </summary>
    public static string CreateDomainCredentialOwnershipMarker()
    {
        return CreateDomainCredentialOwnershipMarker(Environment.ProcessId, DateTime.UtcNow);
    }

    internal static string CreateDomainCredentialOwnershipMarker(int processId, DateTime utcNow)
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DomainCredentialOwnershipPrefix}{processId}{OwnershipMarkerFieldSeparator}{utcNow.Ticks}{OwnershipMarkerFieldSeparator}{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Decides whether a stored ownership marker still describes a launch of this very
    /// process that may not have consumed the credential yet. Markers written by another
    /// process - including an earlier, crashed Heimdall - and markers in the pre-existing
    /// single-field format are reported as reclaimable, which preserves the previous
    /// behaviour for every case except two overlapping launches of the running instance.
    /// </summary>
    internal static bool IsLiveLaunchMarker(string? marker, int currentProcessId, DateTime utcNow)
    {
        if (marker is null ||
            !marker.StartsWith(DomainCredentialOwnershipPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] fields = marker[DomainCredentialOwnershipPrefix.Length..]
            .Split(OwnershipMarkerFieldSeparator);
        if (fields.Length != OwnershipMarkerFieldCount)
        {
            return false;
        }

        if (!int.TryParse(
                fields[0],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int processId) ||
            processId != currentProcessId)
        {
            return false;
        }

        if (!long.TryParse(
                fields[1],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out long ticks) ||
            ticks < 0 ||
            ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        TimeSpan age = utcNow - new DateTime(ticks, DateTimeKind.Utc);
        return age >= TimeSpan.Zero && age < LiveLaunchMarkerWindow;
    }

    /// <summary>
    /// Stores a DOMAIN_PASSWORD credential (CRED_TYPE=2) recognized by mstsc.exe / RDP
    /// only when the target is absent or already carries a Heimdall ownership marker.
    /// Target must follow the TERMSRV/host format for RDP auto-login.
    /// Persist is set to Session - credential lives only until logoff and is cleaned
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
        return WriteDomainCredential(
            targetName,
            username,
            password,
            ownershipMarker,
            probeCredential,
            writeCredential,
            Environment.ProcessId,
            DateTime.UtcNow,
            out credentialWritten,
            out error);
    }

    internal static bool WriteDomainCredential(
        string targetName,
        string username,
        string password,
        string ownershipMarker,
        Func<string, string, CredentialProbeResult> probeCredential,
        CredentialWriteOperation writeCredential,
        int currentProcessId,
        DateTime utcNow,
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

        if (probe.Exists && IsLiveLaunchMarker(probe.Comment, currentProcessId, utcNow))
        {
            // The entry belongs to a launch of this process that may not have read it yet.
            // Overwriting it would hand that session this profile's account instead of its
            // own, so treat it exactly like a foreign entry and leave it in place.
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
        string? Error,
        string? Comment = null);

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
            return new CredentialProbeResult(true, true, markerMatches, null, comment);
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
