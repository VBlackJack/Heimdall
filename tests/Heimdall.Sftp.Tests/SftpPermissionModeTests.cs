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

using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The special bits used to be invisible: a 4755 binary read as 755, the chmod dialog offered
/// 755 and refused anything above 777 because it parsed the octal text as a decimal.
/// </summary>
public sealed class SftpPermissionModeTests
{
    [Theory]
    [InlineData(0b111_101_101, "rwxr-xr-x")]
    [InlineData(0b110_100_100, "rw-r--r--")]
    [InlineData(0, "---------")]
    [InlineData(SftpPermissionMode.SetUid | 0b111_101_101, "rwsr-xr-x")]
    [InlineData(SftpPermissionMode.SetUid | 0b110_100_100, "rwSr--r--")]
    [InlineData(SftpPermissionMode.SetGid | 0b111_111_101, "rwxrwsr-x")]
    [InlineData(SftpPermissionMode.SetGid | 0b111_100_101, "rwxr-Sr-x")]
    [InlineData(SftpPermissionMode.Sticky | 0b111_111_111, "rwxrwxrwt")]
    [InlineData(SftpPermissionMode.Sticky | 0b111_111_110, "rwxrwxrwT")]
    public void FormatSymbolic_RendersTheSpecialBits(int mode, string expected)
    {
        Assert.Equal(expected, SftpPermissionMode.FormatSymbolic(mode));
    }

    [Theory]
    [InlineData("rwxr-xr-x", 0b111_101_101)]
    [InlineData("rwsr-xr-x", SftpPermissionMode.SetUid | 0b111_101_101)]
    [InlineData("rwSr--r--", SftpPermissionMode.SetUid | 0b110_100_100)]
    [InlineData("rwxrwsr-x", SftpPermissionMode.SetGid | 0b111_111_101)]
    [InlineData("rwxrwxrwt", SftpPermissionMode.Sticky | 0b111_111_111)]
    [InlineData("rwxrwxrwT", SftpPermissionMode.Sticky | 0b111_111_110)]
    public void TryParseSymbolic_ReadsTheSpecialBitsBack(string symbolic, int expected)
    {
        Assert.True(SftpPermissionMode.TryParseSymbolic(symbolic, out int mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rwx")]
    [InlineData(null)]
    public void TryParseSymbolic_RefusesAnythingButNineCharacters(string? symbolic)
    {
        Assert.False(SftpPermissionMode.TryParseSymbolic(symbolic, out _));
    }

    [Theory]
    [InlineData(0b111_101_101, "755")]
    [InlineData(0, "000")]
    [InlineData(SftpPermissionMode.SetUid | 0b111_101_101, "4755")]
    [InlineData(SftpPermissionMode.Sticky | 0b111_111_111, "1777")]
    [InlineData(SftpPermissionMode.SetUid | SftpPermissionMode.SetGid | SftpPermissionMode.Sticky | 0b111_111_111, "7777")]
    public void ToOctalString_ShowsAFourthDigitOnlyForSpecialBits(int mode, string expected)
    {
        Assert.Equal(expected, SftpPermissionMode.ToOctalString(mode));
    }

    [Theory]
    [InlineData(0b111_101_101, 755)]
    [InlineData(SftpPermissionMode.SetUid | 0b111_101_101, 4755)]
    [InlineData(SftpPermissionMode.Sticky | 0b111_111_111, 1777)]
    public void ToOctalCoded_ProducesTheNumberSshNetTakes(int mode, short expected)
    {
        Assert.Equal(expected, SftpPermissionMode.ToOctalCoded(mode));
    }

    [Theory]
    [InlineData("755", 0b111_101_101)]
    [InlineData("4755", SftpPermissionMode.SetUid | 0b111_101_101)]
    [InlineData("1777", SftpPermissionMode.Sticky | 0b111_111_111)]
    [InlineData("0", 0)]
    public void TryParseOctal_AcceptsUpToFourOctalDigits(string text, int expected)
    {
        Assert.True(SftpPermissionMode.TryParseOctal(text, out short mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("778")]
    [InlineData("77777")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("7a5")]
    [InlineData("-755")]
    public void TryParseOctal_RefusesAnythingElse(string? text)
    {
        Assert.False(SftpPermissionMode.TryParseOctal(text, out _));
    }
}
