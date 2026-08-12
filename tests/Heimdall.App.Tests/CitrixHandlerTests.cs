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

    [Fact]
    public void CreateStoreFrontStartInfo_UsesArgumentListAndPreservesQuotedAppNameToken()
    {
        var appName = "Calculator \"Admin\" Tool";
        var storeFrontUrl = "https://citrix.example.com/Citrix/StoreWeb/";

        var startInfo = CitrixHandler.CreateStoreFrontStartInfo(
            @"C:\Program Files (x86)\Citrix\ICA Client\storebrowse.exe",
            appName,
            storeFrontUrl,
            useSso: true);

        Assert.Equal(@"C:\Program Files (x86)\Citrix\ICA Client\storebrowse.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(4, startInfo.ArgumentList.Count);
        Assert.Equal("-L", startInfo.ArgumentList[0]);
        Assert.Equal("-S", startInfo.ArgumentList[1]);
        Assert.Equal(appName, startInfo.ArgumentList[2]);
        Assert.Equal(storeFrontUrl, startInfo.ArgumentList[3]);
        Assert.Equal(string.Empty, startInfo.Arguments);
    }

    [Fact]
    public void CreateStoreFrontStartInfo_WithoutSso_OmitsSessionLaunchFlag()
    {
        var startInfo = CitrixHandler.CreateStoreFrontStartInfo(
            "storebrowse.exe",
            "Calculator",
            "https://citrix.example.com/Citrix/StoreWeb/",
            useSso: false);

        Assert.Equal(3, startInfo.ArgumentList.Count);
        Assert.Equal("-L", startInfo.ArgumentList[0]);
        Assert.Equal("Calculator", startInfo.ArgumentList[1]);
        Assert.Equal("https://citrix.example.com/Citrix/StoreWeb/", startInfo.ArgumentList[2]);
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

    private static CitrixHandler CreateHandler(
        Func<ProcessStartInfo, Process?> startProcess,
        Func<string, string?> unprotectSecret,
        ConnectionStateMachine? stateMachine = null,
        Action<string>? logInfo = null,
        Action<string>? logWarning = null) =>
        new(
            stateMachine ?? new ConnectionStateMachine(),
            new LocalizationManager(),
            startProcess,
            unprotectSecret,
            static () => "SelfService.exe",
            logInfo ?? (static _ => { }),
            logWarning ?? (static _ => { }),
            static () => "storebrowse.exe");

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
