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
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Tests;

public sealed class RdpHandlerTests
{
    /// <summary>Budget for observing a detached autofill task reach - or not reach - its fake.</summary>
    private static readonly TimeSpan AutofillObservationBudget = TimeSpan.FromSeconds(5);

    /// <summary>Budget for observing the deferred cleanup task run - or stay put.</summary>
    private static readonly TimeSpan CleanupObservationBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConnectAsync_ForceEmbeddedUsesEmbeddedPathWithoutMutatingProfile()
    {
        var launcher = new TrackingRdpExternalClientLauncher();
        var handler = CreateHandler(launcher);
        var server = CreateServer("External");
        var settings = new AppSettings();

        var result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceEmbedded);

        Assert.True(result.Success);
        Assert.IsType<RdpSessionResult>(result.Session);
        Assert.Equal(0, launcher.LaunchCalls);
        Assert.Equal("External", server.RdpMode);
    }

    [Fact]
    public async Task ConnectAsync_ForceExternalUsesExternalLauncherWithoutMutatingProfile()
    {
        var launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = new FakeLaunchedRdpClientProcess(4242)
        };
        var handler = CreateHandler(launcher);
        var server = CreateServer("Embedded");
        var settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        var result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.True(result.Success);
        Assert.Null(result.Session);
        Assert.Equal(1, launcher.LaunchCalls);
        Assert.False(string.IsNullOrWhiteSpace(launcher.LastRdpFilePath));
        Assert.Equal("Embedded", server.RdpMode);
    }

    [Fact]
    public async Task ConnectAsync_ExternalModeRequestsDistinctLoopbackTunnel()
    {
        FakeTunnelService tunnelService = new FakeTunnelService();
        var launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = new FakeLaunchedRdpClientProcess(4242)
        };
        var handler = CreateHandler(tunnelService, launcher);
        var server = CreateServer("Embedded");
        var settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        var result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.True(result.Success);
        Assert.Equal(true, tunnelService.LastPreferDistinctLoopback);
    }

    [Fact]
    public async Task ConnectAsync_EmbeddedModeDoesNotRequestDistinctLoopbackTunnel()
    {
        FakeTunnelService tunnelService = new FakeTunnelService();
        var launcher = new TrackingRdpExternalClientLauncher();
        var handler = CreateHandler(tunnelService, launcher);
        var server = CreateServer("External");
        var settings = new AppSettings();

        var result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceEmbedded);

        Assert.True(result.Success);
        Assert.IsType<RdpSessionResult>(result.Session);
        Assert.Equal(false, tunnelService.LastPreferDistinctLoopback);
        Assert.Equal(0, launcher.LaunchCalls);
    }

    [Fact]
    public async Task ConnectAsync_ForceExternalLaunchExceptionReturnsLocalizedMstscError()
    {
        const string rawExceptionMessage = "raw mstsc launch exception";
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ExceptionToThrow = new InvalidOperationException(rawExceptionMessage)
        };
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        RdpHandler handler = CreateHandler(new PassThroughTunnelService(), localizer, launcher);
        ServerProfileDto server = CreateServer("Embedded");
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.False(result.Success);
        Assert.Equal("mstsc.exe did not start.", result.ErrorMessage);
        Assert.NotEqual(rawExceptionMessage, result.ErrorMessage);
        Assert.Equal(1, launcher.LaunchCalls);
        Assert.False(File.Exists(launcher.LastRdpFilePath));
    }

    [Fact]
    public async Task ConnectAsync_ForceExternalInvalidGatewayReturnsLocalizedAttestationErrorWithoutLaunching()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher();
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        RdpHandler handler = CreateHandler(new PassThroughTunnelService(), localizer, launcher);
        ServerProfileDto server = CreateServer("Embedded");
        server.RdpGateway = "invalid gateway!";
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.False(result.Success);
        Assert.Equal(localizer["RdpGatewayAttestationFailed"], result.ErrorMessage);
        Assert.Equal(0, launcher.LaunchCalls);
    }

    [Fact]
    public async Task ConnectAsync_ForeignCredential_ContinuesWithNoticeWithoutWriteOrDelete()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = new FakeLaunchedRdpClientProcess(4242)
        };
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = false
        };
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        int autofillCalls = 0;
        TaskCompletionSource autofillEntered =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RdpHandler handler = CreateHandler(
            launcher,
            credentialManager,
            localizer,
            (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref autofillCalls);
                autofillEntered.TrySetResult();
                return Task.FromResult(true);
            });
        ServerProfileDto server = CreateCredentialedServer();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.True(result.Success);
        Assert.Equal(
            "An existing Windows credential is being used; Heimdall's stored credential was not injected.",
            result.Warning);
        Assert.Equal(1, credentialManager.WriteCalls);
        Assert.Equal(0, credentialManager.DeleteCalls);
        Assert.Equal(1, launcher.LaunchCalls);

        // Assert-absence. Its positive control is
        // ConnectAsync_OwnCredential_InvokesTheAutofillDelegate, which proves this very
        // fake is reached well inside the same budget when the credential is Heimdall's.
        Task settled = await Task.WhenAny(autofillEntered.Task, Task.Delay(AutofillObservationBudget));

        Assert.NotSame(autofillEntered.Task, settled);
        Assert.Equal(0, Volatile.Read(ref autofillCalls));
    }

    [Fact]
    public async Task ConnectAsync_OwnCredential_InvokesTheAutofillDelegate()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = new FakeLaunchedRdpClientProcess(4242)
        };
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = true
        };
        int autofillCalls = 0;
        TaskCompletionSource autofillEntered =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RdpHandler handler = CreateHandler(
            launcher,
            credentialManager,
            new LocalizationManager(),
            (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref autofillCalls);
                autofillEntered.TrySetResult();
                return Task.FromResult(true);
            });
        ServerProfileDto server = CreateCredentialedServer();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.True(result.Success);

        await autofillEntered.Task.WaitAsync(AutofillObservationBudget);

        Assert.Equal(1, Volatile.Read(ref autofillCalls));
    }

    [Fact]
    public async Task ConnectAsync_ExternalClientExits_CancelsTheAutofillWatcher()
    {
        FakeLaunchedRdpClientProcess process = new FakeLaunchedRdpClientProcess(4242);
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = process
        };
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = true
        };
        CancellationToken observedToken = default;
        TaskCompletionSource autofillEntered =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RdpHandler handler = CreateHandler(
            launcher,
            credentialManager,
            new LocalizationManager(),
            async (_, _, _, _, token) =>
            {
                observedToken = token;
                autofillEntered.TrySetResult();
                // Model the real watcher: it polls until its own deadline and only stops
                // early when the token it was handed is cancelled.
                await Task.Delay(Timeout.Infinite, token);
                return true;
            });
        ServerProfileDto server = CreateCredentialedServer();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 90000
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.True(result.Success);
        await autofillEntered.Task.WaitAsync(AutofillObservationBudget);

        process.RaiseExited();

        Assert.True(
            await WaitForCancellationAsync(observedToken, AutofillObservationBudget),
            "The autofill watcher kept a token that the external client's exit cannot cancel.");
    }

    [Fact]
    public async Task ConnectAsync_ConnectTokenCancelledAfterLaunch_DoesNotBringCleanupForward()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = new FakeLaunchedRdpClientProcess(4242)
        };
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = true
        };
        RdpHandler handler = CreateHandler(launcher, credentialManager, new LocalizationManager());
        ServerProfileDto server = CreateCredentialedServer();
        using CancellationTokenSource connectCancellation = new CancellationTokenSource();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 60000,
            RdpCredentialAutofillTimeoutMs = 1
        };

        try
        {
            ConnectionResult result = await handler.ConnectAsync(
                server,
                settings,
                connectCancellation.Token,
                RdpModeOverride.ForceExternal);

            Assert.True(result.Success);

            // The command's own CTS is cancelled as soon as the user launches anything else.
            connectCancellation.Cancel();

            // Assert-absence. Its positive control is
            // ConnectAsync_Success_TransfersCredentialToDelayedCleanup, which proves the
            // very same cleanup path does fire once the configured delay elapses.
            Task settled = await Task.WhenAny(
                credentialManager.DeleteObserved.Task,
                Task.Delay(CleanupObservationBudget));

            Assert.NotSame(credentialManager.DeleteObserved.Task, settled);
            Assert.Equal(0, credentialManager.DeleteCalls);
            Assert.True(File.Exists(launcher.LastRdpFilePath));
        }
        finally
        {
            // The 60 s cleanup outlives this test: the artifact is ours to remove.
            if (launcher.LastRdpFilePath is not null && File.Exists(launcher.LastRdpFilePath))
            {
                File.Delete(launcher.LastRdpFilePath);
            }
        }
    }

    [Fact]
    public async Task ConnectAsync_CredentialWrittenThenLaunchFails_ReleasesImmediately()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher();
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = true
        };
        RdpHandler handler = CreateHandler(launcher, credentialManager, new LocalizationManager());
        ServerProfileDto server = CreateCredentialedServer();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 60000,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.False(result.Success);
        Assert.Equal(1, credentialManager.WriteCalls);
        Assert.Equal(1, credentialManager.DeleteCalls);
        Assert.Equal(credentialManager.LastWriteMarker, credentialManager.LastDeleteMarker);
    }

    [Fact]
    public async Task ConnectAsync_Success_TransfersCredentialToDelayedCleanup()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = new FakeLaunchedRdpClientProcess(4242)
        };
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = true
        };
        TaskCompletionSource cleanupDelayElapsed =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? requestedDelay = null;
        RdpHandler handler = CreateHandler(
            launcher,
            credentialManager,
            new LocalizationManager(),
            artifactCleanupDelay: delay =>
            {
                requestedDelay = delay;
                return cleanupDelayElapsed.Task;
            });
        ServerProfileDto server = CreateCredentialedServer();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 60000,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.True(result.Success);
        Assert.Equal(0, credentialManager.DeleteCalls);

        cleanupDelayElapsed.SetResult();
        await credentialManager.DeleteObserved.Task.WaitAsync(CleanupObservationBudget);

        Assert.Equal(TimeSpan.FromMilliseconds(60000), requestedDelay);
        Assert.Equal(1, credentialManager.DeleteCalls);
        Assert.Equal(credentialManager.LastWriteMarker, credentialManager.LastDeleteMarker);
    }

    [Fact]
    public async Task ConnectAsync_LauncherReturnsNull_DeletesRdpFileBeforeReturningAndReleasesCredentialOnce()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher();
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = true
        };
        RdpHandler handler = CreateHandler(launcher, credentialManager, new LocalizationManager());
        ServerProfileDto server = CreateCredentialedServer();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 60000,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.False(result.Success);
        Assert.Equal(1, launcher.LaunchCalls);
        Assert.True(launcher.FileExistedAtLaunch);
        Assert.False(File.Exists(launcher.LastRdpFilePath));
        Assert.Equal(1, credentialManager.DeleteCalls);
        Assert.Equal(credentialManager.LastWriteMarker, credentialManager.LastDeleteMarker);
    }

    [Fact]
    public async Task ConnectAsync_CredentialReleaseThrows_StillFailsNormallyAndDeletesRdpFile()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher();
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = true,
            DeleteException = new InvalidOperationException("credential provider failure")
        };
        RdpHandler handler = CreateHandler(launcher, credentialManager, new LocalizationManager());
        ServerProfileDto server = CreateCredentialedServer();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 60000,
            RdpCredentialAutofillTimeoutMs = 1
        };

        try
        {
            ConnectionResult result = await handler.ConnectAsync(
                server,
                settings,
                CancellationToken.None,
                RdpModeOverride.ForceExternal);

            Assert.False(result.Success);
            Assert.Equal(1, launcher.LaunchCalls);
            Assert.True(launcher.FileExistedAtLaunch);
            Assert.False(File.Exists(launcher.LastRdpFilePath));
            Assert.Equal(1, credentialManager.DeleteCalls);
        }
        finally
        {
            // Defensive: if the artifact survived, this test must not litter %TEMP%.
            if (launcher.LastRdpFilePath is not null)
            {
                File.Delete(launcher.LastRdpFilePath);
            }
        }
    }

    [Fact]
    public async Task ConnectAsync_LauncherThrows_DeletesRdpFileBeforeReturningAndReleasesCredentialOnce()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ExceptionToThrow = new InvalidOperationException("raw mstsc launch exception")
        };
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = true
        };
        RdpHandler handler = CreateHandler(launcher, credentialManager, new LocalizationManager());
        ServerProfileDto server = CreateCredentialedServer();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 60000,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.False(result.Success);
        Assert.Equal(1, launcher.LaunchCalls);
        Assert.True(launcher.FileExistedAtLaunch);
        Assert.False(File.Exists(launcher.LastRdpFilePath));
        Assert.Equal(1, credentialManager.DeleteCalls);
        Assert.Equal(credentialManager.LastWriteMarker, credentialManager.LastDeleteMarker);
    }

    [Fact]
    public async Task ConnectAsync_DeferredRdpFileDeletionThrowsUnauthorized_StillReleasesCredentialOnce()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = new FakeLaunchedRdpClientProcess(4242)
        };
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = true
        };
        string? attemptedDeletePath = null;
        RdpHandler handler = CreateHandler(
            launcher,
            credentialManager,
            new LocalizationManager(),
            deleteRdpFile: (string path) =>
            {
                attemptedDeletePath = path;
                throw new UnauthorizedAccessException("access to the path is denied");
            });
        ServerProfileDto server = CreateCredentialedServer();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        try
        {
            ConnectionResult result = await handler.ConnectAsync(
                server,
                settings,
                CancellationToken.None,
                RdpModeOverride.ForceExternal);

            Assert.True(result.Success);

            await credentialManager.DeleteObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, credentialManager.DeleteCalls);
            Assert.Equal(credentialManager.LastWriteMarker, credentialManager.LastDeleteMarker);
            Assert.Equal(launcher.LastRdpFilePath, attemptedDeletePath);
        }
        finally
        {
            // The injected seam never deleted anything: this test owns the artifact.
            if (launcher.LastRdpFilePath is not null)
            {
                File.Delete(launcher.LastRdpFilePath);
            }
        }
    }

    [Fact]
    public async Task ConnectAsync_SuccessfulLaunch_DefersRdpFileDeletionThenDeletesItWithTheCredential()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = new FakeLaunchedRdpClientProcess(4242)
        };
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = true
        };
        TaskCompletionSource deleteObserved =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cleanupDelayElapsed =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? deletedPath = null;
        RdpHandler handler = CreateHandler(
            launcher,
            credentialManager,
            new LocalizationManager(),
            deleteRdpFile: (string path) =>
            {
                deletedPath = path;
                File.Delete(path);
                deleteObserved.TrySetResult();
            },
            artifactCleanupDelay: _ => cleanupDelayElapsed.Task);
        ServerProfileDto server = CreateCredentialedServer();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 60000,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.True(result.Success);
        Assert.True(launcher.FileExistedAtLaunch);
        Assert.True(File.Exists(launcher.LastRdpFilePath));
        Assert.Null(deletedPath);
        Assert.Equal(0, credentialManager.DeleteCalls);

        cleanupDelayElapsed.SetResult();
        await deleteObserved.Task.WaitAsync(CleanupObservationBudget);
        await credentialManager.DeleteObserved.Task.WaitAsync(CleanupObservationBudget);

        Assert.Equal(launcher.LastRdpFilePath, deletedPath);
        Assert.False(File.Exists(launcher.LastRdpFilePath));
        Assert.Equal(1, credentialManager.DeleteCalls);
        Assert.Equal(credentialManager.LastWriteMarker, credentialManager.LastDeleteMarker);
    }

    [Fact]
    public async Task ConnectAsync_ForceExternalDecryptFailureReturnsFailureBeforeLaunching()
    {
        var launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = new FakeLaunchedRdpClientProcess(4242)
        };
        var handler = CreateHandler(launcher);
        var server = CreateServer("Embedded");
        server.RdpUsername = "user";
        server.RdpPasswordEncrypted = "not-valid-base64-at-all!!!";
        var settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        var result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
        Assert.Equal(0, launcher.LaunchCalls);
    }

    [Fact]
    public async Task ConnectAsync_ForceExternalDecryptFailureReleasesTunnelReference()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = 53389
        };
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = new FakeLaunchedRdpClientProcess(4242)
        };
        RdpHandler handler = CreateHandler(tunnelService, launcher);
        ServerProfileDto server = CreateServer("Embedded");
        server.RdpUsername = "user";
        server.RdpPasswordEncrypted = "not-valid-base64-at-all!!!";
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
        Assert.Equal(0, launcher.LaunchCalls);
        Assert.Equal(1, tunnelService.ReleaseCount);
        Assert.Equal(53389, tunnelService.ReleasedLocalPort);
    }

    [Fact]
    public async Task ConnectAsync_ForceExternalPostTunnelLaunchFailureReleasesTunnelReference()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = 53389
        };
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher();
        RdpHandler handler = CreateHandler(tunnelService, launcher);
        ServerProfileDto server = CreateServer("Embedded");
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.False(result.Success);
        Assert.Equal(1, launcher.LaunchCalls);
        Assert.Equal(1, tunnelService.ReleaseCount);
        Assert.Equal(53389, tunnelService.ReleasedLocalPort);
    }

    [Fact]
    public async Task ConnectAsync_ForceExternalSuccessReleasesTunnelReferenceOnProcessExit()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = 53389
        };
        FakeLaunchedRdpClientProcess process = new FakeLaunchedRdpClientProcess(4242);
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = process
        };
        RdpHandler handler = CreateHandler(tunnelService, launcher);
        ServerProfileDto server = CreateServer("Embedded");
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.True(result.Success);
        Assert.Equal(0, tunnelService.ReleaseCount);

        process.RaiseExited();

        Assert.Equal(1, tunnelService.ReleaseCount);
        Assert.Equal(53389, tunnelService.ReleasedLocalPort);
    }

    [Fact]
    public void CreateStartInfo_ResolvesMstscUnderTheSystemDirectoryWithAnExplicitWorkingDirectory()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Assert.False(
            string.IsNullOrEmpty(systemDirectory),
            "This assertion documents the precondition: the system directory must be known.");

        ProcessStartInfo startInfo = MstscRdpExternalClientLauncher.CreateStartInfo(
            @"C:\Temp\heimdall_srv_0.rdp");

        Assert.True(
            Path.IsPathFullyQualified(startInfo.FileName),
            $"mstsc.exe must not be resolved through the CreateProcess search order: '{startInfo.FileName}'.");
        Assert.Equal(
            Path.Combine(systemDirectory, MstscRdpExternalClientLauncher.MstscExecutableName),
            startInfo.FileName);
        Assert.Equal(systemDirectory, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal("\"C:\\Temp\\heimdall_srv_0.rdp\"", startInfo.Arguments);
    }

    [Fact]
    public void SweepStaleRdpArtifacts_DeletesOnlyTheOrphansOlderThanTheThreshold()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"heimdall-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            DateTime utcNow = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            string stale = WriteArtifact(directory, "heimdall_srv_stale.rdp", utcNow.AddHours(-2));
            string fresh = WriteArtifact(directory, "heimdall_srv_fresh.rdp", utcNow.AddSeconds(-30));
            string foreign = WriteArtifact(directory, "someone_else.rdp", utcNow.AddHours(-2));
            string otherKind = WriteArtifact(directory, "heimdall_ssh_pw_old.tmp", utcNow.AddHours(-2));
            List<string> deleted = [];

            int count = RdpHandler.SweepStaleRdpArtifacts(
                directory,
                RdpHandler.StaleRdpArtifactMaxAge,
                utcNow,
                path =>
                {
                    deleted.Add(path);
                    File.Delete(path);
                });

            Assert.Equal(1, count);
            Assert.Equal([stale], deleted);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(foreign));
            Assert.True(File.Exists(otherKind));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SweepStaleRdpArtifacts_MissingDirectory_ReportsNothingSweptWithoutThrowing()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"heimdall-absent-{Guid.NewGuid():N}");
        bool deleteCalled = false;

        int count = RdpHandler.SweepStaleRdpArtifacts(
            directory,
            RdpHandler.StaleRdpArtifactMaxAge,
            DateTime.UtcNow,
            _ => deleteCalled = true);

        Assert.Equal(0, count);
        Assert.False(deleteCalled);
    }

    [Fact]
    public async Task ConnectAsync_ForceExternal_SweepsStaleArtifactsBeforeCreatingTheNewOne()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher
        {
            ProcessToReturn = new FakeLaunchedRdpClientProcess(4242)
        };
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager
        {
            CredentialWritten = true
        };
        int sweepCalls = 0;
        bool artifactExistedAtSweep = true;
        RdpHandler handler = CreateHandler(
            launcher,
            credentialManager,
            new LocalizationManager(),
            deleteRdpFile: File.Delete,
            artifactCleanupDelay: _ => Task.CompletedTask,
            sweepStaleRdpArtifacts: () =>
            {
                sweepCalls++;
                artifactExistedAtSweep = launcher.LastRdpFilePath is not null;
            });
        ServerProfileDto server = CreateCredentialedServer();
        AppSettings settings = new AppSettings
        {
            RdpArtifactCleanupDelayMs = 1,
            RdpCredentialAutofillTimeoutMs = 1
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            settings,
            CancellationToken.None,
            RdpModeOverride.ForceExternal);

        Assert.True(result.Success);
        Assert.Equal(1, sweepCalls);
        Assert.False(artifactExistedAtSweep);
        Assert.NotNull(launcher.LastRdpFilePath);
        Assert.Matches(
            "^heimdall_.*\\.rdp$",
            Path.GetFileName(launcher.LastRdpFilePath!));
    }

    [Fact]
    public async Task ConnectAsync_ForceEmbedded_DoesNotSweep()
    {
        TrackingRdpExternalClientLauncher launcher = new TrackingRdpExternalClientLauncher();
        TrackingRdpCredentialManager credentialManager = new TrackingRdpCredentialManager();
        int sweepCalls = 0;
        RdpHandler handler = CreateHandler(
            launcher,
            credentialManager,
            new LocalizationManager(),
            sweepStaleRdpArtifacts: () => sweepCalls++);
        ServerProfileDto server = CreateCredentialedServer();

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None,
            RdpModeOverride.ForceEmbedded);

        Assert.True(result.Success);
        Assert.Equal(0, sweepCalls);
    }

    private static string WriteArtifact(string directory, string fileName, DateTime lastWriteUtc)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "full address:s:127.0.0.1");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Theory]
    [InlineData("External", RdpModeOverride.UseProfile, "External")]
    [InlineData("Embedded", RdpModeOverride.ForceExternal, "External")]
    [InlineData("External", RdpModeOverride.ForceEmbedded, "Embedded")]
    public void ResolveEffectiveMode_HonorsOneShotOverride(
        string profileMode,
        RdpModeOverride rdpModeOverride,
        string expectedMode)
    {
        var server = CreateServer(profileMode);

        var actualMode = RdpHandler.ResolveEffectiveMode(server, rdpModeOverride);

        Assert.Equal(expectedMode, actualMode);
        Assert.Equal(profileMode, server.RdpMode);
    }

    private static RdpHandler CreateHandler(IRdpExternalClientLauncher launcher)
    {
        return CreateHandler(new PassThroughTunnelService(), launcher);
    }

    private static RdpHandler CreateHandler(
        ITunnelService tunnelService,
        IRdpExternalClientLauncher launcher)
    {
        return CreateHandler(tunnelService, new LocalizationManager(), launcher);
    }

    private static RdpHandler CreateHandler(
        ITunnelService tunnelService,
        LocalizationManager localizer,
        IRdpExternalClientLauncher launcher)
    {
        return new RdpHandler(
            tunnelService,
            new ConnectionStateMachine(),
            localizer,
            launcher);
    }

    private static RdpHandler CreateHandler(
        IRdpExternalClientLauncher launcher,
        IRdpCredentialManager credentialManager,
        LocalizationManager localizer,
        RdpCredentialAutofillOperation? credentialAutofill = null,
        Action<string>? deleteRdpFile = null,
        Func<TimeSpan, Task>? artifactCleanupDelay = null,
        Action? sweepStaleRdpArtifacts = null)
    {
        return new RdpHandler(
            new PassThroughTunnelService(),
            new ConnectionStateMachine(),
            localizer,
            launcher,
            credentialManager: credentialManager,
            decryptPassword: _ => "password",
            credentialAutofill: credentialAutofill,
            deleteRdpFile: deleteRdpFile,
            artifactCleanupDelay: artifactCleanupDelay,
            sweepStaleRdpArtifacts: sweepStaleRdpArtifacts);
    }

    /// <summary>
    /// Completes when the token is cancelled. Returns false immediately for a token no
    /// source can ever cancel, so a watcher wired to CancellationToken.None is reported as
    /// uncancellable rather than as a slow cancellation.
    /// </summary>
    private static async Task<bool> WaitForCancellationAsync(CancellationToken token, TimeSpan budget)
    {
        if (!token.CanBeCanceled)
        {
            return false;
        }

        TaskCompletionSource cancelled =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelled.TrySetResult()))
        {
            Task settled = await Task.WhenAny(cancelled.Task, Task.Delay(budget));
            return ReferenceEquals(settled, cancelled.Task);
        }
    }

    private static ServerProfileDto CreateServer(string rdpMode) =>
        new()
        {
            Id = "rdp-test",
            DisplayName = "RDP Test",
            RemoteServer = "127.0.0.1",
            RemotePort = 3389,
            ConnectionType = "RDP",
            RdpMode = rdpMode,
            UseDirectConnection = true
        };

    private static ServerProfileDto CreateCredentialedServer()
    {
        ServerProfileDto server = CreateServer("External");
        server.RdpUsername = "user";
        server.RdpPasswordEncrypted = "encrypted";
        return server;
    }

    private sealed class PassThroughTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            return Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class FakeTunnelService : ITunnelService
    {
        public bool UsesTunnel { get; init; }
        public string TargetHost { get; init; } = "";
        public int TargetPort { get; init; }
        public int ReleaseCount { get; private set; }
        public int ReleasedLocalPort { get; private set; }
        public bool? LastPreferDistinctLoopback { get; private set; }

        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            LastPreferDistinctLoopback = preferDistinctLoopback;
            string host = UsesTunnel ? TargetHost : server.RemoteServer;
            int port = UsesTunnel ? TargetPort : remotePort;
            return Task.FromResult(new TunnelSetupOutcome(true, UsesTunnel, host, port, (string?)null, null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
            ReleaseCount++;
            ReleasedLocalPort = localPort;
        }
    }

    private sealed class TrackingRdpExternalClientLauncher : IRdpExternalClientLauncher
    {
        public int LaunchCalls { get; private set; }

        public string? LastRdpFilePath { get; private set; }

        public bool? FileExistedAtLaunch { get; private set; }

        public ILaunchedRdpClientProcess? ProcessToReturn { get; init; }

        public Exception? ExceptionToThrow { get; init; }

        public ILaunchedRdpClientProcess? Launch(string rdpFilePath)
        {
            LaunchCalls++;
            LastRdpFilePath = rdpFilePath;
            FileExistedAtLaunch = File.Exists(rdpFilePath);
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return ProcessToReturn;
        }
    }

    private sealed class TrackingRdpCredentialManager : IRdpCredentialManager
    {
        public bool CredentialWritten { get; init; }

        public Exception? DeleteException { get; init; }

        public int WriteCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public string? LastWriteMarker { get; private set; }

        public string? LastDeleteMarker { get; private set; }

        public TaskCompletionSource DeleteObserved { get; } =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public string CreateOwnershipMarker()
        {
            return "Heimdall:RDP:test-launch";
        }

        public bool WriteDomainCredential(
            string targetName,
            string username,
            string password,
            string ownershipMarker,
            out bool credentialWritten,
            out string? error)
        {
            WriteCalls++;
            LastWriteMarker = ownershipMarker;
            credentialWritten = CredentialWritten;
            error = null;
            return true;
        }

        public bool DeleteCredential(
            string targetName,
            string ownershipMarker,
            out bool credentialDeleted,
            out string? error)
        {
            DeleteCalls++;
            LastDeleteMarker = ownershipMarker;
            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            credentialDeleted = true;
            error = null;
            DeleteObserved.TrySetResult();
            return true;
        }
    }

    private sealed class FakeLaunchedRdpClientProcess(int id) : ILaunchedRdpClientProcess
    {
        private EventHandler? exited;

        public int Id { get; } = id;

        public int ExitCode => 0;

        public bool EnableRaisingEvents { get; set; }

        public event EventHandler Exited
        {
            add => exited += value;
            remove => exited -= value;
        }

        public void RaiseExited()
        {
            EventHandler? handler = exited;
            handler?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }
}
