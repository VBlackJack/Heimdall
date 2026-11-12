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
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The external program of a web session is shown with its control characters visible, and a
/// network or device path is never started.
/// </summary>
public sealed class WebExternalLaunchHardeningTests
{
    private const char Backslash = (char)92;
    private const string LocalProgram = "C:/Tools/browser.exe";
    private static readonly Uri Address = new("https://ilo.example.test/");

    [Fact]
    public void DescribeCommand_PlainProgramAndArguments_IsUnchanged()
    {
        Assert.Equal("browser.exe --new-window {Url}", WebExternalLaunch.DescribeCommand("browser.exe", "--new-window {Url}"));
        Assert.Equal("browser.exe", WebExternalLaunch.DescribeCommand("browser.exe", null));
    }

    [Fact]
    public void DescribeCommand_ANewlineInTheArguments_IsShownAsAVisibleEscape()
    {
        string described = WebExternalLaunch.DescribeCommand("browser.exe", "--a\n--b");

        Assert.DoesNotContain('\n', described);
        Assert.Contains("\\u{000a}", described, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeCommand_ForASession_UsesTheSameDescription()
    {
        var session = new WebSessionResult("srv", Address)
        {
            ExternalProgram = "browser.exe",
            ExternalArguments = "--a\r--b"
        };

        Assert.Equal(
            WebExternalLaunch.DescribeCommand("browser.exe", "--a\r--b"),
            WebExternalLaunch.DescribeCommand(session));
        Assert.DoesNotContain('\r', WebExternalLaunch.DescribeCommand(session));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TryCreate_NetworkOrDevicePath_IsRefused(int form)
    {
        string program = form == 0
            ? $"{Backslash}{Backslash}host{Backslash}share{Backslash}app.exe"
            : $"{Backslash}{Backslash}?{Backslash}C:{Backslash}x.exe";

        var info = WebExternalLaunch.TryCreate(Profile(program), Address, out string? errorKey);

        Assert.Null(info);
        Assert.Equal("ErrorWebExternalNetworkPathRefused", errorKey);
    }

    [Fact]
    public void TryCreate_LocalPath_StillWorks()
    {
        var info = WebExternalLaunch.TryCreate(Profile(LocalProgram), Address, out string? errorKey);

        Assert.NotNull(info);
        Assert.Null(errorKey);
        Assert.Equal(LocalProgram, info!.FileName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TryCreate_ProgramTheShellResolvesToAShare_IsRefused(int form)
    {
        string program = form switch
        {
            0 => "file://host/share/x.exe",
            1 => $"\"{Backslash}{Backslash}host{Backslash}share{Backslash}x.exe\"",
            2 => "//host/share/x.exe",
            _ => $"\"C:{Backslash}Tools{Backslash}x.exe\"",
        };

        var info = WebExternalLaunch.TryCreate(Profile(program), Address, out string? errorKey);

        Assert.Null(info);
        Assert.Equal("ErrorWebExternalNetworkPathRefused", errorKey);
    }

    [Theory]
    [InlineData("Z:/mapped/browser.exe")]
    [InlineData("browser.exe")]
    [InlineData("C:/Program Files/Browser/browser.exe")]
    public void TryCreate_MappedDriveOrBareName_StillWorks(string program)
    {
        var info = WebExternalLaunch.TryCreate(Profile(program), Address, out string? errorKey);

        Assert.NotNull(info);
        Assert.Null(errorKey);
    }

    [Fact]
    public void TryCreate_ArgumentsLongerThanTheDescriptionLimit_AreRefusedRatherThanShownCut()
    {
        ServerProfileDto profile = Profile(LocalProgram);
        profile.WebExternalArguments = new string('a', WebExternalLaunch.MaxDescribedArgumentsLength + 1);

        var info = WebExternalLaunch.TryCreate(profile, Address, out string? errorKey);

        Assert.Null(info);
        Assert.Equal("ErrorWebExternalCommandTooLong", errorKey);
    }

    [Fact]
    public void TryCreate_ArgumentsAtTheLimit_AreAccepted()
    {
        ServerProfileDto profile = Profile(LocalProgram);
        profile.WebExternalArguments = new string('a', WebExternalLaunch.MaxDescribedArgumentsLength);

        Assert.NotNull(WebExternalLaunch.TryCreate(profile, Address, out _));
    }

    [Fact]
    public async Task PrepareForSession_ATooLongCommand_IsRefusedBeforeAnythingIsAsked()
    {
        var session = new WebSessionResult("srv", Address)
        {
            ExternalProgram = LocalProgram,
            ExternalArguments = new string('a', WebExternalLaunch.MaxDescribedArgumentsLength + 1),
        };
        int asked = 0;

        WebExternalLaunch.SessionLaunch launch = await WebExternalLaunch.PrepareForSessionAsync(
            session,
            currentPage: null,
            _ =>
            {
                asked++;
                return Task.FromResult(true);
            });

        Assert.Equal(0, asked);
        Assert.Null(launch.StartInfo);
        Assert.Equal("ErrorWebExternalCommandTooLong", launch.ErrorKey);
    }

    private static ServerProfileDto Profile(string program) => new()
    {
        DisplayName = "Console",
        RemoteServer = "ilo.example.test",
        WebExternalProgram = program,
    };
}
