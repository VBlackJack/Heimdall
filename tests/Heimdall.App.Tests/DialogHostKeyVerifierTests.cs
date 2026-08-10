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
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Tests;

[Collection(DialogVerifierFileLoggerCollection.Name)]
public sealed class DialogHostKeyVerifierTests
{
    [Fact]
    public async Task VerifyAsync_WithoutApplicationCurrent_ReturnsReject_AndLogsWarning()
    {
        Assert.Null(System.Windows.Application.Current);

        var localizer = await CreateLocalizerAsync("en");
        var logDirectory = Path.Combine(
            Path.GetTempPath(),
            "heimdall-dialog-hostkey-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);
        FileLogger.Initialize(logDirectory, flushIntervalMs: 10);

        var verifier = new DialogHostKeyVerifier(
            localizer,
            new TrustPromptCoordinator());

        var decision = await verifier.VerifyAsync(
            "headless.example.com",
            22,
            "ssh-ed25519",
            "SHA256:presented",
            null);

        FileLogger.Flush();
        var logFile = Assert.Single(Directory.GetFiles(logDirectory, "heimdall_*.log"));
        var logContent = await File.ReadAllTextAsync(logFile);

        Assert.Equal(HostKeyDecision.Reject, decision);
        Assert.Contains("DialogHostKeyVerifier invoked without Application.Current", logContent);
        Assert.Contains("headless.example.com:22", logContent);
    }

    [Fact]
    public async Task VerifyAsync_WithCancelledToken_ReturnsReject()
    {
        var localizer = await CreateLocalizerAsync("en");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var verifier = new DialogHostKeyVerifier(
            localizer,
            new TrustPromptCoordinator());

        var decision = await verifier.VerifyAsync(
            "cancelled.example.com",
            22,
            "ssh-ed25519",
            "SHA256:presented",
            null,
            cts.Token);

        Assert.Equal(HostKeyDecision.Reject, decision);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        var localesPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "locales"));
        await manager.LoadAsync(localesPath, locale);
        return manager;
    }
}
