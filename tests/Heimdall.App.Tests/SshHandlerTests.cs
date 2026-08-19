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
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

public sealed class SshHandlerTests
{
    [Fact]
    public void TryValidateKeyPath_RejectsQuoteInjection()
    {
        bool isValid = SshHandler.TryValidateKeyPath(
            "C:\\keys\\id\" --corrupt.ppk",
            out SshKeyPathValidationError error);

        Assert.False(isValid);
        Assert.Equal(SshKeyPathValidationError.InvalidCharacters, error);
    }

    [Fact]
    public void TryValidateKeyPath_RejectsRelativePath()
    {
        bool isValid = SshHandler.TryValidateKeyPath(
            "keys\\id_ed25519",
            out SshKeyPathValidationError error);

        Assert.False(isValid);
        Assert.Equal(SshKeyPathValidationError.NotAbsolute, error);
    }

    [Fact]
    public void TryValidateKeyPath_RejectsMissingAbsolutePath()
    {
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            $"heimdall-missing-key-{Guid.NewGuid():N}.ppk");

        bool isValid = SshHandler.TryValidateKeyPath(
            missingPath,
            out SshKeyPathValidationError error);

        Assert.False(isValid);
        Assert.Equal(SshKeyPathValidationError.FileNotFound, error);
    }

    [Fact]
    public void BuildPuttyStartInfo_UsesArgumentListForValidInputs()
    {
        string keyPath = Path.GetTempFileName();

        try
        {
            System.Diagnostics.ProcessStartInfo psi = SshHandler.BuildPuttyStartInfo(
                @"C:\tools\putty.exe",
                keyPath,
                compression: true,
                agentForwarding: true,
                x11Forwarding: false,
                port: 22,
                target: "user@example.com",
                hostKeyFingerprint: null);

            Assert.Equal(@"C:\tools\putty.exe", psi.FileName);
            Assert.Contains("-i", psi.ArgumentList);
            Assert.Contains(keyPath, psi.ArgumentList);
            Assert.Contains("-C", psi.ArgumentList);
            Assert.Contains("-A", psi.ArgumentList);
            Assert.Contains("user@example.com", psi.ArgumentList);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void BuildPuttyStartInfo_WithHostKey_AddsHostKeyBeforeTarget()
    {
        System.Diagnostics.ProcessStartInfo psi = SshHandler.BuildPuttyStartInfo(
            @"C:\tools\putty.exe",
            keyPath: null,
            compression: false,
            agentForwarding: false,
            x11Forwarding: false,
            port: 2222,
            target: "user@example.com",
            hostKeyFingerprint: "SHA256:abc123");

        int hostKeyIndex = psi.ArgumentList.IndexOf("-hostkey");
        int targetIndex = psi.ArgumentList.IndexOf("user@example.com");

        Assert.True(hostKeyIndex >= 0);
        Assert.Equal("SHA256:abc123", psi.ArgumentList[hostKeyIndex + 1]);
        Assert.True(targetIndex > hostKeyIndex);
        Assert.Equal(targetIndex - 2, hostKeyIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void BuildPuttyStartInfo_WithoutHostKey_OmitsHostKeyArguments(string? hostKeyFingerprint)
    {
        System.Diagnostics.ProcessStartInfo psi = SshHandler.BuildPuttyStartInfo(
            @"C:\tools\putty.exe",
            keyPath: null,
            compression: false,
            agentForwarding: false,
            x11Forwarding: false,
            port: 22,
            target: "user@example.com",
            hostKeyFingerprint: hostKeyFingerprint);

        Assert.DoesNotContain("-hostkey", psi.ArgumentList);
        Assert.Contains("user@example.com", psi.ArgumentList);
    }

    [Fact]
    public void BuildPipeModeArguments_QuotesKeyPathAndHostKey()
    {
        var escapedKeyPath = InputValidator.EscapeForDoubleQuotedString(@"C:\keys\id test.ppk");
        var arguments = SshHandler.BuildPipeModeArguments(
            @"C:\keys\id test.ppk",
            compression: true,
            agentForwarding: false,
            x11Forwarding: true,
            port: 2222,
            target: "user@example.com",
            hostKeyFingerprint: "SHA256:abc123");

        Assert.Contains($"-i \"{escapedKeyPath}\"", arguments);
        Assert.Contains("-C", arguments);
        Assert.Contains("-X", arguments);
        Assert.Contains("-hostkey \"SHA256:abc123\"", arguments);
        Assert.Contains("user@example.com", arguments);
    }

    [Fact]
    public void BuildPipeModeArguments_IncludesQuotedPasswordFilePath()
    {
        var passwordFilePath = @"C:\Temp\heimdall pw.txt";
        var escapedPasswordFilePath = InputValidator.EscapeForDoubleQuotedString(passwordFilePath);

        var arguments = SshHandler.BuildPipeModeArguments(
            keyPath: null,
            compression: false,
            agentForwarding: false,
            x11Forwarding: false,
            port: 22,
            target: "bastion@example.com",
            hostKeyFingerprint: null,
            passwordFilePath: passwordFilePath);

        Assert.Contains($"-pwfile \"{escapedPasswordFilePath}\"", arguments);
        Assert.DoesNotContain("secret", arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPipeModeArguments_PutsAllOptionsBeforeTarget()
    {
        var arguments = SshHandler.BuildPipeModeArguments(
            keyPath: null,
            compression: false,
            agentForwarding: false,
            x11Forwarding: false,
            port: 2222,
            target: "bastion@example.com",
            hostKeyFingerprint: "SHA256:abc123",
            passwordFilePath: @"C:\Temp\heimdall-pw.txt");

        var hostKeyIndex = arguments.IndexOf("-hostkey", StringComparison.Ordinal);
        var passwordFileIndex = arguments.IndexOf("-pwfile", StringComparison.Ordinal);
        var targetIndex = arguments.LastIndexOf("bastion@example.com", StringComparison.Ordinal);

        Assert.True(hostKeyIndex >= 0);
        Assert.True(passwordFileIndex >= 0);
        Assert.True(targetIndex > hostKeyIndex);
        Assert.True(targetIndex > passwordFileIndex);
        Assert.EndsWith(" bastion@example.com", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldPromptForPlinkPassword_WithPasswordFile_ReturnsFalse()
    {
        var shouldPrompt = SshHandler.ShouldPromptForPlinkPassword(
            @"C:\Temp\heimdall-pw.txt",
            keyPath: null);

        Assert.False(shouldPrompt);
    }

    [Fact]
    public void ShouldPromptForPlinkPassword_WithKeyPath_ReturnsFalse()
    {
        var shouldPrompt = SshHandler.ShouldPromptForPlinkPassword(
            password: null,
            keyPath: @"C:\keys\id_ed25519.ppk");

        Assert.False(shouldPrompt);
    }

    [Fact]
    public void ShouldPromptForPlinkPassword_WithoutPasswordOrKey_ReturnsTrue()
    {
        var shouldPrompt = SshHandler.ShouldPromptForPlinkPassword(
            password: null,
            keyPath: null);

        Assert.True(shouldPrompt);
    }
}
