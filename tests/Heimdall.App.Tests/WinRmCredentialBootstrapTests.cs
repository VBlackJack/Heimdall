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

using System.IO;
using System.Text;
using Heimdall.App.Services.WinRm;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class WinRmCredentialBootstrapTests
{
    private const string TestScriptPath = @"C:\Temp\heimdall_winrm_test.ps1";
    private const string TestBlobPath = @"C:\Temp\heimdall_winrm_test.blob";

    // Script-block logging writes the text of every script it runs to the event log. The DPAPI
    // blob is ciphertext bound to this user, but it has no business in a log that other readers
    // and forwarders see, so it lives in its own file beside the script and never in its text.
    [Fact]
    public void Write_CreatesProtectedBootstrapScriptWithoutPlaintextPassword()
    {
        Dictionary<string, string> written = new(StringComparer.Ordinal);
        WinRmCredentialBootstrap bootstrap = new WinRmCredentialBootstrap(
            createScriptPath: () => TestScriptPath,
            writeAndProtect: (path, content) => written.Add(path, content),
            unprotectStoredPasswordBytes: encrypted =>
                encrypted == "stored-password" ? Encoding.UTF8.GetBytes("p@ss'word!") : null,
            protectBootstrapPasswordBytes: bytes =>
                Encoding.UTF8.GetString(bytes) == "p@ss'word!" ? "dpapi-bootstrap-blob" : "unexpected");

        WinRmCredentialBootstrapResult result = bootstrap.Write(CreateCredentialServer());

        Assert.Equal(TestScriptPath, result.ScriptPath);
        Assert.Equal([TestBlobPath, TestScriptPath], written.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("dpapi-bootstrap-blob", written[TestBlobPath]);
        string writtenContent = written[TestScriptPath];
        Assert.DoesNotContain("dpapi-bootstrap-blob", writtenContent, StringComparison.Ordinal);
        Assert.Contains(
            "$blobPath = [System.IO.Path]::ChangeExtension($PSCommandPath, '.blob')",
            writtenContent,
            StringComparison.Ordinal);
        Assert.Contains("$blob = [System.IO.File]::ReadAllText($blobPath)", writtenContent, StringComparison.Ordinal);
        Assert.Contains("System.Security.Cryptography.ProtectedData", writtenContent, StringComparison.Ordinal);
        Assert.Contains("[System.Security.Cryptography.ProtectedData]::Unprotect", writtenContent, StringComparison.Ordinal);
        Assert.Contains("[System.Management.Automation.PSCredential]::new('CONTOSO\\operator'", writtenContent, StringComparison.Ordinal);
        Assert.Contains("Enter-PSSession -ComputerName 'server01.contoso.local' -Port 5986 -Authentication Negotiate -UseSSL -Credential $credential", writtenContent, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $scriptPath -Force", writtenContent, StringComparison.Ordinal);
        Assert.True(
            writtenContent.IndexOf("Remove-Item -LiteralPath $scriptPath", StringComparison.Ordinal)
            < writtenContent.IndexOf("Enter-PSSession", StringComparison.Ordinal));
        Assert.DoesNotContain("ConvertTo-SecureString", writtenContent, StringComparison.Ordinal);
        Assert.DoesNotContain("$plainPassword", writtenContent, StringComparison.Ordinal);
        Assert.Contains("New-Object System.Security.SecureString", writtenContent, StringComparison.Ordinal);
        Assert.Contains("AppendChar", writtenContent, StringComparison.Ordinal);
        Assert.DoesNotContain("p@ss'word!", writtenContent, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-password", writtenContent, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScript_ReadsTheBlobFromItsOwnFileAndRemovesIt()
    {
        ServerProfileDto server = CreateCredentialServer();

        string script = WinRmCredentialBootstrap.BuildScript(server);

        int read = script.IndexOf("try { $blob = [System.IO.File]::ReadAllText($blobPath) }", StringComparison.Ordinal);
        int removed = script.IndexOf(
            "finally { Remove-Item -LiteralPath $blobPath -Force -ErrorAction SilentlyContinue }",
            StringComparison.Ordinal);
        int decrypted = script.IndexOf("[Convert]::FromBase64String($blob)", StringComparison.Ordinal);
        Assert.True(read >= 0);
        Assert.True(removed > read);
        Assert.True(decrypted > removed);
        Assert.Contains(@"[System.Management.Automation.PSCredential]::new('CONTOSO\operator'", script, StringComparison.Ordinal);
        Assert.Contains("-ComputerName 'server01.contoso.local'", script, StringComparison.Ordinal);
        Assert.Contains("-Authentication Negotiate", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScript_SetsTheEnteredFlagOnlyAfterEnterPSSession()
    {
        ServerProfileDto server = CreateCredentialServer();

        string script = WinRmCredentialBootstrap.BuildScript(server);
        string[] lines = script.Split("\r\n");

        // The local prompt guard is defined by the launch command, before this script loads.
        // Here the entered flag is set only on the line after Enter-PSSession, inside the try,
        // under a Stop preference, so any failure before it leaves the flag false.
        // WinRmLaunchExitGuardExecutionTests runs this script in a real host.
        Assert.Equal("$ErrorActionPreference = 'Stop'", lines[0]);
        int enterLine = Array.FindIndex(lines, line => line.TrimStart().StartsWith("Enter-PSSession ", StringComparison.Ordinal));
        Assert.True(enterLine > 1);
        Assert.Equal("    $global:HeimdallWinRmEntered = $true", lines[enterLine + 1]);
        Assert.Equal("}", lines[enterLine + 2]);
    }

    [Fact]
    public void BuildScript_ClearsDpapiBlobInFinallyBlock()
    {
        ServerProfileDto server = CreateCredentialServer();

        string script = WinRmCredentialBootstrap.BuildScript(server);

        int finallyStart = script.IndexOf("finally {\r\n", StringComparison.Ordinal);
        int blobClearIndex = script.IndexOf("    $blob = $null", StringComparison.Ordinal);
        int finallyEnd = script.IndexOf("\r\n}", finallyStart, StringComparison.Ordinal);

        Assert.True(finallyStart >= 0);
        Assert.True(blobClearIndex > finallyStart);
        Assert.True(finallyEnd > blobClearIndex);
    }

    [Fact]
    public void Write_WhenStoredPasswordCannotBeDecrypted_Throws()
    {
        WinRmCredentialBootstrap bootstrap = new WinRmCredentialBootstrap(
            createScriptPath: () => @"C:\Temp\unused.ps1",
            writeAndProtect: (_, _) => throw new InvalidOperationException("should not write"),
            unprotectStoredPasswordBytes: _ => null,
            protectBootstrapPasswordBytes: bytes => Encoding.UTF8.GetString(bytes));

        WinRmConfigurationException ex = Assert.Throws<WinRmConfigurationException>(
            () => bootstrap.Write(CreateCredentialServer()));

        Assert.Equal("ErrorWinRmCredentialUnavailable", ex.LocalizationKey);
    }

    [Fact]
    public void Write_WithCurrentUserIdentity_Throws()
    {
        WinRmCredentialBootstrap bootstrap = new WinRmCredentialBootstrap(
            createScriptPath: () => @"C:\Temp\unused.ps1",
            writeAndProtect: (_, _) => throw new InvalidOperationException("should not write"),
            unprotectStoredPasswordBytes: _ => Encoding.UTF8.GetBytes("secret"),
            protectBootstrapPasswordBytes: bytes => Encoding.UTF8.GetString(bytes));
        ServerProfileDto server = CreateCredentialServer();
        server.WinRmIdentityMode = WinRmIdentityMode.CurrentUser;

        WinRmConfigurationException ex =
            Assert.Throws<WinRmConfigurationException>(() => bootstrap.Write(server));

        Assert.Equal("ErrorWinRmProfileInvalid", ex.LocalizationKey);
    }

    [Fact]
    public void Write_ZeroesPlaintextBytesAfterReturning()
    {
        byte[] capturedBytes = Encoding.UTF8.GetBytes("p@ss'word!");
        WinRmCredentialBootstrap bootstrap = new WinRmCredentialBootstrap(
            createScriptPath: () => @"C:\Temp\heimdall_winrm_zero_test.ps1",
            writeAndProtect: (_, _) => { },
            unprotectStoredPasswordBytes: _ => capturedBytes,
            protectBootstrapPasswordBytes: _ => "dpapi-blob");

        bootstrap.Write(CreateCredentialServer());

        Assert.All(capturedBytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void CreateDefaultScriptPath_UsesHeimdallWinRmTempPattern()
    {
        string scriptPath = WinRmCredentialBootstrap.CreateDefaultScriptPath();

        Assert.StartsWith(Path.GetTempPath(), scriptPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("heimdall_winrm_", Path.GetFileName(scriptPath), StringComparison.Ordinal);
        Assert.EndsWith(".ps1", scriptPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Delete_RemovesExistingFile()
    {
        string scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"heimdall_winrm_delete_{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, "bootstrap");
        WinRmCredentialBootstrap bootstrap = new WinRmCredentialBootstrap();

        bootstrap.Delete(scriptPath);

        Assert.False(File.Exists(scriptPath));
    }

    [Fact]
    public void Delete_RemovesTheBlobBesideTheScript()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"heimdall_winrm_blob_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string scriptPath = Path.Combine(directory, "heimdall_winrm_x.ps1");
            string blobPath = Path.Combine(directory, "heimdall_winrm_x.blob");
            File.WriteAllText(scriptPath, "bootstrap");
            File.WriteAllText(blobPath, "blob");

            new WinRmCredentialBootstrap().Delete(scriptPath);

            Assert.False(File.Exists(scriptPath));
            Assert.False(File.Exists(blobPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Write_WhenTheScriptCannotBeWritten_RemovesTheBlobItWrote()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"heimdall_winrm_blob_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string scriptPath = Path.Combine(directory, "heimdall_winrm_x.ps1");
            List<string> writtenPaths = [];
            WinRmCredentialBootstrap bootstrap = new WinRmCredentialBootstrap(
                createScriptPath: () => scriptPath,
                writeAndProtect: (path, content) =>
                {
                    writtenPaths.Add(path);
                    if (path.EndsWith(".ps1", StringComparison.Ordinal))
                    {
                        throw new IOException("disk full");
                    }

                    File.WriteAllText(path, content);
                },
                unprotectStoredPasswordBytes: _ => Encoding.UTF8.GetBytes("secret"),
                protectBootstrapPasswordBytes: _ => "dpapi-blob");

            Assert.Throws<IOException>(() => bootstrap.Write(CreateCredentialServer()));

            string blobPath = Path.Combine(directory, "heimdall_winrm_x.blob");
            Assert.Contains(blobPath, writtenPaths);
            Assert.False(File.Exists(blobPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Delete_WhenFileIsMissing_DoesNotThrow()
    {
        string scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"heimdall_winrm_missing_{Guid.NewGuid():N}.ps1");
        WinRmCredentialBootstrap bootstrap = new WinRmCredentialBootstrap();

        bootstrap.Delete(scriptPath);
    }

    private static ServerProfileDto CreateCredentialServer()
        => new ServerProfileDto
        {
            ConnectionType = "WINRM",
            RemoteServer = "server01.contoso.local",
            WinRmPort = DefaultPorts.WinRmHttps,
            WinRmUseSsl = true,
            WinRmIdentityMode = WinRmIdentityMode.Credential,
            WinRmUsername = @"CONTOSO\operator",
            WinRmPasswordEncrypted = "stored-password"
        };
}
