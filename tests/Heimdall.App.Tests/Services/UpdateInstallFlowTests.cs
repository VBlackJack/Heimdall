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
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.Core.Updates;

namespace Heimdall.App.Tests;

public sealed class UpdateInstallFlowTests
{
    private static readonly string InstallerSha256 = new('a', 64);

    private static UpdateInfo SampleUpdate() =>
        new(
            HeimdallVersion.Parse("2026.061502"),
            "v2026.061502",
            "https://example.test",
            "notes",
            new UpdateAsset("Heimdall_2026.061502_Standard_Setup.exe", "https://example.test/setup.exe", 1),
            null);

    [Fact]
    public async Task RunAsync_DownloadOkAndLaunched_ReturnsStartedAndRequestsShutdownOnce()
    {
        var updateService = new FakeUpdateService();
        var installer = new FakeUpdateInstaller { BeginInstallResult = true };
        var lifecycle = new FakeApplicationLifecycle();
        var flow = new UpdateInstallFlow(updateService, installer, lifecycle);
        var progress = new Progress<double>();

        var outcome = await flow.RunAsync(SampleUpdate(), progress, CancellationToken.None);

        outcome.Should().Be(UpdateInstallOutcome.Started);
        installer.BeginInstallCallCount.Should().Be(1);
        installer.LastInstallerPath.Should().Be(updateService.DownloadResultPath);
        lifecycle.RequestShutdownCallCount.Should().Be(1);
        updateService.LastProgress.Should().BeSameAs(progress);
        updateService.Package.TransferCallCount.Should().Be(1);
        updateService.Package.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_BeginInstallFalse_ReturnsInstallLaunchFailedWithoutShutdown()
    {
        var updateService = new FakeUpdateService();
        var installer = new FakeUpdateInstaller { BeginInstallResult = false };
        var lifecycle = new FakeApplicationLifecycle();
        var flow = new UpdateInstallFlow(updateService, installer, lifecycle);

        var outcome = await flow.RunAsync(SampleUpdate(), null, CancellationToken.None);

        outcome.Should().Be(UpdateInstallOutcome.InstallLaunchFailed);
        installer.BeginInstallCallCount.Should().Be(1);
        lifecycle.RequestShutdownCallCount.Should().Be(0);
        updateService.Package.TransferCallCount.Should().Be(0);
        updateService.Package.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_DownloadThrowsOperationCanceled_ReturnsCancelled()
    {
        var updateService = new FakeUpdateService { DownloadException = new OperationCanceledException() };
        var installer = new FakeUpdateInstaller();
        var lifecycle = new FakeApplicationLifecycle();
        var flow = new UpdateInstallFlow(updateService, installer, lifecycle);

        var outcome = await flow.RunAsync(SampleUpdate(), null, CancellationToken.None);

        outcome.Should().Be(UpdateInstallOutcome.Cancelled);
        installer.BeginInstallCallCount.Should().Be(0);
        lifecycle.RequestShutdownCallCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_DownloadThrowsInvalidOperation_ReturnsVerificationFailed()
    {
        var updateService = new FakeUpdateService { DownloadException = new InvalidOperationException("checksum mismatch") };
        var installer = new FakeUpdateInstaller();
        var lifecycle = new FakeApplicationLifecycle();
        var flow = new UpdateInstallFlow(updateService, installer, lifecycle);

        var outcome = await flow.RunAsync(SampleUpdate(), null, CancellationToken.None);

        outcome.Should().Be(UpdateInstallOutcome.VerificationFailed);
        installer.BeginInstallCallCount.Should().Be(0);
        lifecycle.RequestShutdownCallCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_DownloadThrowsGenericException_ReturnsDownloadFailed()
    {
        var updateService = new FakeUpdateService { DownloadException = new IOException("network down") };
        var installer = new FakeUpdateInstaller();
        var lifecycle = new FakeApplicationLifecycle();
        var flow = new UpdateInstallFlow(updateService, installer, lifecycle);

        var outcome = await flow.RunAsync(SampleUpdate(), null, CancellationToken.None);

        outcome.Should().Be(UpdateInstallOutcome.DownloadFailed);
        installer.BeginInstallCallCount.Should().Be(0);
        lifecycle.RequestShutdownCallCount.Should().Be(0);
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public string DownloadResultPath { get; set; } = @"C:\Temp\HeimdallSetup.exe";

        public Exception? DownloadException { get; set; }

        public IProgress<double>? LastProgress { get; private set; }

        public FakeVerifiedUpdatePackage Package { get; } = new(
            @"C:\Temp\HeimdallSetup.exe",
            InstallerSha256,
            @"C:\Temp\update-stage");

        public Task<UpdateCheckResult> CheckForUpdatesAsync(HeimdallVersion current, string owner, string repo, CancellationToken cancellationToken)
            => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.UpToDate, null));

        public Task<IVerifiedUpdatePackage> DownloadVerifiedAsync(
            UpdateInfo update,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            LastProgress = progress;
            if (DownloadException is not null)
            {
                throw DownloadException;
            }

            return Task.FromResult<IVerifiedUpdatePackage>(Package);
        }
    }

    private sealed class FakeUpdateInstaller : IUpdateInstaller
    {
        public bool BeginInstallResult { get; set; } = true;

        public int BeginInstallCallCount { get; private set; }

        public string? LastInstallerPath { get; private set; }

        public bool BeginInstall(IVerifiedUpdatePackage package)
        {
            BeginInstallCallCount++;
            LastInstallerPath = package.InstallerPath;
            return BeginInstallResult;
        }
    }

    private sealed class FakeVerifiedUpdatePackage(
        string installerPath,
        string expectedSha256,
        string stagingDirectory) : IVerifiedUpdatePackage
    {
        public string InstallerPath { get; } = installerPath;

        public string ExpectedSha256 { get; } = expectedSha256;

        public string StagingDirectory { get; } = stagingDirectory;

        public int TransferCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public void TransferCleanupToRelauncher() => TransferCallCount++;

        public void Dispose() => DisposeCallCount++;
    }

    private sealed class FakeApplicationLifecycle : IApplicationLifecycle
    {
        public int RequestShutdownCallCount { get; private set; }

        public void RequestShutdown() => RequestShutdownCallCount++;
    }
}
