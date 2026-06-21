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
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

/// <summary>
/// Round-trips a temporary generic credential through the real Windows Credential
/// Manager to genuinely exercise the P/Invoke. Each test uses a unique target and
/// deletes it in a finally so the store is left clean.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsCredentialManagerProviderTests
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistSession = 1;

    [Fact]
    public async Task GetCredentialAsync_ExistingEntry_ReturnsUsernameAndPassword()
    {
        string target = $"Heimdall-Test-{Guid.NewGuid():N}";
        const string ExpectedUser = "vault-user";
        const string ExpectedPassword = "s3cr3t-p@ss-éè";

        WriteGenericCredential(target, ExpectedUser, ExpectedPassword);
        try
        {
            var provider = new WindowsCredentialManagerProvider();

            var result = await provider.GetCredentialAsync(
                "host.example.com", 22, username: null, title: target);

            Assert.NotNull(result);
            Assert.Equal(ExpectedUser, result!.Username);
            Assert.Equal(ExpectedPassword, result.Password);
        }
        finally
        {
            CredDelete(target, CredTypeGeneric, 0);
        }
    }

    [Fact]
    public async Task GetCredentialAsync_MissingEntry_ReturnsNull()
    {
        string missingTarget = $"Heimdall-Test-Missing-{Guid.NewGuid():N}";
        var provider = new WindowsCredentialManagerProvider();

        var result = await provider.GetCredentialAsync(
            "host.example.com", 22, username: null, title: missingTarget);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCredentialAsync_NoTitle_ReturnsNull()
    {
        var provider = new WindowsCredentialManagerProvider();

        var result = await provider.GetCredentialAsync(
            "host.example.com", 22, username: null, title: "   ");

        Assert.Null(result);
    }

    private static void WriteGenericCredential(string target, string userName, string password)
    {
        byte[] blob = System.Text.Encoding.Unicode.GetBytes(password);
        IntPtr blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var credential = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = userName,
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)blob.Length,
                Persist = CredPersistSession
            };

            bool written = CredWrite(ref credential, 0);
            Assert.True(written, $"CredWriteW failed (error {Marshal.GetLastWin32Error()})");
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
}
