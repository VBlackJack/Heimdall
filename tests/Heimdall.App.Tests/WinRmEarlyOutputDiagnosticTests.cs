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

using System.Text;
using Heimdall.App.Services.WinRm;

namespace Heimdall.App.Tests;

public sealed class WinRmEarlyOutputDiagnosticTests
{
    [Fact]
    public void Observe_NtlmLoopbackCode_ReturnsKeyAndDisables()
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes("Enter-PSSession : 0x8009030e"));

        Assert.Equal("ErrorWinRmNtlmLoopback", result);
        Assert.False(diagnostic.IsActive);
        Assert.Null(diagnostic.Observe(Bytes("0x8009030e")));
    }

    [Fact]
    public void Observe_NtlmLoopbackCodeSplitAcrossChunks_ReturnsKey()
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        Assert.Null(diagnostic.Observe(Bytes("Enter-PSSession : 0x8009")));
        string? result = diagnostic.Observe(Bytes("030e"));

        Assert.Equal("ErrorWinRmNtlmLoopback", result);
        Assert.False(diagnostic.IsActive);
    }

    [Fact]
    public void Observe_NtlmCodeWithoutWinRmContext_DoesNotMatch()
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes("Process returned 0x8009030e."));

        Assert.Null(result);
        Assert.True(diagnostic.IsActive);
    }

    [Fact]
    public void Observe_WsMan12152WithContext_ReturnsKey()
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes(
            "WinRM cannot process the request. WSMan provider returned error 12152."));

        Assert.Equal("ErrorWinRmWsmanInvalidResponse", result);
        Assert.False(diagnostic.IsActive);
    }

    [Fact]
    public void Observe_WsMan12152WithoutContext_DoesNotMatch()
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes("Process 12152 completed."));

        Assert.Null(result);
        Assert.True(diagnostic.IsActive);
    }

    // A remote prompt is the only proof the session was entered. The local "PS C:\>" used to
    // count too, and it is exactly what the host printed after a failed Enter-PSSession.
    [Theory]
    [InlineData("[server.example]: PS C:\\Users\\operator> ")]
    [InlineData("[server.example] : PS C:\\Users\\operator> ")]
    public void Observe_RemotePowerShellPrompt_DisablesDiagnostic(string output)
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes(output));

        Assert.Null(result);
        Assert.False(diagnostic.IsActive);
        Assert.Null(diagnostic.Observe(Bytes("WinRM 0x8009030e")));
    }

    [Fact]
    public void Observe_LocalPowerShellPrompt_StaysActive()
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes("PowerShell 7.5.0\r\nPS C:\\> "));

        Assert.Null(result);
        Assert.True(diagnostic.IsActive);
    }

    [Theory]
    [InlineData("PS C:\\Users\\operator> ")]
    [InlineData("[server.example]: PS C:\\Users\\operator> ")]
    public void Observe_ErrorAndPromptInOneChunk_ReturnsKey(string prompt)
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes(
            "Enter-PSSession : Connecting to remote server failed: WinRM cannot process the request. "
            + "Error code 0x8009030e occurred while using Negotiate authentication.\r\n"
            + prompt));

        Assert.Equal("ErrorWinRmNtlmLoopback", result);
        Assert.False(diagnostic.IsActive);
    }

    [Fact]
    public void Observe_ErrorAfterLocalPromptChunk_ReturnsKey()
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        Assert.Null(diagnostic.Observe(Bytes("PS C:\\Users\\operator> ")));
        string? result = diagnostic.Observe(Bytes("Enter-PSSession : WinRM 0x8009030e"));

        Assert.Equal("ErrorWinRmNtlmLoopback", result);
    }

    // The refusal names its help topic, untranslated, in every language (measured on a French
    // Windows PowerShell 5.1: "voir la rubrique about_Execution_Policies").
    [Theory]
    [InlineData("File C:\\Temp\\heimdall_winrm_x.ps1 cannot be loaded. The file is not digitally signed. "
        + "For more information, see about_Execution_Policies at https://go.microsoft.com/fwlink/?LinkID=135170.")]
    [InlineData("Impossible de charger le fichier C:\\Temp\\heimdall_winrm_x.ps1. Pour plus d'informations, "
        + "voir la rubrique about_Execution_Policies.\r\n    + FullyQualifiedErrorId : UnauthorizedAccess")]
    public void Observe_ExecutionPolicyRefusal_ReturnsKey(string output)
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes(output));

        Assert.Equal("ErrorWinRmExecutionPolicyBlocked", result);
        Assert.False(diagnostic.IsActive);
    }

    [Fact]
    public void Observe_CleanBannerWithoutPrompt_StaysActive()
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes("PowerShell 7.5.0\r\nCopyright Microsoft Corporation."));

        Assert.Null(result);
        Assert.True(diagnostic.IsActive);
    }

    [Fact]
    public void MarkUserInput_DisablesDiagnostic()
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        diagnostic.MarkUserInput();

        Assert.False(diagnostic.IsActive);
        Assert.Null(diagnostic.Observe(Bytes("WinRM 0x8009030e")));
    }

    [Fact]
    public void Observe_ExceedingCapWithoutMatch_Disables()
    {
        WinRmEarlyOutputDiagnostic diagnostic = new(maxBufferedBytes: 8);

        Assert.Null(diagnostic.Observe(Bytes("abcdefgh")));
        Assert.True(diagnostic.IsActive);

        Assert.Null(diagnostic.Observe(Bytes("i 0x8009030e")));
        Assert.False(diagnostic.IsActive);
    }

    [Theory]
    [InlineData("Enter-PSSession : The WinRM client cannot process the request. Add the destination to the TrustedHosts configuration setting.", "ErrorWinRmTrustedHosts")]
    [InlineData("Enter-PSSession : WinRM error 0x803381A1 while connecting.", "ErrorWinRmTrustedHosts")]
    [InlineData("Enter-PSSession : WinRM Kerberos error 0x80090322 occurred.", "ErrorWinRmKerberosPrincipal")]
    [InlineData("Enter-PSSession : WinRM connection failed. 0x80070005 Access is denied.", "ErrorWinRmAccessDenied")]
    [InlineData("Enter-PSSession : WinRM logon failure 0x8009030C.", "ErrorWinRmLogonFailed")]
    [InlineData("Enter-PSSession : WSMan returned 0x8007052e.", "ErrorWinRmLogonFailed")]
    public void Observe_AuthenticationFailureToken_ReturnsActionableKey(string output, string expectedKey)
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes(output));

        Assert.Equal(expectedKey, result);
        Assert.False(diagnostic.IsActive);
    }

    [Theory]
    [InlineData("Process returned 0x80090322.")]
    [InlineData("Copy failed: 0x80070005 Access is denied.")]
    [InlineData("See the TrustedHosts article in the wiki.")]
    public void Observe_AuthenticationTokenWithoutWinRmContext_DoesNotMatch(string output)
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        Assert.Null(diagnostic.Observe(Bytes(output)));
        Assert.True(diagnostic.IsActive);
    }

    [Fact]
    public void Observe_TrustedHostsRefusalMentioningKerberos_PrefersTrustedHostsKey()
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes(
            "Enter-PSSession : WinRM Kerberos 0x80090322 ... add the host to TrustedHosts."));

        Assert.Equal("ErrorWinRmTrustedHosts", result);
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
