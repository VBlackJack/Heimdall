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

using Heimdall.App.Services;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

public sealed class TerminalCommandFormatterTests
{
    [Fact]
    public void FormatRemoteCd_SingleQuotesPath_NeutralizesInjection()
    {
        string path = "x';id;'";

        string command = TerminalCommandFormatter.FormatRemoteCd(path);

        Assert.StartsWith("cd '", command, StringComparison.Ordinal);
        Assert.Equal("cd " + InputValidator.EscapeShellArg(path), command);
        Assert.DoesNotContain('\n', command);
    }

    [Fact]
    public void FormatRemoteCd_StripsNewline()
    {
        string command = TerminalCommandFormatter.FormatRemoteCd("a\nrm -rf ~");

        Assert.DoesNotContain('\n', command);
        Assert.Equal("cd " + InputValidator.EscapeShellArg("arm -rf ~"), command);
        Assert.Contains("rm -rf ~", command, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCd_PowerShell_DoublesSingleQuoteAndKeepsBackslash()
    {
        string command = TerminalCommandFormatter.FormatCd("powershell.exe", @"C:\Temp\o'brien");

        Assert.Equal("cd 'C:\\Temp\\o''brien'\r", command);
    }

    [Fact]
    public void FormatCd_PowerShell_NeutralizesDoubleQuoteInjection()
    {
        string path = "x\";calc;\"";
        string expected = "cd '" + path.Replace("'", "''", StringComparison.Ordinal) + "'\r";

        string command = TerminalCommandFormatter.FormatCd("powershell.exe", path);

        Assert.Equal(expected, command);
    }

    [Fact]
    public void FormatCd_Cmd_StripsEmbeddedDoubleQuote()
    {
        string command = TerminalCommandFormatter.FormatCd("cmd.exe", "x\";dir;\"");

        Assert.Equal("cd /d \"x;dir;\"\r", command);
    }

    [Fact]
    public void FormatCd_Bash_UsesSingleQuoteEscaping()
    {
        string path = "x';id";

        string command = TerminalCommandFormatter.FormatCd("/bin/bash", path);

        Assert.Equal("cd " + InputValidator.EscapeShellArg(path) + "\r", command);
    }

    [Fact]
    public void FormatRun_PowerShell_PrefixesAmpersand()
    {
        string command = TerminalCommandFormatter.FormatRun("powershell.exe", @"C:\Tools\a b.exe");

        Assert.Equal("& 'C:\\Tools\\a b.exe'\r", command);
    }

    [Fact]
    public void FormatCd_NullShell_DefaultsToPowerShell()
    {
        string command = TerminalCommandFormatter.FormatCd(null, @"C:\Temp");

        Assert.Equal("cd 'C:\\Temp'\r", command);
    }

    [Fact]
    public void FormatCd_NormalPosixPath_Unchanged()
    {
        string command = TerminalCommandFormatter.FormatCd("bash", "/var/log");

        Assert.Equal("cd '/var/log'\r", command);
    }

    /// <summary>
    /// Carriage return submits the line; line feed does not.
    /// </summary>
    /// <remarks>
    /// Measured 2026-09-20 against a live ConPTY: the same command terminated with LF leaves
    /// Windows PowerShell on its ">> " continuation prompt with nothing executed, three runs
    /// out of three, while CR runs it and returns a fresh prompt. The file browser's
    /// "navigate here" and "run in shell" typed their command and never sent it.
    /// </remarks>
    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("cmd.exe")]
    [InlineData("bash.exe")]
    public void FormatCdAndRun_EndWithCarriageReturn_NotLineFeed(string shell)
    {
        string cd = TerminalCommandFormatter.FormatCd(shell, @"C:\Temp");
        string run = TerminalCommandFormatter.FormatRun(shell, @"C:\Temp\tool.exe");

        Assert.EndsWith("\r", cd, StringComparison.Ordinal);
        Assert.EndsWith("\r", run, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', cd);
        Assert.DoesNotContain('\n', run);
    }

    /// <summary>
    /// The shell kind comes from the file name, not from anywhere in the path.
    /// </summary>
    /// <remarks>
    /// Matching "cmd" anywhere made C:\cmdtools\pwsh.exe a cmd shell, which quotes with double
    /// quotes and strips apostrophes instead of doubling them.
    /// </remarks>
    [Fact]
    public void FormatCd_ShellInADirectoryNamedAfterAnotherShell_UsesTheFileName()
    {
        string command = TerminalCommandFormatter.FormatCd(@"C:\cmdtools\powershell.exe", @"C:\Temp\o'brien");

        Assert.Equal(@"cd 'C:\Temp\o''brien'" + "\r", command);
    }
}
