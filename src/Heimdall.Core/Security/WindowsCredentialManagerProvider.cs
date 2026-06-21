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
using System.Runtime.Versioning;

namespace Heimdall.Core.Security;

/// <summary>
/// Credential provider backed by the Windows Credential Manager (generic credentials).
/// The lookup target is the entry <c>title</c> supplied by the caller (per-profile vault
/// entry name when set, otherwise the server display name). Returns both the stored
/// username and password; the credential blob is never logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerProvider : ICredentialProvider
{
    private const int CredTypeGeneric = 1;
    private const int ErrorNotFound = 1168;

    /// <inheritdoc />
    public string Name => "WindowsCredentialManager";

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public Task<CredentialResult?> GetCredentialAsync(
        string serverHost,
        int port,
        string? username,
        string? title,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(title))
        {
            Logging.FileLogger.Warn(
                "WindowsCredentialManagerProvider: no target name (vault entry name / display name) provided");
            return Task.FromResult<CredentialResult?>(null);
        }

        return Task.FromResult(ReadGenericCredential(title));
    }

    private static CredentialResult? ReadGenericCredential(string target)
    {
        if (!CredReadW(target, CredTypeGeneric, 0, out IntPtr credPtr))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                Logging.FileLogger.Warn(
                    $"WindowsCredentialManagerProvider: no credential found for target '{target}'");
            }
            else
            {
                Logging.FileLogger.Warn(
                    $"WindowsCredentialManagerProvider: CredRead failed for target '{target}' (error {error})");
            }

            return null;
        }

        try
        {
            CREDENTIAL credential = Marshal.PtrToStructure<CREDENTIAL>(credPtr);

            string userName = credential.UserName ?? string.Empty;

            string password = string.Empty;
            if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0)
            {
                // Credential Manager stores the blob as UTF-16LE bytes (no terminator guaranteed).
                byte[] blob = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, blob, 0, (int)credential.CredentialBlobSize);
                password = System.Text.Encoding.Unicode.GetString(blob);
                Array.Clear(blob, 0, blob.Length);
            }

            if (string.IsNullOrEmpty(password))
            {
                Logging.FileLogger.Warn(
                    $"WindowsCredentialManagerProvider: credential '{target}' has an empty password blob");
                return null;
            }

            return new CredentialResult(userName, password);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }
}
