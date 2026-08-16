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

using System.Diagnostics;
using System.Text;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class CitrixHandlerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/")]
    [InlineData("/path")]
    [InlineData("http:/")]
    public void TryValidateStoreFrontUrl_RejectsInvalidUrls(string? rawUrl)
    {
        var isValid = CitrixHandler.TryValidateStoreFrontUrl(rawUrl, out var validatedUrl);

        Assert.False(isValid);
        Assert.Equal(string.Empty, validatedUrl);
    }

    [Theory]
    [InlineData("https://citrix.example.com/Citrix/StoreWeb/")]
    [InlineData("http://internal-citrix.local:8080/StoreWeb/")]
    public void TryValidateStoreFrontUrl_AcceptsAbsoluteHttpAndHttpsUrls(string rawUrl)
    {
        var isValid = CitrixHandler.TryValidateStoreFrontUrl(rawUrl, out var validatedUrl);

        Assert.True(isValid);
        Assert.Equal(rawUrl, validatedUrl);
    }

    [Fact]
    public void TryValidateStoreFrontUrl_RejectsUserInfo()
    {
        const string rawUrl = "https://user:secret@citrix.example.com/Citrix/StoreWeb/";

        bool isValid = CitrixHandler.TryValidateStoreFrontUrl(rawUrl, out string validatedUrl);

        Assert.False(isValid);
        Assert.Equal(string.Empty, validatedUrl);
    }

    // RDP-005. The invocation used to be built without ever looking at the executable: "-L"
    // unconditionally, plus "-S" when SSO was on. That is wrong twice over - the two commands are
    // documented as distinct and are never combined, and SelfService.exe was handed storebrowse's
    // command line whenever the resolver fell through to it. The tests below pin one grammar per
    // executable, and the two refusals that replace the guess.
    [Fact]
    public void TryCreateStoreFrontStartInfo_StoreBrowseWithSso_EmitsTheLaunchCommandAlone()
    {
        const string appName = "Calculator \"Admin\" Tool";
        const string storeFrontUrl = "https://citrix.example.com/Citrix/StoreWeb/";
        const string launcher = @"C:\Program Files (x86)\Citrix\ICA Client\storebrowse.exe";

        bool created = CitrixHandler.TryCreateStoreFrontStartInfo(
            launcher,
            appName,
            storeFrontUrl,
            useSso: true,
            out ProcessStartInfo? startInfo);

        Assert.True(created);
        Assert.NotNull(startInfo);
        Assert.Equal(launcher, startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(3, startInfo.ArgumentList.Count);
        Assert.Equal("-S", startInfo.ArgumentList[0]);
        // The whole defect in one assertion: the list command must not ride along.
        Assert.DoesNotContain("-L", startInfo.ArgumentList);
        // Application and URL stay separate entries, so a space or a quote survives intact.
        Assert.Equal(appName, startInfo.ArgumentList[1]);
        Assert.Equal(storeFrontUrl, startInfo.ArgumentList[2]);
        Assert.Equal(string.Empty, startInfo.Arguments);
    }

    [Fact]
    public void TryCreateStoreFrontStartInfo_StoreBrowseWithoutSso_EmitsTheListCommandAlone()
    {
        const string storeFrontUrl = "https://citrix.example.com/Citrix/StoreWeb/";

        bool created = CitrixHandler.TryCreateStoreFrontStartInfo(
            "storebrowse.exe",
            "Calculator",
            storeFrontUrl,
            useSso: false,
            out ProcessStartInfo? startInfo);

        Assert.True(created);
        Assert.NotNull(startInfo);
        Assert.Equal(3, startInfo.ArgumentList.Count);
        Assert.Equal("-L", startInfo.ArgumentList[0]);
        Assert.DoesNotContain("-S", startInfo.ArgumentList);
        Assert.Equal("Calculator", startInfo.ArgumentList[1]);
        Assert.Equal(storeFrontUrl, startInfo.ArgumentList[2]);
    }

    [Fact]
    public void TryCreateStoreFrontStartInfo_SelfServiceWithSso_EmitsItsOwnStoreBrowseSubCommand()
    {
        const string appName = "Calculator \"Admin\" Tool";
        const string storeFrontUrl = "https://citrix.example.com/Citrix/StoreWeb/";
        const string launcher =
            @"C:\Program Files (x86)\Citrix\ICA Client\SelfServicePlugin\SelfService.exe";

        bool created = CitrixHandler.TryCreateStoreFrontStartInfo(
            launcher,
            appName,
            storeFrontUrl,
            useSso: true,
            out ProcessStartInfo? startInfo);

        Assert.True(created);
        Assert.NotNull(startInfo);
        Assert.Equal(launcher, startInfo.FileName);
        Assert.Equal(4, startInfo.ArgumentList.Count);
        Assert.Equal("storebrowse", startInfo.ArgumentList[0]);
        Assert.Equal("-q", startInfo.ArgumentList[1]);
        // Neither storebrowse.exe command belongs on this executable.
        Assert.DoesNotContain("-L", startInfo.ArgumentList);
        Assert.DoesNotContain("-S", startInfo.ArgumentList);
        Assert.Equal(appName, startInfo.ArgumentList[2]);
        Assert.Equal(storeFrontUrl, startInfo.ArgumentList[3]);
    }

    [Fact]
    public void TryCreateStoreFrontStartInfo_SelfServiceWithoutSso_RefusesRatherThanGuessing()
    {
        // The documented sub-command covers single sign-on only, so there is no invocation to form.
        bool created = CitrixHandler.TryCreateStoreFrontStartInfo(
            @"C:\Program Files (x86)\Citrix\ICA Client\SelfServicePlugin\SelfService.exe",
            "Calculator",
            "https://citrix.example.com/Citrix/StoreWeb/",
            useSso: false,
            out ProcessStartInfo? startInfo);

        Assert.False(created);
        Assert.Null(startInfo);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryCreateStoreFrontStartInfo_UnknownExecutable_RefusesInsteadOfAssumingStoreBrowse(
        bool useSso)
    {
        bool created = CitrixHandler.TryCreateStoreFrontStartInfo(
            @"C:\Program Files (x86)\Citrix\ICA Client\wfica32.exe",
            "Calculator",
            "https://citrix.example.com/Citrix/StoreWeb/",
            useSso,
            out ProcessStartInfo? startInfo);

        Assert.False(created);
        Assert.Null(startInfo);
    }

    [Theory]
    [InlineData("STOREBROWSE.EXE", "-S")]
    [InlineData(@"C:\Citrix\StoreBrowse.Exe", "-S")]
    [InlineData("selfservice.exe", "storebrowse")]
    [InlineData(@"C:\Citrix\SELFSERVICE.EXE", "storebrowse")]
    public void TryCreateStoreFrontStartInfo_MatchesTheFileNameCaseInsensitively(
        string launcher,
        string expectedFirstArgument)
    {
        bool created = CitrixHandler.TryCreateStoreFrontStartInfo(
            launcher,
            "Calculator",
            "https://citrix.example.com/Citrix/StoreWeb/",
            useSso: true,
            out ProcessStartInfo? startInfo);

        Assert.True(created);
        Assert.NotNull(startInfo);
        Assert.Equal(expectedFirstArgument, startInfo.ArgumentList[0]);
    }

    [Fact]
    public async Task ConnectAsync_SelfServiceLauncherWithoutSso_RefusesBeforeStartingTheProcess()
    {
        int launchCallCount = 0;
        List<string> logs = [];
        ConnectionStateMachine stateMachine = new();
        CitrixHandler handler = CreateHandler(
            _ =>
            {
                launchCallCount++;
                return null;
            },
            static stored => stored,
            stateMachine,
            logs.Add,
            logs.Add,
            resolveCitrixLauncher: static () =>
                @"C:\Program Files (x86)\Citrix\ICA Client\SelfServicePlugin\SelfService.exe");
        ServerProfileDto server = CreateStoreFrontServer(useSso: false);

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixLaunchFailed", result.ErrorMessage);
        Assert.Equal("CitrixLaunchFailed", stateMachine.GetStateData(server.Id)?.ErrorMessage);
        Assert.Equal(0, launchCallCount);
        Assert.Null(result.Session);
        Assert.Contains(
            logs,
            log => log.Contains("unsupportedLauncherGrammar=true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectAsync_UnknownLauncher_RefusesBeforeStartingTheProcess()
    {
        int launchCallCount = 0;
        CitrixHandler handler = CreateHandler(
            _ =>
            {
                launchCallCount++;
                return null;
            },
            static stored => stored,
            resolveCitrixLauncher: static () => @"C:\Citrix\wfica32.exe");

        ConnectionResult result = await handler.ConnectAsync(
            CreateStoreFrontServer(useSso: true),
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixLaunchFailed", result.ErrorMessage);
        Assert.Equal(0, launchCallCount);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task ConnectAsync_SelfServiceLauncherWithSso_LaunchesWithItsOwnGrammar()
    {
        ProcessStartInfo? launchedStartInfo = null;
        CitrixHandler handler = CreateHandler(
            startInfo =>
            {
                launchedStartInfo = startInfo;
                return null;
            },
            static stored => stored,
            resolveCitrixLauncher: static () =>
                @"C:\Program Files (x86)\Citrix\ICA Client\SelfServicePlugin\SelfService.exe");

        ConnectionResult result = await handler.ConnectAsync(
            CreateStoreFrontServer(useSso: true),
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(launchedStartInfo);
        Assert.Equal(
            new[] { "storebrowse", "-q", "Calculator", "https://citrix.example.com/Citrix/StoreWeb/" },
            launchedStartInfo.ArgumentList);
    }

    [Fact]
    public async Task ConnectAsync_InvalidStoreFrontUrl_ReturnsLocalizedError()
    {
        var handler = new CitrixHandler(new ConnectionStateMachine(), new LocalizationManager());
        var server = new ServerProfileDto
        {
            Id = "srv-citrix-invalid-url",
            DisplayName = "Citrix test",
            CitrixAppName = "Calculator",
            CitrixStoreFrontUrl = "javascript:alert(1)"
        };

        var result = await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixInvalidStoreFrontUrl", result.ErrorMessage);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task ConnectAsync_StoreFrontUserInfo_ReturnsDedicatedErrorBeforeLogging()
    {
        const string rawUrl = "https://user:secret@citrix.example.com/Citrix/StoreWeb/";
        int launchCallCount = 0;
        List<string> logs = [];
        CitrixHandler handler = CreateHandler(
            _ =>
            {
                launchCallCount++;
                return null;
            },
            static stored => stored,
            logInfo: logs.Add,
            logWarning: logs.Add);
        ServerProfileDto server = new()
        {
            Id = "srv-citrix-userinfo",
            DisplayName = "Citrix userinfo test",
            CitrixAppName = "Calculator",
            CitrixStoreFrontUrl = rawUrl
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixStoreFrontCredentialsNotAllowed", result.ErrorMessage);
        Assert.Equal(0, launchCallCount);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task ConnectAsync_ValidStoreFrontUrl_LogsOnlySafeUrl()
    {
        const string rawUrl =
            "https://citrix.example.com:8443/Citrix/StoreWeb/?access_token=secret#fragment";
        const string safeLogUrl = "https://citrix.example.com:8443/Citrix/StoreWeb/";
        List<string> logs = [];
        ProcessStartInfo? launchedStartInfo = null;
        CitrixHandler handler = CreateHandler(
            startInfo =>
            {
                launchedStartInfo = startInfo;
                return null;
            },
            static stored => stored,
            logInfo: logs.Add,
            logWarning: logs.Add);
        ServerProfileDto server = new()
        {
            Id = "srv-citrix-safe-log",
            DisplayName = "Citrix safe log test",
            CitrixAppName = "Calculator",
            CitrixStoreFrontUrl = rawUrl
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(launchedStartInfo);
        Assert.Equal(rawUrl, launchedStartInfo.ArgumentList[^1]);
        Assert.Equal(2, logs.Count);
        Assert.All(logs, log =>
        {
            Assert.Contains(safeLogUrl, log, StringComparison.Ordinal);
            Assert.DoesNotContain(rawUrl, log, StringComparison.Ordinal);
            Assert.DoesNotContain("access_token", log, StringComparison.Ordinal);
            Assert.DoesNotContain("fragment", log, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ConnectAsync_RejectsShellMetacharacters_WithDedicatedMessage()
    {
        var handler = new CitrixHandler(new ConnectionStateMachine(), new LocalizationManager());
        var server = new ServerProfileDto
        {
            Id = "srv-citrix-rejected-command",
            DisplayName = "Citrix test",
            CitrixLaunchCommandLine = "launch | malicious"
        };

        var result = await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixLaunchCommandRejected", result.ErrorMessage);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task ConnectAsync_SecretBlob_DecryptsBeforeValidationAndLaunch()
    {
        const string plaintext = "-qlaunch app=Calculator";
        string secretBlob = CreateSecretBlob(plaintext);
        string? decryptInput = null;
        string? launchedArguments = null;
        ProcessStartInfo? retainedStartInfo = null;
        var handler = CreateHandler(
            startInfo =>
            {
                retainedStartInfo = startInfo;
                launchedArguments = startInfo.Arguments;
                return null;
            },
            stored =>
            {
                decryptInput = stored;
                return plaintext;
            });
        var server = CreateCacheServer(secretBlob);

        var result = await handler.ConnectAsync(
            server,
            new AppSettings { VaultEnabled = true },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(secretBlob, decryptInput);
        Assert.Equal(plaintext, launchedArguments);
        Assert.NotEqual(secretBlob, launchedArguments);
        Assert.NotNull(retainedStartInfo);
        Assert.Equal(string.Empty, retainedStartInfo!.Arguments);
        Assert.False(retainedStartInfo.UseShellExecute);
        Assert.True(retainedStartInfo.CreateNoWindow);
    }

    [Fact]
    public async Task ConnectAsync_SecretBlob_ValidatesDecryptedPlaintext()
    {
        string secretBlob = CreateSecretBlob("-qlaunch app=Calculator");
        var launchCallCount = 0;
        var handler = CreateHandler(
            _ =>
            {
                launchCallCount++;
                return null;
            },
            _ => "-qlaunch app=Calculator | rejected");
        var server = CreateCacheServer(secretBlob);

        var result = await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixLaunchCommandRejected", result.ErrorMessage);
        Assert.Equal(0, launchCallCount);
    }

    [Fact]
    public async Task ConnectAsync_LegacyPlaintext_LaunchesWithoutDecrypting()
    {
        const string plaintext = "-qlaunch app=LegacyCalculator";
        var decryptCallCount = 0;
        string? launchedArguments = null;
        var handler = CreateHandler(
            startInfo =>
            {
                launchedArguments = startInfo.Arguments;
                return null;
            },
            _ =>
            {
                decryptCallCount++;
                return "unexpected";
            });
        var server = CreateCacheServer(plaintext);

        var result = await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, decryptCallCount);
        Assert.Equal(plaintext, launchedArguments);
    }

    [Fact]
    public async Task ConnectAsync_VaultEnabledWithPlaintextToken_RefusesWithoutDecryptingOrLaunching()
    {
        const string plaintext = "-qlaunch app=LegacyCalculator ticket=fake-secret";
        int launchCallCount = 0;
        int decryptCallCount = 0;
        List<string> logs = new();
        ConnectionStateMachine stateMachine = new();
        CitrixHandler handler = CreateHandler(
            _ =>
            {
                launchCallCount++;
                return null;
            },
            _ =>
            {
                decryptCallCount++;
                return "unexpected";
            },
            stateMachine,
            logs.Add,
            logs.Add);
        ServerProfileDto server = CreateCacheServer(plaintext);
        AppSettings settings = new() { VaultEnabled = true };

        ConnectionResult result = await handler.ConnectAsync(server, settings, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixLaunchFailed", result.ErrorMessage);
        Assert.Equal("CitrixLaunchFailed", stateMachine.GetStateData(server.Id)?.ErrorMessage);
        Assert.Equal(0, launchCallCount);
        Assert.Equal(0, decryptCallCount);
        Assert.Contains(
            logs,
            log => log.Contains("unprotectedToken=true", StringComparison.Ordinal));
        Assert.All(
            logs,
            log => Assert.DoesNotContain(plaintext, log, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectAsync_VaultLocked_ReturnsRedactedErrorWithoutLaunching()
    {
        string secretBlob = CreateSecretBlob("-qlaunch app=Calculator");
        var launchCallCount = 0;
        var logs = new List<string>();
        var stateMachine = new ConnectionStateMachine();
        var handler = CreateHandler(
            _ =>
            {
                launchCallCount++;
                return null;
            },
            _ => throw new VaultLockedException(),
            stateMachine,
            logs.Add,
            logs.Add);
        var server = CreateCacheServer(secretBlob);

        var result = await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixLaunchVaultLocked", result.ErrorMessage);
        Assert.Equal(0, launchCallCount);
        Assert.Equal("CitrixLaunchVaultLocked", stateMachine.GetStateData(server.Id)?.ErrorMessage);
        Assert.All(logs, log => Assert.DoesNotContain(secretBlob, log, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectAsync_SecretDecryptionReturnsNull_ReturnsLockedErrorWithoutLaunching()
    {
        string secretBlob = CreateSecretBlob("-qlaunch app=Calculator");
        var launchCallCount = 0;
        var logs = new List<string>();
        var handler = CreateHandler(
            _ =>
            {
                launchCallCount++;
                return null;
            },
            _ => null,
            logInfo: logs.Add,
            logWarning: logs.Add);
        var server = CreateCacheServer(secretBlob);

        var result = await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixLaunchVaultLocked", result.ErrorMessage);
        Assert.Equal(0, launchCallCount);
        Assert.All(logs, log => Assert.DoesNotContain(secretBlob, log, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectAsync_LaunchFailure_RedactsExceptionAndResult()
    {
        const string plaintext = "-qlaunch app=SensitiveCalculator";
        string secretBlob = CreateSecretBlob(plaintext);
        var logs = new List<string>();
        var stateMachine = new ConnectionStateMachine();
        var handler = CreateHandler(
            _ => throw new InvalidOperationException(
                $"Failed with stored={secretBlob} plaintext={plaintext}"),
            _ => plaintext,
            stateMachine,
            logs.Add,
            logs.Add);
        var server = CreateCacheServer(secretBlob);

        var result = await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixLaunchFailed", result.ErrorMessage);
        Assert.Equal("CitrixLaunchFailed", stateMachine.GetStateData(server.Id)?.ErrorMessage);
        Assert.Contains(
            logs,
            log => log.Contains(
                "error=InvalidOperationException",
                StringComparison.Ordinal));
        Assert.All(logs, log =>
        {
            Assert.DoesNotContain(secretBlob, log, StringComparison.Ordinal);
            Assert.DoesNotContain(plaintext, log, StringComparison.Ordinal);
        });
    }

    // RDP-020: the whole finding is an ORDERING one - the baseline used to be taken by the view,
    // after the launcher had started, so single sign-on or a warm cache could surface the session
    // window first and have it classified as pre-existing for the entire detection window.
    [Fact]
    public async Task ConnectAsync_CapturesTheVisibleWindowBaselineBeforeStartingTheLauncher()
    {
        List<string> sequence = [];
        IReadOnlySet<nint> baseline = new HashSet<nint> { 0x111, 0x222 };

        CitrixHandler handler = CreateHandler(
            startInfo =>
            {
                _ = startInfo;
                sequence.Add("start");
                return null;
            },
            static stored => stored,
            capturePreLaunchWindows: () =>
            {
                sequence.Add("capture");
                return baseline;
            });

        ConnectionResult result = await handler.ConnectAsync(
            CreateCacheServer("-qlaunch app=Calculator"),
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "capture", "start" }, sequence);

        CitrixSessionResult session = Assert.IsType<CitrixSessionResult>(result.Session);
        Assert.Equal(baseline, session.PreLaunchWindows);
    }

    [Fact]
    public async Task ConnectAsync_CarriesTheBaselineEvenWhenTheLauncherReturnsNoProcess()
    {
        IReadOnlySet<nint> baseline = new HashSet<nint> { 0x333 };
        CitrixHandler handler = CreateHandler(
            static _ => null,
            static stored => stored,
            capturePreLaunchWindows: () => baseline);

        ConnectionResult result = await handler.ConnectAsync(
            CreateCacheServer("-qlaunch app=Calculator"),
            new AppSettings(),
            CancellationToken.None);

        CitrixSessionResult session = Assert.IsType<CitrixSessionResult>(result.Session);
        Assert.Equal(baseline, session.PreLaunchWindows);
    }

    private static CitrixHandler CreateHandler(
        Func<ProcessStartInfo, Process?> startProcess,
        Func<string, string?> unprotectSecret,
        ConnectionStateMachine? stateMachine = null,
        Action<string>? logInfo = null,
        Action<string>? logWarning = null,
        Func<IReadOnlySet<nint>>? capturePreLaunchWindows = null,
        Func<string?>? resolveCitrixLauncher = null) =>
        new(
            stateMachine ?? new ConnectionStateMachine(),
            new LocalizationManager(),
            startProcess,
            unprotectSecret,
            static () => "SelfService.exe",
            logInfo ?? (static _ => { }),
            logWarning ?? (static _ => { }),
            resolveCitrixLauncher ?? (static () => "storebrowse.exe"),
            capturePreLaunchWindows);

    private static ServerProfileDto CreateStoreFrontServer(bool useSso) =>
        new()
        {
            Id = "srv-citrix-storefront",
            DisplayName = "Citrix StoreFront test",
            CitrixAppName = "Calculator",
            CitrixStoreFrontUrl = "https://citrix.example.com/Citrix/StoreWeb/",
            CitrixUseSso = useSso
        };

    private static ServerProfileDto CreateCacheServer(string launchCommandLine) =>
        new()
        {
            Id = "srv-citrix-cache",
            DisplayName = "Citrix cache test",
            CitrixLaunchCommandLine = launchCommandLine
        };

    private static string CreateSecretBlob(string plaintext)
    {
        byte[] key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        return VaultSecretBlob.Seal(key, Encoding.UTF8.GetBytes(plaintext));
    }
}
