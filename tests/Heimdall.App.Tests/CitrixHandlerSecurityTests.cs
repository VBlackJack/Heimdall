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
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class CitrixHandlerCredentialProtectorTests : IDisposable
{
    private readonly CredentialProtectorStateScope _scope = new();

    public void Dispose()
    {
        _scope.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_RealCredentialProtector_DecryptsAtLaunchBoundary()
    {
        const string plaintext = "-qlaunch app=ProtectedCalculator";
        using var dek = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultEnabled(true);
        CredentialProtector.SetVaultKey(dek);
        string secretBlob = CredentialProtector.Protect(plaintext);
        string? launchedArguments = null;
        var handler = CreateHandler(
            startInfo =>
            {
                launchedArguments = startInfo.Arguments;
                return null;
            });

        var result = await handler.ConnectAsync(
            CreateServer(secretBlob),
            new AppSettings(),
            CancellationToken.None);

        Assert.True(VaultSecretBlob.IsSecretBlob(secretBlob));
        Assert.True(result.Success);
        Assert.Equal(plaintext, launchedArguments);
        Assert.NotEqual(secretBlob, launchedArguments);
    }

    [Fact]
    public async Task ConnectAsync_RealCredentialProtector_WhenVaultLocks_LaunchesNothing()
    {
        const string plaintext = "-qlaunch app=ProtectedCalculator";
        using var dek = VaultKeyManager.GenerateDek();
        CredentialProtector.SetVaultEnabled(true);
        CredentialProtector.SetVaultKey(dek);
        string secretBlob = CredentialProtector.Protect(plaintext);
        CredentialProtector.ClearVaultKey();
        var launchCallCount = 0;
        var handler = CreateHandler(
            _ =>
            {
                launchCallCount++;
                return null;
            });

        var result = await handler.ConnectAsync(
            CreateServer(secretBlob),
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixLaunchVaultLocked", result.ErrorMessage);
        Assert.Equal(0, launchCallCount);
    }

    private static CitrixHandler CreateHandler(
        Func<ProcessStartInfo, Process?> startProcess) =>
        new(
            new ConnectionStateMachine(),
            new LocalizationManager(),
            startProcess,
            CredentialProtector.Unprotect,
            static () => "SelfService.exe",
            static _ => { },
            static _ => { });

    private static ServerProfileDto CreateServer(string launchCommandLine) =>
        new()
        {
            Id = "srv-citrix-real-protector",
            DisplayName = "Citrix real protector test",
            CitrixLaunchCommandLine = launchCommandLine
        };
}

[Collection("ExternalToolLaunchFileLogger")]
public sealed class CitrixHandlerFileLoggerTests : IDisposable
{
    private readonly string _tempDirectory;

    public CitrixHandlerFileLoggerTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "Heimdall.CitrixHandler.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        FileLogger.SetEnabled(true);
        FileLogger.Initialize(_tempDirectory, flushIntervalMs: 60000);
    }

    public void Dispose()
    {
        FileLogger.SetEnabled(false);
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task ConnectAsync_SecretBlobAndPlaintext_AreAbsentFromFileLog()
    {
        const string plaintext = "-qlaunch app=LOG_SENTINEL_PLAINTEXT";
        string secretBlob = CreateSecretBlob(plaintext);
        string? launchedArguments = null;
        var handler = CreateHandler(
            startInfo =>
            {
                launchedArguments = startInfo.Arguments;
                return null;
            },
            _ => plaintext);

        var result = await handler.ConnectAsync(
            CreateServer(secretBlob),
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(plaintext, launchedArguments);
        string log = ReadLog();
        Assert.Contains("mode=SelfServiceCache", log, StringComparison.Ordinal);
        Assert.Contains("launcher=SelfService.exe", log, StringComparison.Ordinal);
        Assert.Contains("hasLaunchCmd=true", log, StringComparison.Ordinal);
        Assert.DoesNotContain(secretBlob, log, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, log, StringComparison.Ordinal);
        Assert.DoesNotContain("LOG_SENTINEL_PLAINTEXT", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_LaunchException_DoesNotLeakIntoFileLogOrResult()
    {
        const string plaintext = "-qlaunch app=ERROR_SENTINEL_PLAINTEXT";
        string secretBlob = CreateSecretBlob(plaintext);
        var handler = CreateHandler(
            _ => throw new InvalidOperationException(
                $"Launch failed for {secretBlob} / {plaintext}"),
            _ => plaintext);

        var result = await handler.ConnectAsync(
            CreateServer(secretBlob),
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CitrixLaunchFailed", result.ErrorMessage);
        string log = ReadLog();
        Assert.Contains("error=InvalidOperationException", log, StringComparison.Ordinal);
        Assert.DoesNotContain(secretBlob, log, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, log, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR_SENTINEL_PLAINTEXT", log, StringComparison.Ordinal);
    }

    private static CitrixHandler CreateHandler(
        Func<ProcessStartInfo, Process?> startProcess,
        Func<string, string?> unprotectSecret) =>
        new(
            new ConnectionStateMachine(),
            new LocalizationManager(),
            startProcess,
            unprotectSecret,
            static () => "SelfService.exe",
            FileLogger.Info,
            FileLogger.Warn);

    private static ServerProfileDto CreateServer(string launchCommandLine) =>
        new()
        {
            Id = "srv-citrix-log",
            DisplayName = "Citrix log test",
            CitrixLaunchCommandLine = launchCommandLine
        };

    private static string CreateSecretBlob(string plaintext)
    {
        byte[] key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        return VaultSecretBlob.Seal(key, Encoding.UTF8.GetBytes(plaintext));
    }

    private string ReadLog()
    {
        FileLogger.Flush();
        string logFile = Assert.Single(Directory.GetFiles(_tempDirectory, "heimdall_*.log"));
        return File.ReadAllText(logFile);
    }
}
