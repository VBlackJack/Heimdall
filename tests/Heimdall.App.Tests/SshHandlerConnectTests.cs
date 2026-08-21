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
using System.Net;
using System.Net.Sockets;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.SessionDiagnostics;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SshHandlerConnectTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConnectAsync_CallerCancellation_PropagatesAndReleasesTunnel()
    {
        const int targetPort = 49152;
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = targetPort
        };
        using SshHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateGatewayServer();
        using CancellationTokenSource cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ConnectAsync(
            server,
            new AppSettings(),
            cancellationSource.Token));

        Assert.Equal(1, tunnelService.ReleaseCount);
        Assert.Equal(targetPort, tunnelService.ReleasedLocalPort);
    }

    // SSH-013 binding, observed at runtime rather than read off the source. An independent review
    // showed the earlier source scan was defeated by one inserted line: the declaration and the call
    // both still matched while an assignment between them handed back the configured path. So the
    // two candidate paths are made to differ in whether they can start at all, and the outcome says
    // which one was used - no seam on PipeModeSession required.
    [Fact]
    public async Task ConnectSshViaPlinkAsync_StartsTheLauncherOnThePathTheLeaseResolved()
    {
        const int targetPort = 49159;

        // The configured path exists, so it resolves, but it is not a runnable image.
        string configured = Path.Combine(Path.GetTempPath(), $"heimdall-unrunnable-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(configured, []);

        // The lease's path is the real launcher. Only a handler that starts the lease's path gets a
        // process; one that starts the configured path fails at Process.Start.
        string runnable = Path.Combine(Path.GetTempPath(), $"heimdall-runnable-{Guid.NewGuid():N}.exe");
        File.Copy(ShippedLauncherPath(), runnable);

        string? deletedPasswordPath = null;
        ConnectionResult? result = null;

        try
        {
            FakeTunnelService tunnelService = new FakeTunnelService();
            HostKeyStore hostKeyStore = new HostKeyStore();
            hostKeyStore.Trust(
                "server01.contoso.local",
                DefaultPorts.Ssh,
                "SHA256:stored-test-fingerprint");
            HostKeyTrustService hostKeyTrustService = new HostKeyTrustService(hostKeyStore);
            using SshHandler handler = CreateHandler(
                tunnelService,
                hostKeyTrustService,
                new FakePlinkHostKeyProbe(null),
                deletePlinkPasswordFile: path =>
                {
                    deletedPasswordPath = path;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        File.Delete(path);
                    }
                },
                plinkAttestation: _ => new PlinkAttestationLease(
                    new FileStream(runnable, FileMode.Open, FileAccess.Read, FileShare.Read),
                    runnable));

            ServerProfileDto server = CreateGatewayServer();
            server.SshPasswordEncrypted = CredentialProtector.Protect("temporary-password");
            AppSettings settings = new AppSettings
            {
                PlinkPath = configured
            };

            result = await handler.ConnectSshViaPlinkAsync(
                server,
                settings,
                "127.0.0.1",
                targetPort,
                usesTunnel: true,
                originalFailure: null,
                CancellationToken.None);

            // Starting the configured path would have thrown at Process.Start and produced a failed
            // result. Success means the launcher was started on the path the lease resolved.
            Assert.True(
                result.Success,
                $"The launcher was not started on the lease's path: {result.ErrorMessage}");
        }
        finally
        {
            if (result?.Session is TerminalSessionResult terminal)
            {
                terminal.Session.Dispose();
            }

            if (!string.IsNullOrWhiteSpace(deletedPasswordPath))
            {
                TemporaryFileCleanup.Delete(deletedPasswordPath);
            }

            TemporaryFileCleanup.Delete(configured);
            TemporaryFileCleanup.Delete(runnable);
        }
    }

    private static string ShippedLauncherPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Heimdall.App", "Assets", "Tools", "plink.exe");
    }

    // SSH-013 closure. Without a login name the launcher waits and writes nothing, so the first byte
    // that normally proves it read the password file never arrives and the secret would sit on disk
    // for the whole session - measured against a live server. The product now refuses instead, and
    // refuses early: before the password dialog, before any host-key probe or trust mutation, before
    // the launcher is identified, and before the file exists at all.
    [Theory]
    [InlineData(null, true, null)]          // stored password, no key
    [InlineData("", true, null)]
    [InlineData("   ", true, null)]
    [InlineData(null, true, @"C:\keys\id.ppk")]  // stored password AND a key
    [InlineData("   ", true, @"C:\keys\id.ppk")]
    [InlineData(null, false, null)]         // neither password nor key: this path would ask for one
    [InlineData("   ", false, null)]
    public async Task ConnectSshViaPlinkAsync_PasswordBackedWithoutUsername_RefusesBeforeAnythingIsMaterialised(
        string? username,
        bool storedPassword,
        string? keyPath)
    {
        const int targetPort = 49161;
        string plinkPath = Path.GetTempFileName();
        try
        {
            FakeTunnelService tunnelService = new();
            HostKeyStore hostKeyStore = new();
            HostKeyTrustService hostKeyTrustService = new(hostKeyStore);
            FakePlinkHostKeyProbe probe = new(null);
            int attestations = 0;
            int deletes = 0;

            // Kept, not discarded: the previous version passed an anonymous list here and asserted
            // on a counter nothing ever incremented, so "no dialog" could not fail.
            List<string> dialogCalls = [];

            using SshHandler handler = CreateHandler(
                tunnelService,
                hostKeyTrustService,
                probe,
                deletePlinkPasswordFile: _ => deletes++,
                plinkAttestation: _ =>
                {
                    attestations++;
                    return PlinkAttestationLease.NotAttested;
                },
                dialogService: new PromptingDialogService(dialogCalls, "should-never-be-asked"));

            ServerProfileDto server = CreateGatewayServer();
            server.SshUsername = username;
            server.SshKeyPath = keyPath;
            server.SshPasswordEncrypted = storedPassword
                ? CredentialProtector.Protect("stored-password")
                : null;

            AppSettings settings = new() { PlinkPath = plinkPath };

            ConnectionResult result = await handler.ConnectSshViaPlinkAsync(
                server, settings, "127.0.0.1", targetPort,
                usesTunnel: true, originalFailure: null, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(
                new LocalizationManager()[SshLocalizationKeys.ErrorSshUsernameRequiredForPassword],
                result.ErrorMessage);
            Assert.Equal(
                SshLocalizationKeys.ErrorSshUsernameRequiredForPassword,
                result.Failure?.MessageKey);

            // Nothing was asked, probed, identified, written or started.
            Assert.Empty(dialogCalls);
            Assert.Equal(0, probe.CallCount);
            Assert.Equal(0, attestations);
            Assert.Equal(0, deletes);
            Assert.Null(result.Session);
            Assert.Empty(hostKeyStore.GetAllEntries());

            // No scan of the temporary directory here. Every password-file writer in the product
            // shares one prefix in one directory shared by every test process, so a file another
            // assembly is legitimately using in that instant is indistinguishable from one this
            // handler wrote. The counters above carry the same property without that ambiguity: the
            // launcher was never identified and never attested, and the file is written inside the
            // launch it never reached.

            // And the tunnel reference is given back exactly once.
            Assert.Equal(1, tunnelService.ReleaseCount);
            Assert.Equal(targetPort, tunnelService.ReleasedLocalPort);
        }
        finally
        {
            TemporaryFileCleanup.Delete(plinkPath);
        }
    }

    // A key with no password never writes the file, so the new refusal must not touch it. With the
    // username simply absent the pre-existing path continues to its own next result.
    [Fact]
    public async Task ConnectSshViaPlinkAsync_KeyOnlyWithoutUsername_IsNotRefusedByTheNewGuard()
    {
        const int targetPort = 49162;
        string plinkPath = Path.GetTempFileName();
        string keyPath = Path.GetTempFileName();
        try
        {
            FakeTunnelService tunnelService = new();
            HostKeyStore hostKeyStore = new();
            using SshHandler handler = CreateHandler(
                tunnelService,
                new HostKeyTrustService(hostKeyStore),
                new FakePlinkHostKeyProbe(null));

            ServerProfileDto server = CreateGatewayServer();
            server.SshUsername = null;
            server.SshKeyPath = keyPath;
            server.SshPasswordEncrypted = null;

            AppSettings settings = new() { PlinkPath = plinkPath };

            ConnectionResult result = await handler.ConnectSshViaPlinkAsync(
                server, settings, "127.0.0.1", targetPort,
                usesTunnel: true, originalFailure: null, CancellationToken.None);

            Assert.NotEqual(
                SshLocalizationKeys.ErrorSshUsernameRequiredForPassword,
                result.Failure?.MessageKey);

            // This test used to add that no password file appeared on disk. It cannot: the check
            // read a directory shared with every other test process, where every writer in the
            // product uses the same prefix, so a file another assembly was legitimately using was
            // indistinguishable from one this call wrote. What remains is the claim this test is
            // named for.
        }
        finally
        {
            // Deliberately not sweeping the shared temporary directory. Doing so deleted files that
            // other test processes were still using, which turned this test into a cause of their
            // failures as well as a victim of theirs.
            TemporaryFileCleanup.Delete(plinkPath);
            TemporaryFileCleanup.Delete(keyPath);
        }
    }

    // A username that is present but rejected by input validation keeps its own message: the new
    // refusal is about a missing name, not a malformed one.
    [Fact]
    public async Task ConnectSshViaPlinkAsync_MalformedUsername_KeepsTheValidationMessage()
    {
        const int targetPort = 49163;
        string plinkPath = Path.GetTempFileName();

        try
        {
            FakeTunnelService tunnelService = new();
            using SshHandler handler = CreateHandler(
                tunnelService,
                new HostKeyTrustService(new HostKeyStore()),
                new FakePlinkHostKeyProbe(null));

            ServerProfileDto server = CreateGatewayServer();
            server.SshUsername = "bad user;rm";
            server.SshPasswordEncrypted = CredentialProtector.Protect("stored-password");

            AppSettings settings = new() { PlinkPath = plinkPath };

            ConnectionResult result = await handler.ConnectSshViaPlinkAsync(
                server, settings, "127.0.0.1", targetPort,
                usesTunnel: true, originalFailure: null, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(SshLocalizationKeys.ErrorInvalidSshUsername, result.Failure?.MessageKey);
        }
        finally
        {
            TemporaryFileCleanup.Delete(plinkPath);
        }
    }

    // SSH-013 ordering. The password dialog can hold this thread for as long as the user takes, so
    // an attestation taken before it describes an image that a legitimate update could have replaced
    // before the launch. The dialog must therefore be finished first. Only a profile with neither a
    // stored password nor a key reaches the prompt, which is why this case exists alongside the one
    // below.
    [Fact]
    public async Task ConnectSshViaPlinkAsync_AttestsOnlyAfterThePasswordDialogHasReturned()
    {
        const int targetPort = 49158;
        string plinkPath = Path.GetTempFileName();
        string? deletedPasswordPath = null;
        List<string> order = [];

        try
        {
            FakeTunnelService tunnelService = new FakeTunnelService();
            using CancellationTokenSource cancellationSource = new CancellationTokenSource();
            HostKeyStore hostKeyStore = new HostKeyStore();
            hostKeyStore.Trust(
                "server01.contoso.local",
                DefaultPorts.Ssh,
                "SHA256:stored-test-fingerprint");
            HostKeyTrustService hostKeyTrustService = new HostKeyTrustService(hostKeyStore);
            CancelingPlinkHostKeyProbe probe = new CancelingPlinkHostKeyProbe(cancellationSource);
            using SshHandler handler = CreateHandler(
                tunnelService,
                hostKeyTrustService,
                probe,
                deletePlinkPasswordFile: path =>
                {
                    deletedPasswordPath = path;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        File.Delete(path);
                    }
                },
                plinkAttestation: _ =>
                {
                    order.Add("attest");
                    return PlinkAttestationLease.NotAttested;
                },
                dialogService: new PromptingDialogService(order, "typed-password"));

            ServerProfileDto server = CreateGatewayServer();
            server.SshPasswordEncrypted = null;
            server.SshKeyPath = null;
            AppSettings settings = new AppSettings
            {
                PlinkPath = plinkPath
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ConnectSshViaPlinkAsync(
                server,
                settings,
                "127.0.0.1",
                targetPort,
                usesTunnel: true,
                originalFailure: null,
                cancellationSource.Token));

            Assert.Equal(["prompt", "attest"], order);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(deletedPasswordPath))
            {
                TemporaryFileCleanup.Delete(deletedPasswordPath);
            }

            TemporaryFileCleanup.Delete(plinkPath);
        }
    }

    // SSH-013 wiring. The attestation is not a helper the handler may or may not consult. It must
    // be taken on the executable actually about to run, after the password dialog, before the
    // password file exists - and the pin it takes must still be held while the launcher is started,
    // then released. The lease here is a real PlinkAttestationLease over a real file, so "still
    // held" is observed the only way it can be: the pinned file cannot be deleted.
    [Fact]
    public async Task ConnectSshViaPlinkAsync_HoldsTheAttestationLeaseAcrossTheLaunch()
    {
        const int targetPort = 49157;
        string plinkPath = Path.GetTempFileName();
        string pinned = Path.GetTempFileName();
        string? deletedPasswordPath = null;
        List<string?> attested = [];
        bool? pinnedWasHeldDuringTeardown = null;

        try
        {
            FakeTunnelService tunnelService = new FakeTunnelService();
            using CancellationTokenSource cancellationSource = new CancellationTokenSource();
            HostKeyStore hostKeyStore = new HostKeyStore();
            hostKeyStore.Trust(
                "server01.contoso.local",
                DefaultPorts.Ssh,
                "SHA256:stored-test-fingerprint");
            HostKeyTrustService hostKeyTrustService = new HostKeyTrustService(hostKeyStore);
            CancelingPlinkHostKeyProbe probe = new CancelingPlinkHostKeyProbe(cancellationSource);
            using SshHandler handler = CreateHandler(
                tunnelService,
                hostKeyTrustService,
                probe,
                deletePlinkPasswordFile: path =>
                {
                    // Reached from the release handle inside the launch catch, which sits inside the
                    // lease scope. If the lease had already been released, this delete would succeed.
                    pinnedWasHeldDuringTeardown = !TryDelete(pinned);

                    deletedPasswordPath = path;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        File.Delete(path);
                    }
                },
                plinkAttestation: path =>
                {
                    attested.Add(path);

                    return new PlinkAttestationLease(
                        new FileStream(pinned, FileMode.Open, FileAccess.Read, FileShare.Read),
                        plinkPath);
                });

            ServerProfileDto server = CreateGatewayServer();
            server.SshPasswordEncrypted = CredentialProtector.Protect("temporary-password");
            AppSettings settings = new AppSettings
            {
                PlinkPath = plinkPath
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ConnectSshViaPlinkAsync(
                server,
                settings,
                "127.0.0.1",
                targetPort,
                usesTunnel: true,
                originalFailure: null,
                cancellationSource.Token));

            // Asked once, about the launcher that was resolved and would have been started.
            Assert.Equal([plinkPath], attested);

            // This test used to add that the secret was not yet on disk when the attestation was
            // asked for. That check read the whole shared temporary directory for any file with the
            // product's password-file prefix, so any other test process holding one made it true.
            // The ordering it aimed at is asserted where it is attributable, in the release and
            // lease tests, and making it provable here needs the writer itself to become a seam.

            // Held while the launch was being torn down, which is inside the lease scope.
            Assert.True(pinnedWasHeldDuringTeardown);

            // And released once the call returned, on this cancellation path as on any other.
            Assert.True(TryDelete(pinned));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(deletedPasswordPath))
            {
                TemporaryFileCleanup.Delete(deletedPasswordPath);
            }

            TryDelete(pinned);
            TemporaryFileCleanup.Delete(plinkPath);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public async Task ConnectSshViaPlinkAsync_CallerCancellationAtProcessStart_PropagatesAndCleansResources()
    {
        const int targetPort = 49153;
        string plinkPath = Path.GetTempFileName();
        string? deletedPasswordPath = null;
        try
        {
            FakeTunnelService tunnelService = new FakeTunnelService();
            int passwordDeleteCount = 0;
            using CancellationTokenSource cancellationSource = new CancellationTokenSource();
            HostKeyStore hostKeyStore = new HostKeyStore();
            hostKeyStore.Trust(
                "server01.contoso.local",
                DefaultPorts.Ssh,
                "SHA256:stored-test-fingerprint");
            HostKeyTrustService hostKeyTrustService = new HostKeyTrustService(hostKeyStore);
            CancelingPlinkHostKeyProbe probe = new CancelingPlinkHostKeyProbe(cancellationSource);
            using SshHandler handler = CreateHandler(
                tunnelService,
                hostKeyTrustService,
                probe,
                deletePlinkPasswordFile: path =>
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(path);
                    passwordDeleteCount++;
                    deletedPasswordPath = path;
                    File.Delete(path);
                });
            ServerProfileDto server = CreateGatewayServer();
            server.SshPasswordEncrypted = CredentialProtector.Protect("temporary-password");
            AppSettings settings = new AppSettings
            {
                PlinkPath = plinkPath
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ConnectSshViaPlinkAsync(
                server,
                settings,
                "127.0.0.1",
                targetPort,
                usesTunnel: true,
                originalFailure: null,
                cancellationSource.Token));

            Assert.True(cancellationSource.IsCancellationRequested);
            Assert.Equal(1, probe.CallCount);
            Assert.Equal(1, tunnelService.ReleaseCount);
            Assert.Equal(targetPort, tunnelService.ReleasedLocalPort);
            Assert.Equal(1, passwordDeleteCount);
            Assert.False(string.IsNullOrWhiteSpace(deletedPasswordPath));
            Assert.False(File.Exists(deletedPasswordPath));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(deletedPasswordPath))
            {
                TemporaryFileCleanup.Delete(deletedPasswordPath);
            }

            TemporaryFileCleanup.Delete(plinkPath);
        }
    }

    [Fact]
    public async Task Constructor_YoungPasswordOrphan_ReschedulesAndDeletesAtEligibility()
    {
        DateTime firstSweepUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        TimeSpan maxAge = TimeSpan.FromMinutes(10);
        DateTime lastWriteUtc = firstSweepUtc - TimeSpan.FromMinutes(5);
        DateTime eligibilityUtc = lastWriteUtc + maxAge;
        string orphanPath = Path.Combine(
            Path.GetTempPath(),
            $"{PlinkPasswordFileNaming.Prefix}scheduler-wiring");
        int sweepCount = 0;
        int utcNowCallCount = 0;
        TaskCompletionSource<string> deleted = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PlinkPasswordFileJanitor janitor = new PlinkPasswordFileJanitor(
            enumerateFiles: _ =>
            {
                Interlocked.Increment(ref sweepCount);
                return new string[] { orphanPath };
            },
            getLastWriteTimeUtc: _ => lastWriteUtc,
            isOwnedByCurrentUser: _ => true,
            delete: path => deleted.TrySetResult(path),
            utcNow: () => Interlocked.Increment(ref utcNowCallCount) == 1
                ? firstSweepUtc
                : eligibilityUtc,
            maxAge: maxAge);

        using SshHandler handler = CreateHandler(
            new FakeTunnelService(),
            plinkPasswordFileJanitor: janitor);

        string deletedPath = await deleted.Task.WaitAsync(TestTimeout);

        Assert.Equal(orphanPath, deletedPath);
        Assert.Equal(2, Volatile.Read(ref sweepCount));
        Assert.Equal(2, Volatile.Read(ref utcNowCallCount));
    }

    [Fact]
    public async Task ConnectAsync_TunneledConnectFailureReleasesTunnelReference()
    {
        int freePort = ReserveAndReleaseLoopbackPort();
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = freePort
        };
        SshHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateGatewayServer();

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, tunnelService.ReleaseCount);
        Assert.Equal(freePort, tunnelService.ReleasedLocalPort);
    }

    [Fact]
    public async Task ConnectAsync_DirectConnectFailureDoesNotReleaseTunnelReference()
    {
        int freePort = ReserveAndReleaseLoopbackPort();
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = false
        };
        SshHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateDirectServer(freePort);

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, tunnelService.ReleaseCount);
    }

    [Theory]
    [InlineData(false, SessionFailureStage.GenericFailure)]
    [InlineData(true, SessionFailureStage.SshGateway)]
    public async Task ConnectAsync_NetworkFailure_LocalizesMessageAndUsesActualConnectionScope(
        bool usesTunnel,
        SessionFailureStage expectedStage)
    {
        int freePort = ReserveAndReleaseLoopbackPort();
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = usesTunnel,
            TargetHost = "127.0.0.1",
            TargetPort = freePort
        };
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "fr");
        using SshHandler handler = CreateHandler(tunnelService, localizer: localizer);
        ServerProfileDto server = usesTunnel ? CreateGatewayServer() : CreateDirectServer(freePort);

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        string expectedMessage = localizer.Format("ErrorSshNetworkRefused", "127.0.0.1");
        Assert.False(result.Success);
        Assert.Equal(expectedMessage, result.ErrorMessage);
        Assert.NotNull(result.Failure);
        Assert.Equal(expectedStage, result.Failure.Stage);
        Assert.Equal(expectedMessage, result.Failure.Detail);
    }

    [Fact]
    public async Task ConnectAsync_TunnelSetupFailsWithCircularChainDependency_MapsToGatewayDiagnostic()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            SetupSucceeds = false,
            SetupFailureCode = SshFailureCode.CircularChainDependency,
            SetupErrorMessage = "Circular dependency detected in gateway chain."
        };
        using SshHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreateGatewayServer();

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
        Assert.Equal(SessionFailureStage.SshGateway, result.Failure.Stage);
        Assert.Equal("ErrorSshCircularChainDependency", result.Failure.MessageKey);
        Assert.Equal((int)SshFailureCode.CircularChainDependency, result.Failure.Code);
    }

    [Fact]
    public async Task ConnectAsync_ExternalModeFailureReleasesTunnelReference()
    {
        int freePort = ReserveAndReleaseLoopbackPort();
        string puttyPath = Path.GetTempFileName();
        try
        {
            FakeTunnelService tunnelService = new FakeTunnelService
            {
                UsesTunnel = true,
                TargetHost = "127.0.0.1",
                TargetPort = freePort
            };
            SshHandler handler = CreateHandler(tunnelService);
            ServerProfileDto server = CreateExternalGatewayServer();
            AppSettings settings = new AppSettings
            {
                PuttyPath = puttyPath
            };

            ConnectionResult result = await handler.ConnectAsync(
                server,
                settings,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(1, tunnelService.ReleaseCount);
            Assert.Equal(freePort, tunnelService.ReleasedLocalPort);
        }
        finally
        {
            TemporaryFileCleanup.Delete(puttyPath);
        }
    }

    [Fact]
    public async Task ConnectAsync_ExternalModeWithoutTrustedHostKey_RejectsBeforePuttyLaunch()
    {
        string puttyPath = Path.GetTempFileName();
        try
        {
            FakeTunnelService tunnelService = new FakeTunnelService
            {
                UsesTunnel = false
            };
            FakePlinkHostKeyProbe probe = new FakePlinkHostKeyProbe(null);
            SshHandler handler = CreateHandler(
                tunnelService,
                new NoStoredHostKeyTrustService(),
                probe);
            ServerProfileDto server = CreateExternalDirectServer();
            AppSettings settings = new AppSettings
            {
                PuttyPath = puttyPath,
                PlinkPath = Path.Combine(
                    Path.GetTempPath(),
                    $"heimdall-missing-plink-{Guid.NewGuid():N}.exe")
            };

            ConnectionResult result = await handler.ConnectAsync(
                server,
                settings,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal((int)SshFailureCode.HostKeyUnavailable, result.Failure?.Code);
            Assert.Equal("ErrorSshHostKeyUnavailable", result.Failure?.MessageKey);
            Assert.Equal(0, tunnelService.ReleaseCount);
        }
        finally
        {
            TemporaryFileCleanup.Delete(puttyPath);
        }
    }

    [Fact]
    public async Task ConnectAsync_ExternalModeWithTunnel_UsesLogicalHostKeyIdentity()
    {
        const int tunnelPort = 49152;
        string puttyPath = Path.GetTempFileName();
        try
        {
            FakeTunnelService tunnelService = new FakeTunnelService
            {
                UsesTunnel = true,
                TargetHost = "127.0.0.1",
                TargetPort = tunnelPort
            };
            var trust = new NoStoredHostKeyTrustService();
            var probe = new FakePlinkHostKeyProbe(null);
            SshHandler handler = CreateHandler(tunnelService, trust, probe);
            ServerProfileDto server = CreateExternalGatewayServer();
            server.SshUsername = "operator";
            AppSettings settings = new AppSettings
            {
                PuttyPath = puttyPath,
                PlinkPath = Path.Combine(
                    Path.GetTempPath(),
                    $"heimdall-missing-plink-{Guid.NewGuid():N}.exe")
            };

            ConnectionResult result = await handler.ConnectAsync(
                server,
                settings,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("server01.contoso.local", trust.LastGetEffectiveHost);
            Assert.Equal(DefaultPorts.Ssh, trust.LastGetEffectivePort);
            Assert.Equal(1, tunnelService.ReleaseCount);
            Assert.Equal(tunnelPort, tunnelService.ReleasedLocalPort);
        }
        finally
        {
            TemporaryFileCleanup.Delete(puttyPath);
        }
    }

    [Fact]
    public async Task ConnectSshViaPlinkAsync_EarlyFailureWithTunnel_ReleasesTunnelReference()
    {
        const int targetPort = 13389;
        FakeTunnelService tunnelService = new FakeTunnelService();
        SshHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreatePlinkServer();
        AppSettings settings = new AppSettings
        {
            PlinkPath = @"C:\nonexistent\plink.exe"
        };

        ConnectionResult result = await handler.ConnectSshViaPlinkAsync(
            server,
            settings,
            "127.0.0.1",
            targetPort,
            usesTunnel: true,
            originalFailure: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, tunnelService.ReleaseCount);
        Assert.Equal(targetPort, tunnelService.ReleasedLocalPort);
    }

    [Fact]
    public async Task ConnectSshViaPlinkAsync_EarlyFailureWithoutTunnel_DoesNotReleaseTunnelReference()
    {
        const int targetPort = 13389;
        FakeTunnelService tunnelService = new FakeTunnelService();
        SshHandler handler = CreateHandler(tunnelService);
        ServerProfileDto server = CreatePlinkServer();
        AppSettings settings = new AppSettings
        {
            PlinkPath = @"C:\nonexistent\plink.exe"
        };

        ConnectionResult result = await handler.ConnectSshViaPlinkAsync(
            server,
            settings,
            "127.0.0.1",
            targetPort,
            usesTunnel: false,
            originalFailure: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, tunnelService.ReleaseCount);
    }

    [Fact]
    public async Task ConnectSshViaPlinkAsync_WithTunnel_UsesLogicalHostKeyIdentityAndTransportProbe()
    {
        const int tunnelPort = 49152;
        string plinkPath = Path.GetTempFileName();
        try
        {
            FakeTunnelService tunnelService = new FakeTunnelService();
            var trust = new NoStoredHostKeyTrustService();
            var probe = new FakePlinkHostKeyProbe(null);
            SshHandler handler = CreateHandler(tunnelService, trust, probe);
            ServerProfileDto server = CreateGatewayServer();
            AppSettings settings = new AppSettings
            {
                PlinkPath = plinkPath
            };

            ConnectionResult result = await handler.ConnectSshViaPlinkAsync(
                server,
                settings,
                "127.0.0.1",
                tunnelPort,
                usesTunnel: true,
                originalFailure: null,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("server01.contoso.local", trust.LastGetEffectiveHost);
            Assert.Equal(DefaultPorts.Ssh, trust.LastGetEffectivePort);
            Assert.Equal("127.0.0.1", probe.LastHost);
            Assert.Equal(tunnelPort, probe.LastPort);
            Assert.Equal(1, tunnelService.ReleaseCount);
            Assert.Equal(tunnelPort, tunnelService.ReleasedLocalPort);
        }
        finally
        {
            TemporaryFileCleanup.Delete(plinkPath);
        }
    }

    // The embedded client is the default path and the least guarded of the three: the
    // external branch validates the username's shape and Plink has a guard of its own,
    // while this one used to carry a blank name all the way into SSH.NET and come back
    // with a raw ArgumentException - after the host-key prompt had already run.
    [Fact]
    public async Task ConnectAsync_EmbeddedWithoutUsername_RefusesAndReleasesTheTunnel()
    {
        const int targetPort = 49170;
        FakeTunnelService tunnelService = new()
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = targetPort
        };

        // The default host-key trust service throws if it is ever consulted, so reaching
        // it would fail this test rather than pass it silently.
        using SshHandler handler = CreateHandler(tunnelService);

        ServerProfileDto server = CreateGatewayServer();
        server.SshUsername = null;

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SshLocalizationKeys.ErrorSshUsernameRequired, result.ErrorMessage);
        Assert.Equal(
            SshLocalizationKeys.ErrorSshUsernameRequired,
            result.Failure?.MessageKey);
        Assert.Null(result.Session);
        Assert.Equal(1, tunnelService.ReleaseCount);
        Assert.Equal(targetPort, tunnelService.ReleasedLocalPort);
    }

    private static SshHandler CreateHandler(
        FakeTunnelService tunnelService,
        IHostKeyTrustService? hostKeyTrustService = null,
        IPlinkHostKeyProbe? plinkHostKeyProbe = null,
        PlinkPasswordFileJanitor? plinkPasswordFileJanitor = null,
        Action<string?>? deletePlinkPasswordFile = null,
        LocalizationManager? localizer = null,
        Func<string?, PlinkAttestationLease>? plinkAttestation = null,
        IDialogService? dialogService = null)
    {
        LocalizationManager effectiveLocalizer = localizer ?? new LocalizationManager();
        IHostKeyTrustService effectiveHostKeyTrustService =
            hostKeyTrustService ?? new ThrowingHostKeyTrustService();
        return new SshHandler(
            tunnelService,
            new ConnectionStateMachine(),
            effectiveLocalizer,
            new HostKeyStore(),
            effectiveHostKeyTrustService,
            AutoAcceptHostKeyVerifier.Instance,
            new X11ServerManager(new InMemoryConfigManager(), effectiveLocalizer),
            dialogService ?? new ThrowingDialogService(),
            plinkHostKeyProbe: plinkHostKeyProbe,
            plinkPasswordFileJanitor:
                plinkPasswordFileJanitor ?? CreateNoOpPlinkPasswordFileJanitor(),
            deletePlinkPasswordFile: deletePlinkPasswordFile,
            plinkAttestation: plinkAttestation);
    }

    private static PlinkPasswordFileJanitor CreateNoOpPlinkPasswordFileJanitor()
    {
        return new PlinkPasswordFileJanitor(enumerateFiles: _ => Array.Empty<string>());
    }

    private static ServerProfileDto CreateGatewayServer()
    {
        return new ServerProfileDto
        {
            Id = "ssh-gateway-test",
            DisplayName = "SSH Gateway Test",
            ConnectionType = "SSH",
            RemoteServer = "server01.contoso.local",
            SshPort = DefaultPorts.Ssh,
            SshMode = "Embedded",
            SshUsername = "operator",
            SshGatewayId = "gateway-01",
            UseDirectConnection = false
        };
    }

    private static ServerProfileDto CreatePlinkServer()
    {
        return new ServerProfileDto
        {
            Id = "ssh-plink-test",
            DisplayName = "SSH Plink Test",
            ConnectionType = "SSH",
            RemoteServer = "server01.contoso.local",
            SshPort = DefaultPorts.Ssh,
            // Intentionally invalid to force a deterministic early return with no network I/O.
            SshUsername = "invalid user"
        };
    }

    private static ServerProfileDto CreateExternalGatewayServer()
    {
        return new ServerProfileDto
        {
            Id = "ssh-external-gateway-test",
            DisplayName = "SSH External Gateway Test",
            ConnectionType = "SSH",
            RemoteServer = "server01.contoso.local",
            SshPort = DefaultPorts.Ssh,
            SshMode = "External",
            SshUsername = "invalid user",
            SshGatewayId = "gateway-01",
            UseDirectConnection = false
        };
    }

    private static ServerProfileDto CreateExternalDirectServer()
    {
        return new ServerProfileDto
        {
            Id = "ssh-external-direct-test",
            DisplayName = "SSH External Direct Test",
            ConnectionType = "SSH",
            RemoteServer = "127.0.0.1",
            SshPort = DefaultPorts.Ssh,
            SshMode = "External",
            SshUsername = "operator",
            UseDirectConnection = true
        };
    }

    private static ServerProfileDto CreateDirectServer(int port)
    {
        return new ServerProfileDto
        {
            Id = "ssh-direct-test",
            DisplayName = "SSH Direct Test",
            ConnectionType = "SSH",
            RemoteServer = "127.0.0.1",
            SshPort = port,
            SshMode = "Embedded",
            SshUsername = "operator",
            UseDirectConnection = true
        };
    }

    private static int ReserveAndReleaseLoopbackPort()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            IPEndPoint endpoint = (IPEndPoint)listener.LocalEndpoint;
            return endpoint.Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class FakeTunnelService : ITunnelService
    {
        public bool UsesTunnel { get; init; }
        public string TargetHost { get; init; } = "";
        public int TargetPort { get; init; }
        public int ReleaseCount { get; private set; }
        public int ReleasedLocalPort { get; private set; }
        public bool SetupSucceeds { get; init; } = true;
        public SshFailureCode? SetupFailureCode { get; init; }
        public string? SetupErrorMessage { get; init; }

        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            if (!SetupSucceeds)
            {
                return Task.FromResult(
                    new TunnelSetupOutcome(false, false, string.Empty, 0, SetupErrorMessage, SetupFailureCode));
            }

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

    private sealed class FakePlinkHostKeyProbe : IPlinkHostKeyProbe
    {
        private readonly PlinkHostKeyPresentation? _presentation;

        public FakePlinkHostKeyProbe(PlinkHostKeyPresentation? presentation)
        {
            _presentation = presentation;
        }

        public int CallCount { get; private set; }
        public string? LastHost { get; private set; }
        public int? LastPort { get; private set; }

        public Task<PlinkHostKeyPresentation?> ProbeAsync(
            string plinkPath,
            string host,
            int port,
            string? username,
            int timeoutMs,
            CancellationToken ct)
        {
            CallCount++;
            LastHost = host;
            LastPort = port;
            return Task.FromResult(_presentation);
        }
    }

    private sealed class CancelingPlinkHostKeyProbe(CancellationTokenSource cancellationSource)
        : IPlinkHostKeyProbe
    {
        public int CallCount { get; private set; }

        public Task<PlinkHostKeyPresentation?> ProbeAsync(
            string plinkPath,
            string host,
            int port,
            string? username,
            int timeoutMs,
            CancellationToken ct)
        {
            CallCount++;
            cancellationSource.Cancel();
            return Task.FromResult<PlinkHostKeyPresentation?>(null);
        }
    }

    private sealed class NoStoredHostKeyTrustService : IHostKeyTrustService
    {
        public event Action<string, HostKeyEntry>? EntryTrusted { add { } remove { } }
        public event Action<string>? EntryRemoved { add { } remove { } }
        public event Action<string, HostKeyEntry, HostKeyEntry>? EntryReplaced { add { } remove { } }

        public string? LastGetEffectiveHost { get; private set; }
        public int? LastGetEffectivePort { get; private set; }

        public HostKeyEntry? GetEntry(string host, int port) => null;

        public HostKeyEntry? GetEffectiveEntry(string host, int port)
        {
            LastGetEffectiveHost = host;
            LastGetEffectivePort = port;
            return null;
        }

        public IReadOnlyList<(string HostPort, HostKeyEntry Entry)> GetAllEntries() => [];

        public HostKeyVerifyResult Verify(
            string host,
            int port,
            string presentedFingerprint,
            string algorithm)
        {
            throw new NotSupportedException();
        }

        public void Trust(
            string host,
            int port,
            string fingerprint,
            string algorithm,
            HostKeySource source,
            string? publicKeyBase64 = null)
        {
            throw new NotSupportedException();
        }

        public void TrustForSession(
            string host,
            int port,
            string fingerprint,
            string algorithm,
            string? publicKeyBase64 = null)
        {
            throw new NotSupportedException();
        }

        public void Import(
            string host,
            int port,
            string fingerprint,
            string algorithm,
            DateTimeOffset importedAt,
            string? publicKeyBase64 = null)
        {
            throw new NotSupportedException();
        }

        public bool Remove(string host, int port) => false;
    }

    private sealed class ThrowingHostKeyTrustService : IHostKeyTrustService
    {
        public event Action<string, HostKeyEntry>? EntryTrusted { add { } remove { } }
        public event Action<string>? EntryRemoved { add { } remove { } }
        public event Action<string, HostKeyEntry, HostKeyEntry>? EntryReplaced { add { } remove { } }

        public HostKeyEntry? GetEntry(string host, int port)
        {
            throw new NotImplementedException();
        }

        public HostKeyEntry? GetEffectiveEntry(string host, int port)
        {
            throw new NotImplementedException();
        }

        public IReadOnlyList<(string HostPort, HostKeyEntry Entry)> GetAllEntries()
        {
            throw new NotImplementedException();
        }

        public HostKeyVerifyResult Verify(
            string host,
            int port,
            string presentedFingerprint,
            string algorithm)
        {
            throw new NotImplementedException();
        }

        public void Trust(
            string host,
            int port,
            string fingerprint,
            string algorithm,
            HostKeySource source,
            string? publicKeyBase64 = null)
        {
            throw new NotImplementedException();
        }

        public void TrustForSession(
            string host,
            int port,
            string fingerprint,
            string algorithm,
            string? publicKeyBase64 = null)
        {
            throw new NotImplementedException();
        }

        public void Import(
            string host,
            int port,
            string fingerprint,
            string algorithm,
            DateTimeOffset importedAt,
            string? publicKeyBase64 = null)
        {
            throw new NotImplementedException();
        }

        public bool Remove(string host, int port)
        {
            throw new NotImplementedException();
        }
    }

    // Returns a password and records when it did, so the order of the dialog and the attestation
    // is observable rather than assumed.
    private sealed class PromptingDialogService(List<string> order, string password) : ThrowingDialogService
    {
        public override Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default)
        {
            order.Add("prompt");
            return Task.FromResult<string?>(password);
        }
    }

    private class ThrowingDialogService : IDialogService
    {
        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            throw new NotImplementedException();
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
        {
            throw new NotImplementedException();
        }

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
        {
            throw new NotImplementedException();
        }

        public virtual Task<string?> ShowPasswordInputAsync(
            string title,
            string prompt,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
        {
            throw new NotImplementedException();
        }

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
        {
            throw new NotImplementedException();
        }

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
        {
            throw new NotImplementedException();
        }

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
        {
            throw new NotImplementedException();
        }

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
        {
            throw new NotImplementedException();
        }

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
        {
            throw new NotImplementedException();
        }

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
        {
            throw new NotImplementedException();
        }

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
        {
            throw new NotImplementedException();
        }

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
        {
            throw new NotImplementedException();
        }

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
        {
            throw new NotImplementedException();
        }

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
        {
            throw new NotImplementedException();
        }

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
        {
            throw new NotImplementedException();
        }

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
        {
            throw new NotImplementedException();
        }

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
        {
            throw new NotImplementedException();
        }

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<string?> ShowBulkEditUsernameAsync(
            int count,
            string? initialUsername,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public void ShowError(string title, string message)
        {
            throw new NotImplementedException();
        }

        public void ShowInfo(string title, string message)
        {
            throw new NotImplementedException();
        }

        public void ShowWarning(string title, string message)
        {
            throw new NotImplementedException();
        }
    }
}
