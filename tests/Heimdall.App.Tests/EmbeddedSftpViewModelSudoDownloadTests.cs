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
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

public sealed class EmbeddedSftpViewModelSudoDownloadTests
{
    [Fact]
    public void BuildSudoInvocation_WithoutStdinPassword_UsesPlainSudo()
    {
        string command = EmbeddedSftpViewModel.BuildSudoInvocation(
            "base64 -- '/etc/ssh/config'",
            false);

        Assert.Equal("sudo base64 -- '/etc/ssh/config'", command);
    }

    [Fact]
    public void BuildSudoInvocation_WithStdinPassword_UsesSudoStdinMode()
    {
        string command = EmbeddedSftpViewModel.BuildSudoInvocation(
            "base64 -- '/etc/ssh/config'",
            true);

        Assert.Equal("sudo -S -p '' base64 -- '/etc/ssh/config'", command);
    }

    [Fact]
    public void BuildSudoBase64DownloadBody_EscapesRemotePath()
    {
        string command = EmbeddedSftpViewModel.BuildSudoBase64DownloadBody("/etc/ssh/it's config");

        Assert.StartsWith("sh -c ", command, StringComparison.Ordinal);
        Assert.Contains("ln -P", command, StringComparison.Ordinal);
        Assert.Contains("exec 3< source", command, StringComparison.Ordinal);
        Assert.Contains("base64 <&3", command, StringComparison.Ordinal);
        Assert.EndsWith(@"sh '/etc/ssh/it'\''s config'", command, StringComparison.Ordinal);
        Assert.DoesNotContain("base64 -- '/etc/ssh", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sudo: a terminal is required to read the password; either use the -S option to read from standard input or configure an askpass helper", nameof(SudoFailureKind.PasswordUnavailable))]
    [InlineData("sudo: no tty present and no askpass program specified", nameof(SudoFailureKind.PasswordUnavailable))]
    [InlineData("sudo: a password is required", nameof(SudoFailureKind.PasswordUnavailable))]
    [InlineData("Sorry, try again.", nameof(SudoFailureKind.PasswordRejected))]
    [InlineData("sudo: 3 incorrect password attempts", nameof(SudoFailureKind.PasswordRejected))]
    [InlineData("sudo: no password was provided", nameof(SudoFailureKind.PasswordRejected))]
    [InlineData("sudo: unable to resolve host labbox", nameof(SudoFailureKind.None))]
    [InlineData("", nameof(SudoFailureKind.None))]
    [InlineData(null, nameof(SudoFailureKind.None))]
    public void ClassifySudoStderr_ProducerClassifiesAuthenticationFailures(
        string? stderr,
        string expectedName)
    {
        SudoFailureKind expected = Enum.Parse<SudoFailureKind>(expectedName);
        SudoFailureKind actual = EmbeddedSftpViewModel.ClassifySudoStderr(stderr);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecodeSudoBase64_RoundTripsBinaryBytes()
    {
        byte[] expected = [0x00, 0xff, 0xfe, 0x80, 0x01, 0x7f, 0xc3, 0x28, 0x0a];
        string encoded = Convert.ToBase64String(expected);

        byte[] actual = EmbeddedSftpViewModel.DecodeSudoBase64(encoded);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecodeSudoBase64_ToleratesWrappedOutput()
    {
        byte[] expected = new byte[256];
        for (int index = 0; index < expected.Length; index++)
        {
            expected[index] = (byte)index;
        }

        string encoded = Convert.ToBase64String(expected);
        string wrapped = WrapEvery76Characters(encoded);

        byte[] actual = EmbeddedSftpViewModel.DecodeSudoBase64(wrapped);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecodeSudoBase64_EmptyString_ReturnsEmptyArray()
    {
        byte[] actual = EmbeddedSftpViewModel.DecodeSudoBase64(string.Empty);

        Assert.Empty(actual);
    }

    private static string WrapEvery76Characters(string input)
    {
        StringBuilder builder = new();
        for (int index = 0; index < input.Length; index += 76)
        {
            int length = Math.Min(76, input.Length - index);
            builder.Append(input, index, length);
            builder.Append('\n');
        }

        return builder.ToString();
    }
}
