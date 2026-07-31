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
    [Fact]
    public void Write_CreatesProtectedBootstrapScriptWithoutPlaintextPassword()
    {
        string? writtenPath = null;
        string? writtenContent = null;
        WinRmCredentialBootstrap bootstrap = new WinRmCredentialBootstrap(
            createScriptPath: () => @"C:\Temp\heimdall_winrm_test.ps1",
            writeAndProtect: (path, content) =>
            {
                writtenPath = path;
                writtenContent = content;
            },
            unprotectStoredPasswordBytes: encrypted =>
                encrypted == "stored-password" ? Encoding.UTF8.GetBytes("p@ss'word!") : null,
            protectBootstrapPasswordBytes: bytes =>
                Encoding.UTF8.GetString(bytes) == "p@ss'word!" ? "dpapi-bootstrap-blob" : "unexpected");

        WinRmCredentialBootstrapResult result = bootstrap.Write(CreateCredentialServer());

        Assert.Equal(@"C:\Temp\heimdall_winrm_test.ps1", result.ScriptPath);
        Assert.Equal(result.ScriptPath, writtenPath);
        Assert.NotNull(writtenContent);
        Assert.Contains("$blob = 'dpapi-bootstrap-blob'", writtenContent, StringComparison.Ordinal);
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
    public void BuildScript_QuotesDpapiBlobForPowerShell()
    {
        ServerProfileDto server = CreateCredentialServer();

        string script = WinRmCredentialBootstrap.BuildScript(server, "blob'value");

        Assert.Contains("$blob = 'blob''value'", script, StringComparison.Ordinal);
        Assert.Contains("[System.Management.Automation.PSCredential]::new('CONTOSO\\operator'", script, StringComparison.Ordinal);
        Assert.Contains("-ComputerName 'server01.contoso.local'", script, StringComparison.Ordinal);
        Assert.Contains("-Authentication Negotiate", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScript_ClearsDpapiBlobInFinallyBlock()
    {
        ServerProfileDto server = CreateCredentialServer();

        string script = WinRmCredentialBootstrap.BuildScript(server, "dpapi-bootstrap-blob");

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

        Assert.Throws<InvalidOperationException>(() => bootstrap.Write(CreateCredentialServer()));
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
