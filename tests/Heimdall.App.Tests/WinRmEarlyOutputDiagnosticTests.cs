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

    [Theory]
    [InlineData("PowerShell 7.5.0\r\nPS C:\\> ")]
    [InlineData("[server.example]: PS C:\\Users\\operator> ")]
    public void Observe_ConfirmedPowerShellPrompt_DisablesDiagnostic(string output)
    {
        WinRmEarlyOutputDiagnostic diagnostic = new();

        string? result = diagnostic.Observe(Bytes(output));

        Assert.Null(result);
        Assert.False(diagnostic.IsActive);
        Assert.Null(diagnostic.Observe(Bytes("WinRM 0x8009030e")));
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

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
