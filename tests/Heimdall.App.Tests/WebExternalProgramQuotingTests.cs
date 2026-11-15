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
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The program of a web profile is wrapped in quotes when it holds a space or a quote, so where
/// it ends and its arguments begin is shown, and the profile name of the question cannot close
/// the quotes around it.
/// </summary>
public sealed class WebExternalProgramQuotingTests
{
    [Fact]
    public void DescribeCommand_ProgramWithASpace_IsQuoted()
    {
        Assert.Equal(
            @"""C:\Program Files\b.exe"" --x",
            WebExternalLaunch.DescribeCommand(@"C:\Program Files\b.exe", "--x"));
    }

    [Fact]
    public void DescribeCommand_ProgramWithANoBreakSpace_IsQuoted()
    {
        Assert.StartsWith("\"", WebExternalLaunch.DescribeCommand("C:\\a\u00A0b.exe", "--x"), StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeCommand_ProgramWithAQuote_IsQuotedWithTheQuoteEscaped()
    {
        Assert.Equal(
            @"""a\""b.exe"" --x",
            WebExternalLaunch.DescribeCommand(@"a""b.exe", "--x"));
    }

    [Fact]
    public void DescribeCommand_PlainProgram_StaysUnquoted()
    {
        Assert.Equal("legacy.exe --x", WebExternalLaunch.DescribeCommand("legacy.exe", "--x"));
    }

    [Fact]
    public void FitsDescription_ProgramOfTheMaximumLengthWithAQuote_IsRefused()
    {
        string program = new string('a', WebExternalLaunch.MaxDescribedProgramLength - 1) + "\"";

        Assert.False(WebExternalLaunch.FitsDescription(program, null));
    }

    [Fact]
    public void FitsDescription_PlainProgramOfTheMaximumLength_Fits()
    {
        string program = new string('a', WebExternalLaunch.MaxDescribedProgramLength);

        Assert.True(WebExternalLaunch.FitsDescription(program, null));
    }

    [Fact]
    public async Task ConfirmationBody_ProfileNameWithALineBreakAndAQuote_IsWrittenVisibly()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

        string body = WebExternalLaunch.ConfirmationBody("My\nname\"x", "browser.exe", localizer);

        Assert.DoesNotContain("\n", body, StringComparison.Ordinal);
        Assert.Contains(@"My\u{000a}name\""x", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FitsDescription_QuotedProgramAtTheLargestInnerLength_FitsAndOneMoreIsRefused()
    {
        int innerLength = WebExternalLaunch.MaxDescribedProgramLength - 2;
        string fits = "a " + new string('b', innerLength - 2);
        string tooLong = fits + "b";

        Assert.True(WebExternalLaunch.FitsDescription(fits, null));
        Assert.Equal(WebExternalLaunch.MaxDescribedProgramLength, WebExternalLaunch.DescribeCommand(fits, null).Length);
        Assert.False(WebExternalLaunch.FitsDescription(tooLong, null));
    }

    [Fact]
    public async Task ConfirmationBody_ProfileNameWithABackslash_IsDoubled()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

        string body = WebExternalLaunch.ConfirmationBody(@"a\b", "browser.exe", localizer);

        Assert.Contains(@"""a\\b""", body, StringComparison.Ordinal);
    }
}
