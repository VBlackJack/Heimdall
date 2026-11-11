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

using System.Runtime.Versioning;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class PrivilegeLauncherTests
{
    [Fact]
    public void EncodeDecodeLaunchPayload_RoundTripsArgumentsWithSpacesQuotesAndDashes()
    {
        string[] originalArgs =
        [
            "arg with spaces",
            "he said \"hello\"",
            "arg ",
            "--key=value",
            "-Force"
        ];

        string encoded = PrivilegeLauncher.EncodeLaunchPayload("notepad.exe", originalArgs);
        PrivilegeLauncher.PrivilegeLaunchPayload decoded = PrivilegeLauncher.DecodeLaunchPayload(encoded);

        Assert.Equal("notepad.exe", decoded.Exe);
        Assert.Equal(originalArgs, decoded.Args);
    }

    [Fact]
    public void EncodeDecodeLaunchPayload_RoundTripsEmptyArgsArray()
    {
        string encoded = PrivilegeLauncher.EncodeLaunchPayload("notepad.exe", []);
        PrivilegeLauncher.PrivilegeLaunchPayload decoded = PrivilegeLauncher.DecodeLaunchPayload(encoded);

        Assert.Equal("notepad.exe", decoded.Exe);
        Assert.Empty(decoded.Args);
    }

    [Fact]
    public void EncodeDecodeLaunchPayload_RoundTripsSingleArgument()
    {
        string encoded = PrivilegeLauncher.EncodeLaunchPayload("notepad.exe", ["single"]);
        PrivilegeLauncher.PrivilegeLaunchPayload decoded = PrivilegeLauncher.DecodeLaunchPayload(encoded);

        Assert.Equal("notepad.exe", decoded.Exe);
        Assert.Equal(["single"], decoded.Args);
    }

    [Fact]
    public void ParseArguments_PreservesQuotedArguments()
    {
        string[] parsed = PrivilegeLauncher.ParseArguments(
            "\"arg with spaces\" \"he said \\\"hello\\\"\" \"arg \" --key=value -Force");

        Assert.Equal(
            ["arg with spaces", "he said \"hello\"", "arg ", "--key=value", "-Force"],
            parsed);
    }

    [Fact]
    public void TryValidateLaunchPayload_RejectsNullExe()
    {
        bool isValid = PrivilegeLauncher.TryValidateLaunchPayload(
            new PrivilegeLauncher.PrivilegeLaunchPayload(null!, []),
            out string errorMessage);

        Assert.False(isValid);
        Assert.Contains("Executable path is required", errorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateLaunchPayload_RejectsEmptyExe()
    {
        bool isValid = PrivilegeLauncher.TryValidateLaunchPayload(
            new PrivilegeLauncher.PrivilegeLaunchPayload("", []),
            out string errorMessage);

        Assert.False(isValid);
        Assert.Contains("Executable path is required", errorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateLaunchPayload_RejectsExeContainingNullByte()
    {
        bool isValid = PrivilegeLauncher.TryValidateLaunchPayload(
            new PrivilegeLauncher.PrivilegeLaunchPayload("bad\0exe.exe", []),
            out string errorMessage);

        Assert.False(isValid);
        Assert.Contains("null byte", errorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateLaunchPayload_RejectsMissingFullyQualifiedExe()
    {
        string missingExePath = Path.Combine(Path.GetTempPath(), $"heimdall-missing-{Guid.NewGuid():N}.exe");
        bool isValid = PrivilegeLauncher.TryValidateLaunchPayload(
            new PrivilegeLauncher.PrivilegeLaunchPayload(missingExePath, []),
            out string errorMessage);

        Assert.False(isValid);
        Assert.Contains("File not found", errorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateLaunchPayload_AcceptsBareExeName()
    {
        bool isValid = PrivilegeLauncher.TryValidateLaunchPayload(
            new PrivilegeLauncher.PrivilegeLaunchPayload("notepad.exe", ["arg with spaces"]),
            out string errorMessage);

        Assert.True(isValid);
        Assert.Equal(string.Empty, errorMessage);
    }

    [Fact]
    public void TryValidateLaunchPayload_System_RejectsBareExeName()
    {
        bool isValid = PrivilegeLauncher.TryValidateLaunchPayload(
            new PrivilegeLauncher.PrivilegeLaunchPayload("notepad.exe", []),
            out string errorMessage,
            PrivilegeLevel.System);

        Assert.False(isValid);
        Assert.Contains("fully-qualified", errorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateLaunchPayload_TrustedInstaller_RejectsBareExeName()
    {
        bool isValid = PrivilegeLauncher.TryValidateLaunchPayload(
            new PrivilegeLauncher.PrivilegeLaunchPayload("notepad.exe", []),
            out string errorMessage,
            PrivilegeLevel.TrustedInstaller);

        Assert.False(isValid);
        Assert.Contains("fully-qualified", errorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateLaunchPayload_System_AcceptsExistingFullyQualifiedExe()
    {
        string? comspec = Environment.GetEnvironmentVariable("COMSPEC");
        Assert.False(string.IsNullOrWhiteSpace(comspec));

        bool isValid = PrivilegeLauncher.TryValidateLaunchPayload(
            new PrivilegeLauncher.PrivilegeLaunchPayload(comspec!, []),
            out string errorMessage,
            PrivilegeLevel.System);

        Assert.True(isValid);
        Assert.Equal(string.Empty, errorMessage);
    }

    [Fact]
    public void TryValidateLaunchPayload_System_RejectsMissingFullyQualifiedExe()
    {
        string missingExePath = Path.Combine(Path.GetTempPath(), $"heimdall-missing-{Guid.NewGuid():N}.exe");
        bool isValid = PrivilegeLauncher.TryValidateLaunchPayload(
            new PrivilegeLauncher.PrivilegeLaunchPayload(missingExePath, []),
            out string errorMessage,
            PrivilegeLevel.System);

        Assert.False(isValid);
        Assert.Contains("File not found", errorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateLaunchPayload_CurrentUserElevated_StillAcceptsBareExeName()
    {
        bool isValid = PrivilegeLauncher.TryValidateLaunchPayload(
            new PrivilegeLauncher.PrivilegeLaunchPayload("notepad.exe", []),
            out string errorMessage,
            PrivilegeLevel.CurrentUserElevated);

        Assert.True(isValid);
        Assert.Equal(string.Empty, errorMessage);
    }

    [Fact]
    public void TryValidateLaunchPayload_AcceptsExistingFullyQualifiedExe()
    {
        string? comspec = Environment.GetEnvironmentVariable("COMSPEC");
        Assert.False(string.IsNullOrWhiteSpace(comspec));

        bool isValid = PrivilegeLauncher.TryValidateLaunchPayload(
            new PrivilegeLauncher.PrivilegeLaunchPayload(comspec!, []),
            out string errorMessage);

        Assert.True(isValid);
        Assert.Equal(string.Empty, errorMessage);
    }

    // -- HandlePrivilegeLaunchArgs: refusals, each checked for the reason that refused ------

    private const string PrivLaunch = "--privlaunch";
    private const string Payload = "--payload";

    private static string ExistingExecutable()
    {
        string? comspec = Environment.GetEnvironmentVariable("COMSPEC");
        Assert.False(string.IsNullOrWhiteSpace(comspec));
        return comspec!;
    }

    // The elevation check is injected as "not elevated", so no request can reach a launch
    // whatever the account the tests run under.
    private static (int? Code, PrivilegeLauncher.PrivilegeLaunchRefusal Reason) Handle(params string[] args)
    {
        int? code = PrivilegeLauncher.HandlePrivilegeLaunchArgs(args, static () => false, out var reason);
        return (code, reason);
    }

    private static void AssertRefused(string[] args, PrivilegeLauncher.PrivilegeLaunchRefusal expected)
    {
        (int? code, PrivilegeLauncher.PrivilegeLaunchRefusal reason) = Handle(args);
        Assert.Equal(1, code);
        Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData]
    [InlineData("--other")]
    [InlineData("--payload", "x")]
    public void HandlePrivilegeLaunchArgs_WithoutThePrivLaunchSwitch_ReturnsNull(params string[] args)
    {
        (int? code, PrivilegeLauncher.PrivilegeLaunchRefusal reason) = Handle(args);

        Assert.Null(code);
        Assert.Equal(PrivilegeLauncher.PrivilegeLaunchRefusal.None, reason);
        Assert.Null(PrivilegeLauncher.HandlePrivilegeLaunchArgs(args));
    }

    [Theory]
    [InlineData("--privlaunch")]
    [InlineData("--privlaunch", "System")]
    [InlineData("--privlaunch", "System", "--payload")]
    [InlineData("--privlaunch", "System", "--wrong", "x")]
    [InlineData("--privlaunch", "System", "--payload", "x", "extra")]
    public void HandlePrivilegeLaunchArgs_MalformedCommandLine_IsRefusedAsMalformed(params string[] args)
    {
        AssertRefused(args, PrivilegeLauncher.PrivilegeLaunchRefusal.MalformedCommandLine);
    }

    [Fact]
    public void HandlePrivilegeLaunchArgs_SwitchIsMatchedWithoutRegardToCase()
    {
        AssertRefused(["--PRIVLAUNCH", "Bogus", Payload, "x"], PrivilegeLauncher.PrivilegeLaunchRefusal.InvalidLevel);
    }

    [Fact]
    public void HandlePrivilegeLaunchArgs_UnknownLevel_IsRefusedAsInvalidLevel()
    {
        string encoded = PrivilegeLauncher.EncodeLaunchPayload(ExistingExecutable(), []);

        AssertRefused([PrivLaunch, "Root", Payload, encoded], PrivilegeLauncher.PrivilegeLaunchRefusal.InvalidLevel);
    }

    // The elevated child is itself started by the self-elevation path, so a request for the
    // plain elevated level coming back in would relaunch forever: it is refused outright, before
    // the payload is even read.
    [Fact]
    public void HandlePrivilegeLaunchArgs_CurrentUserElevated_IsRefusedAsUnsupportedWhateverThePayload()
    {
        string encoded = PrivilegeLauncher.EncodeLaunchPayload(ExistingExecutable(), []);

        AssertRefused(
            [PrivLaunch, nameof(PrivilegeLevel.CurrentUserElevated), Payload, encoded],
            PrivilegeLauncher.PrivilegeLaunchRefusal.UnsupportedLevel);
        AssertRefused(
            [PrivLaunch, nameof(PrivilegeLevel.CurrentUserElevated), Payload, "not a payload"],
            PrivilegeLauncher.PrivilegeLaunchRefusal.UnsupportedLevel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("bm90IGpzb24=")]
    public void HandlePrivilegeLaunchArgs_UndecodablePayload_IsRefusedAsUndecodable(string encoded)
    {
        AssertRefused(
            [PrivLaunch, nameof(PrivilegeLevel.System), Payload, encoded],
            PrivilegeLauncher.PrivilegeLaunchRefusal.PayloadUndecodable);
    }

    [Fact]
    public void HandlePrivilegeLaunchArgs_PayloadFailingValidation_IsRefusedAsInvalidBeforeAnyLaunch()
    {
        string relative = PrivilegeLauncher.EncodeLaunchPayload("notepad.exe", []);
        string missing = PrivilegeLauncher.EncodeLaunchPayload(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"),
            []);

        AssertRefused(
            [PrivLaunch, nameof(PrivilegeLevel.System), Payload, relative],
            PrivilegeLauncher.PrivilegeLaunchRefusal.PayloadInvalid);
        AssertRefused(
            [PrivLaunch, nameof(PrivilegeLevel.TrustedInstaller), Payload, missing],
            PrivilegeLauncher.PrivilegeLaunchRefusal.PayloadInvalid);
    }

    // A valid request reaching a process that is not elevated is refused instead of elevating
    // again: this is the guard against an endless relaunch.
    [Theory]
    [InlineData(nameof(PrivilegeLevel.System))]
    [InlineData(nameof(PrivilegeLevel.TrustedInstaller))]
    public void HandlePrivilegeLaunchArgs_ValidRequestFromAnUnelevatedProcess_IsRefusedAsNotElevated(string level)
    {
        string encoded = PrivilegeLauncher.EncodeLaunchPayload(ExistingExecutable(), []);

        AssertRefused([PrivLaunch, level, Payload, encoded], PrivilegeLauncher.PrivilegeLaunchRefusal.NotElevated);
    }

    [Fact]
    public void HandlePrivilegeLaunchArgs_TheElevationCheckComesAfterTheOthers()
    {
        // An invalid payload is reported as invalid even from an unelevated process.
        AssertRefused(
            [PrivLaunch, nameof(PrivilegeLevel.System), Payload, PrivilegeLauncher.EncodeLaunchPayload("notepad.exe", [])],
            PrivilegeLauncher.PrivilegeLaunchRefusal.PayloadInvalid);
    }

    // -- BuildCommandLine ---------------------------------------------------------------------

    private static readonly string Bs = ((char)92).ToString();
    private static readonly string Q = ((char)34).ToString();

    [Fact]
    public void BuildCommandLine_PlainTokensAreLeftUnquoted()
    {
        Assert.Equal(
            $"tool.exe -Force --key=value C:{Bs}dir{Bs}",
            PrivilegeLauncher.BuildCommandLine("tool.exe", ["-Force", "--key=value", $"C:{Bs}dir{Bs}"]));
    }

    [Fact]
    public void BuildCommandLine_ExecutableWithSpacesIsQuoted()
    {
        Assert.Equal(
            $"{Q}C:{Bs}Program Files{Bs}tool.exe{Q}",
            PrivilegeLauncher.BuildCommandLine($"C:{Bs}Program Files{Bs}tool.exe", []));
    }

    [Fact]
    public void BuildCommandLine_EmptyArgumentIsKeptAsEmptyQuotes()
    {
        Assert.Equal($"tool.exe {Q}{Q} next", PrivilegeLauncher.BuildCommandLine("tool.exe", ["", "next"]));
    }

    [Fact]
    public void BuildCommandLine_ArgumentWithSpacesIsQuoted()
    {
        Assert.Equal($"tool.exe {Q}two words{Q}", PrivilegeLauncher.BuildCommandLine("tool.exe", ["two words"]));
    }

    [Fact]
    public void BuildCommandLine_EmbeddedQuotesAreEscaped()
    {
        Assert.Equal(
            $"tool.exe {Q}say {Bs}{Q}hi{Bs}{Q}{Q}",
            PrivilegeLauncher.BuildCommandLine("tool.exe", [$"say {Q}hi{Q}"]));
    }

    [Fact]
    public void BuildCommandLine_BackslashesBeforeAQuoteAreDoubledAndTheQuoteEscaped()
    {
        Assert.Equal(
            $"tool.exe {Q}a{Bs}{Bs}{Bs}{Q}b{Q}",
            PrivilegeLauncher.BuildCommandLine("tool.exe", [$"a{Bs}{Q}b"]));
    }

    [Fact]
    public void BuildCommandLine_TrailingBackslashesInAQuotedArgumentAreDoubledSoTheClosingQuoteSurvives()
    {
        Assert.Equal(
            $"tool.exe {Q}C:{Bs}my dir{Bs}{Bs}{Q}",
            PrivilegeLauncher.BuildCommandLine("tool.exe", [$"C:{Bs}my dir{Bs}"]));
        Assert.Equal(
            $"tool.exe {Q}x y{Bs}{Bs}{Bs}{Bs}{Q}",
            PrivilegeLauncher.BuildCommandLine("tool.exe", [$"x y{Bs}{Bs}"]));
    }

    [Fact]
    public void BuildCommandLine_TrailingBackslashWithoutSpaceIsLeftAlone()
    {
        Assert.Equal(
            $"tool.exe C:{Bs}dir{Bs}",
            PrivilegeLauncher.BuildCommandLine("tool.exe", [$"C:{Bs}dir{Bs}"]));
    }

    [Fact]
    public void BuildCommandLine_RoundTripsThroughTheWindowsCommandLineParser()
    {
        string[] original =
        [
            "plain",
            "two words",
            $"say {Q}hi{Q}",
            $"C:{Bs}my dir{Bs}",
            $"C:{Bs}dir{Bs}",
            $"a{Bs}{Q}b",
            $"x y{Bs}{Bs}",
            "",
            $"tab{(char)9}there",
        ];

        string commandLine = PrivilegeLauncher.BuildCommandLine("heimdall.exe", original);
        string argumentsOnly = commandLine["heimdall.exe ".Length..];

        Assert.Equal(original, PrivilegeLauncher.ParseArguments(argumentsOnly));
    }
}
