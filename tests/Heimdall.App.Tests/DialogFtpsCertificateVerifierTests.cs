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
using Heimdall.Core.Certificates;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.Tests;

public sealed class DialogFtpsCertificateVerifierTests
{
    [Fact]
    public async Task VerifyAsync_WithoutApplicationCurrent_ReturnsRejectAndLogsWarning()
    {
        Assert.Null(System.Windows.Application.Current);

        LocalizationManager localizer = await CreateLocalizerAsync("en");
        string logDirectory = Path.Combine(
            Path.GetTempPath(),
            "heimdall-dialog-ftps-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);
        FileLogger.Initialize(logDirectory, flushIntervalMs: 10);
        var verifier = new DialogFtpsCertificateVerifier(
            localizer,
            new TrustPromptCoordinator());

        FtpsCertificateDecision decision = await verifier.VerifyAsync(CreatePrompt());

        FileLogger.Flush();
        string logFile = Assert.Single(Directory.GetFiles(logDirectory, "heimdall_*.log"));
        string logContent = await File.ReadAllTextAsync(logFile);

        Assert.Equal(FtpsCertificateDecision.Reject, decision);
        Assert.Contains(
            "DialogFtpsCertificateVerifier invoked without Application.Current",
            logContent);
    }

    [Fact]
    public async Task VerifyAsync_WithCancelledToken_ReturnsReject()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var verifier = new DialogFtpsCertificateVerifier(
            localizer,
            new TrustPromptCoordinator());

        FtpsCertificateDecision decision = await verifier.VerifyAsync(
            CreatePrompt(),
            cts.Token);

        Assert.Equal(FtpsCertificateDecision.Reject, decision);
    }

    private static FtpsCertificatePrompt CreatePrompt()
        => new(
            "ftps.example.com",
            21,
            "SHA256:presented",
            null,
            "CN=ftps.example.com",
            "CN=Test CA",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            "self-signed");

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        string localesPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "locales"));
        await manager.LoadAsync(localesPath, locale);
        return manager;
    }
}
