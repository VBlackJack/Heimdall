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

using Heimdall.Core.Ssh;
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

public class TunnelManagerTests : IDisposable
{
    private readonly TunnelManager _manager = new();

    public void Dispose()
    {
        _manager.Dispose();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static TunnelInfo MakeInfo(
        int localPort,
        string server = "gw.example.com",
        string localBindHost = LoopbackBinding.DefaultHost)
    {
        return new TunnelInfo(
            ServerName: server,
            LocalPort: localPort,
            RemoteHost: "target.internal",
            RemotePort: 3389,
            StartedAt: DateTime.UtcNow,
            IsAlive: true)
        {
            LocalBindHost = localBindHost
        };
    }

    private sealed class FakeHandle : IDisposable
    {
        private int _disposeCount;

        public bool Disposed => DisposeCount > 0;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class BlockingHandle : IDisposable
    {
        private readonly ManualResetEventSlim _disposeStarted = new(false);
        private readonly ManualResetEventSlim _allowDispose = new(false);
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool WaitForDisposeStarted(TimeSpan timeout)
            => _disposeStarted.Wait(timeout);

        public void AllowDispose()
            => _allowDispose.Set();

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _disposeStarted.Set();
            if (!_allowDispose.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting for test to release tunnel dispose.");
            }
        }
    }

    private class RecordingForwardedPortLocal : ForwardedPortLocal
    {
        public RecordingForwardedPortLocal()
            : base("127.0.0.1", 0u, "remote.invalid", 22u)
        {
        }

        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingStopForwardedPortLocal : RecordingForwardedPortLocal
    {
        public override bool IsStarted => true;

        public int StopCount { get; private set; }

        public override void Stop()
        {
            StopCount++;
            throw new InvalidOperationException("Synthetic forwarded port stop failure.");
        }
    }

    private class RecordingSshClient : SshClient
    {
        public RecordingSshClient()
            : base("127.0.0.1", "u", "p")
        {
        }

        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
        }
    }

    private sealed class ThrowingDisposeSshClient : RecordingSshClient
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new InvalidOperationException("Synthetic SSH client dispose failure.");
        }
    }

    private bool RegisterFake(int localPort, IDisposable? handle = null, Func<bool>? isAlive = null)
    {
        return _manager.TryRegisterExternalTunnel(
            MakeInfo(localPort),
            handle ?? new FakeHandle(),
            isAlive ?? (() => true));
    }

    private static SshConnectionParams MakeSshParams(
        string host = "127.0.0.1",
        int port = 22) =>
        new()
        {
            Host = host,
            Port = port,
            Username = "testuser",
            Password = "secret",
            ConnectTimeout = TimeSpan.FromMilliseconds(100)
        };

    private static HostKeyStore TestHostKeyStore() => new();

    private static IHostKeyVerifier TestHostKeyVerifier() => RejectingHostKeyVerifier.Instance;

    // ── Initial state ─────────────────────────────────────────────────

    [Fact]
    public void NewManager_HasNoActiveTunnels()
    {
        Assert.Empty(_manager.GetActiveTunnels());
    }

    [Fact]
    public void HasTunnel_ReturnsFalse_ForUntrackedPort()
    {
        Assert.False(_manager.HasTunnel(12345));
    }

    // ── TryRegisterExternalTunnel ─────────────────────────────────────

    [Fact]
    public void TryRegisterExternalTunnel_Succeeds_ForNewPort()
    {
        var result = RegisterFake(10001);

        Assert.True(result);
        Assert.True(_manager.HasTunnel(10001));
    }

    [Fact]
    public void TryRegisterExternalTunnel_ReturnsFalse_ForDuplicatePort()
    {
        RegisterFake(10001);

        var result = RegisterFake(10001);

        Assert.False(result);
    }

    [Fact]
    public void TryRegisterExternalTunnel_DisposesHandle_OnDuplicatePort()
    {
        RegisterFake(10001);
        var duplicateHandle = new FakeHandle();

        _manager.TryRegisterExternalTunnel(MakeInfo(10001), duplicateHandle, () => true);

        Assert.True(duplicateHandle.Disposed);
    }

    [Fact]
    public void TryRegisterExternalTunnel_ThrowsArgumentNull_ForNullInfo()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _manager.TryRegisterExternalTunnel(null!, new FakeHandle(), () => true));
    }

    [Fact]
    public void TryRegisterExternalTunnel_ThrowsArgumentNull_ForNullHandle()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _manager.TryRegisterExternalTunnel(MakeInfo(10001), null!, () => true));
    }

    [Fact]
    public void TryRegisterExternalTunnel_ThrowsArgumentNull_ForNullIsAlive()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _manager.TryRegisterExternalTunnel(MakeInfo(10001), new FakeHandle(), null!));
    }

    [Fact]
    public void TryRegisterExternalTunnel_ThrowsObjectDisposed_AfterDispose()
    {
        _manager.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            _manager.TryRegisterExternalTunnel(MakeInfo(10001), new FakeHandle(), () => true));
    }

    // ── GetActiveTunnels ──────────────────────────────────────────────

    [Fact]
    public void GetActiveTunnels_ReturnsRegisteredExternalTunnels()
    {
        RegisterFake(10001, isAlive: () => true);
        RegisterFake(10002, isAlive: () => false);

        var tunnels = _manager.GetActiveTunnels();

        Assert.Equal(2, tunnels.Count);
    }

    [Fact]
    public void GetActiveTunnels_ReflectsIsAliveStatus()
    {
        var alive = true;
        RegisterFake(10001, isAlive: () => alive);

        var tunnels = _manager.GetActiveTunnels();
        Assert.True(tunnels[0].IsAlive);

        alive = false;
        tunnels = _manager.GetActiveTunnels();
        Assert.False(tunnels[0].IsAlive);
    }

    [Fact]
    public void GetActiveTunnels_ReturnsEmptyAfterCloseAll()
    {
        RegisterFake(10001);
        RegisterFake(10002);

        _manager.CloseAllTunnels();

        Assert.Empty(_manager.GetActiveTunnels());
    }

    // ── GetTunnel ─────────────────────────────────────────────────────

    [Fact]
    public void GetTunnel_ReturnsNull_ForUntrackedPort()
    {
        Assert.Null(_manager.GetTunnel(99999));
    }

    [Fact]
    public void GetTunnel_ReturnsInfo_ForRegisteredExternalTunnel()
    {
        RegisterFake(10001);

        var info = _manager.GetTunnel(10001);

        Assert.NotNull(info);
        Assert.Equal(10001, info.LocalPort);
        Assert.Equal("target.internal", info.RemoteHost);
        Assert.Equal(LoopbackBinding.DefaultHost, info.LocalBindHost);
    }

    [Fact]
    public void TunnelInfo_LocalBindHost_DefaultsToDefaultLoopback()
    {
        var info = new TunnelInfo(
            "gw.example.com",
            10001,
            "target.internal",
            3389,
            DateTime.UtcNow,
            true);

        Assert.Equal(LoopbackBinding.DefaultHost, info.LocalBindHost);
    }

    // ── AddReference / ReleaseReference ───────────────────────────────

    [Fact]
    public void AddReference_IncrementsRefCount()
    {
        RegisterFake(10001);

        // Initial registration adds 1 ref. Adding another should keep the tunnel alive.
        _manager.AddReference(10001);

        // Release once: should return false (still 1 ref remaining)
        var shouldClose = _manager.ReleaseReference(10001);

        Assert.False(shouldClose);
        Assert.True(_manager.HasTunnel(10001));
    }

    [Fact]
    public void ReleaseReference_ReturnsTrueAndCloses_WhenLastRefReleased()
    {
        RegisterFake(10001);

        // Registration adds 1 ref. Release it.
        var shouldClose = _manager.ReleaseReference(10001);

        Assert.True(shouldClose);
        Assert.False(_manager.HasTunnel(10001));
    }

    [Fact]
    public void ReleaseReference_OnUntrackedPort_ReturnsTrue()
    {
        // Releasing a port that was never tracked should return true (count = 0)
        var result = _manager.ReleaseReference(55555);

        Assert.True(result);
    }

    [Fact]
    public void AddReference_MultipleIncrements_RequireMatchingReleases()
    {
        RegisterFake(10001);

        // Add 2 more refs (total 3 including initial)
        _manager.AddReference(10001);
        _manager.AddReference(10001);

        // Release 1: still 2 refs
        Assert.False(_manager.ReleaseReference(10001));
        Assert.True(_manager.HasTunnel(10001));

        // Release 2: still 1 ref
        Assert.False(_manager.ReleaseReference(10001));
        Assert.True(_manager.HasTunnel(10001));

        // Release 3: last ref, tunnel closed
        Assert.True(_manager.ReleaseReference(10001));
        Assert.False(_manager.HasTunnel(10001));
    }

    [Fact]
    public void AddReference_OnUntrackedPort_DoesNotCreatePhantomReference()
    {
        _manager.AddReference(10001);
        RegisterFake(10001);

        Assert.True(_manager.ReleaseReference(10001));
        Assert.False(_manager.HasTunnel(10001));
    }

    [Fact]
    public void ReleaseReference_CalledAfterLastReference_DisposesAndNotifiesOnce()
    {
        var handle = new FakeHandle();
        var closedCount = 0;
        _manager.TunnelClosed += (_, _) => closedCount++;
        RegisterFake(10001, handle: handle);

        Assert.True(_manager.ReleaseReference(10001));
        Assert.True(_manager.ReleaseReference(10001));

        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(1, closedCount);
        Assert.False(_manager.HasTunnel(10001));
    }

    // ── CloseTunnel (with ref count) ──────────────────────────────────

    [Fact]
    public void CloseTunnel_DoesNotClose_WhenRefsRemain()
    {
        RegisterFake(10001);

        // Add an extra ref (total 2)
        _manager.AddReference(10001);

        // CloseTunnel checks ref count; tunnel should survive
        _manager.CloseTunnel(10001);

        Assert.True(_manager.HasTunnel(10001));
    }

    // ── ForceCloseTunnel ──────────────────────────────────────────────

    [Fact]
    public void ForceCloseTunnel_ClosesRegardlessOfRefCount()
    {
        RegisterFake(10001);
        _manager.AddReference(10001);
        _manager.AddReference(10001);

        _manager.ForceCloseTunnel(10001);

        Assert.False(_manager.HasTunnel(10001));
    }

    [Fact]
    public void ForceCloseTunnel_DisposesHandle()
    {
        var handle = new FakeHandle();
        RegisterFake(10001, handle: handle);

        _manager.ForceCloseTunnel(10001);

        Assert.True(handle.Disposed);
    }

    [Fact]
    public void ForceCloseTunnel_NoOp_ForUntrackedPort()
    {
        // Should not throw
        _manager.ForceCloseTunnel(99999);
    }

    [Fact]
    public void ForceCloseTunnel_CalledTwice_DisposesAndNotifiesOnce()
    {
        var handle = new FakeHandle();
        var closedCount = 0;
        _manager.TunnelClosed += (_, _) => closedCount++;
        RegisterFake(10001, handle: handle);

        _manager.ForceCloseTunnel(10001);
        _manager.ForceCloseTunnel(10001);

        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(1, closedCount);
    }

    [Fact]
    public async Task ForceCloseTunnel_DoesNotHoldRegistryLockWhileDisposing()
    {
        var blockingHandle = new BlockingHandle();
        RegisterFake(10001, handle: blockingHandle);

        Task closeTask = Task.Run(() => _manager.ForceCloseTunnel(10001));
        Assert.True(blockingHandle.WaitForDisposeStarted(TimeSpan.FromSeconds(2)));

        Task<bool> registerTask = Task.Run(
            () => _manager.TryRegisterExternalTunnel(MakeInfo(10002), new FakeHandle(), () => true));

        Task completed = await Task.WhenAny(registerTask, Task.Delay(TimeSpan.FromSeconds(2)));
        try
        {
            Assert.Same(registerTask, completed);
            Assert.True(await registerTask);
            Assert.True(_manager.HasTunnel(10002));
        }
        finally
        {
            blockingHandle.AllowDispose();
            await closeTask;
        }
    }

    [Fact]
    public async Task ForceCloseTunnel_DoesNotHoldRegistryLockWhileRaisingTunnelClosed()
    {
        using var eventStarted = new ManualResetEventSlim(false);
        using var allowEvent = new ManualResetEventSlim(false);
        void Handler(int port, string? error)
        {
            eventStarted.Set();
            Assert.True(allowEvent.Wait(TimeSpan.FromSeconds(5)));
        }

        _manager.TunnelClosed += Handler;
        RegisterFake(10001);

        Task closeTask = Task.Run(() => _manager.ForceCloseTunnel(10001));
        Assert.True(eventStarted.Wait(TimeSpan.FromSeconds(2)));

        Task<bool> registerTask = Task.Run(
            () => _manager.TryRegisterExternalTunnel(MakeInfo(10002), new FakeHandle(), () => true));

        Task completed = await Task.WhenAny(registerTask, Task.Delay(TimeSpan.FromSeconds(2)));
        try
        {
            Assert.Same(registerTask, completed);
            Assert.True(await registerTask);
            Assert.True(_manager.HasTunnel(10002));
        }
        finally
        {
            allowEvent.Set();
            await closeTask;
            _manager.TunnelClosed -= Handler;
        }
    }

    // ── CloseAllTunnels ───────────────────────────────────────────────

    [Fact]
    public void CloseAllTunnels_ClosesAllRegisteredTunnels()
    {
        var handle1 = new FakeHandle();
        var handle2 = new FakeHandle();
        RegisterFake(10001, handle: handle1);
        RegisterFake(10002, handle: handle2);

        _manager.CloseAllTunnels();

        Assert.False(_manager.HasTunnel(10001));
        Assert.False(_manager.HasTunnel(10002));
        Assert.True(handle1.Disposed);
        Assert.True(handle2.Disposed);
    }

    [Fact]
    public void CloseAllTunnels_IgnoresRefCounts()
    {
        RegisterFake(10001);
        _manager.AddReference(10001);
        _manager.AddReference(10001);

        _manager.CloseAllTunnels();

        Assert.False(_manager.HasTunnel(10001));
    }

    [Fact]
    public void CloseAllTunnels_OnEmptyManager_DoesNotThrow()
    {
        _manager.CloseAllTunnels();

        Assert.Empty(_manager.GetActiveTunnels());
    }

    // ── Events ────────────────────────────────────────────────────────

    [Fact]
    public void TunnelOpened_FiresOnRegister()
    {
        TunnelInfo? receivedInfo = null;
        _manager.TunnelOpened += info => receivedInfo = info;

        RegisterFake(10001);

        Assert.NotNull(receivedInfo);
        Assert.Equal(10001, receivedInfo.LocalPort);
    }

    [Fact]
    public void TunnelClosed_FiresOnForceClose()
    {
        int? closedPort = null;
        string? closedError = null;
        _manager.TunnelClosed += (port, error) =>
        {
            closedPort = port;
            closedError = error;
        };

        RegisterFake(10001);
        _manager.ForceCloseTunnel(10001);

        Assert.Equal(10001, closedPort);
        Assert.Null(closedError);
    }

    [Fact]
    public void TunnelClosed_FiresOnReleaseReference_WhenLastRef()
    {
        int? closedPort = null;
        _manager.TunnelClosed += (port, _) => closedPort = port;

        RegisterFake(10001);
        _manager.ReleaseReference(10001);

        Assert.Equal(10001, closedPort);
    }

    [Fact]
    public void TunnelClosed_DoesNotFire_WhenRefsRemain()
    {
        bool fired = false;
        _manager.TunnelClosed += (_, _) => fired = true;

        RegisterFake(10001);
        _manager.AddReference(10001);

        _manager.ReleaseReference(10001);

        Assert.False(fired);
    }

    [Fact]
    public void TunnelClosed_FiresForEachTunnel_OnCloseAll()
    {
        var closedPorts = new List<int>();
        _manager.TunnelClosed += (port, _) => closedPorts.Add(port);

        RegisterFake(10001);
        RegisterFake(10002);

        _manager.CloseAllTunnels();

        Assert.Contains(10001, closedPorts);
        Assert.Contains(10002, closedPorts);
    }

    [Fact]
    public void TunnelOpened_SubscriberThrows_DoesNotEscapeAndKeepsTunnelRegistered()
    {
        _manager.TunnelOpened += _ => throw new InvalidOperationException("Test subscriber failure.");

        Exception? exception = Record.Exception(() => RegisterFake(10001));

        Assert.Null(exception);
        Assert.True(_manager.HasTunnel(10001));
    }

    [Fact]
    public void TunnelClosed_SubscriberThrows_DoesNotEscapeAndRemovesTunnel()
    {
        _manager.TunnelClosed += (_, _) => throw new InvalidOperationException("Test subscriber failure.");
        RegisterFake(10001);

        Exception? exception = Record.Exception(() => _manager.ForceCloseTunnel(10001));

        Assert.Null(exception);
        Assert.False(_manager.HasTunnel(10001));
    }

    [Fact]
    public void ForwardedPortFailed_SubscriberThrows_DoesNotEscape()
    {
        var calls = 0;
        _manager.ForwardedPortFailed += _ =>
        {
            calls++;
            throw new InvalidOperationException("Test subscriber failure.");
        };

        Exception? exception = Record.Exception(() => _manager.RaiseForwardedPortFailed(
            new TunnelForwardedPortFailure(
                LocalPort: 10001,
                RemoteHost: "target.internal",
                RemotePort: 3389,
                Message: "Synthetic forwarded port failure.",
                OccurredAtUtc: DateTimeOffset.UtcNow)));

        Assert.Null(exception);
        Assert.Equal(1, calls);
    }

    // ── Dispose ───────────────────────────────────────────────────────

    [Fact]
    public void Dispose_ClosesAllTunnels()
    {
        var handle = new FakeHandle();
        RegisterFake(10001, handle: handle);

        _manager.Dispose();

        Assert.True(handle.Disposed);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        RegisterFake(10001);

        _manager.Dispose();
        _manager.Dispose();
    }

    [Fact]
    public void Dispose_FiresTunnelClosedEvents()
    {
        var closedPorts = new List<int>();
        _manager.TunnelClosed += (port, _) => closedPorts.Add(port);

        RegisterFake(10001);
        RegisterFake(10002);

        _manager.Dispose();

        Assert.Contains(10001, closedPorts);
        Assert.Contains(10002, closedPorts);
    }

    // ── AllocatePort ──────────────────────────────────────────────────

    [Fact]
    public void AllocatePort_WithZero_ReturnsEphemeralPort()
    {
        int port = _manager.AllocatePort(0);

        Assert.True(port > 0);
    }

    [Fact]
    public void AllocatePort_TrackedPort_ReturnsAlternative()
    {
        RegisterFake(10001);

        int port = _manager.AllocatePort(10001);

        // Should return a different port since 10001 is tracked
        Assert.NotEqual(10001, port);
        Assert.True(port > 0);
    }

    [Fact]
    public void AllocatePort_PreferredHeldByExternalProcess_FallsBackToEphemeral()
    {
        // Hold a loopback port so the bind probe inside AllocatePort fails
        // with AddressAlreadyInUse, exercising the SocketException fallback path.
        using var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        blocker.Start();
        var occupiedPort = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;

        try
        {
            int port = _manager.AllocatePort(occupiedPort);

            Assert.NotEqual(occupiedPort, port);
            Assert.True(port > 0);
        }
        finally
        {
            blocker.Stop();
        }
    }

    // ── Loopback alias reservations ─────────────────────────────────

    [Fact]
    public void AllocateLoopbackAlias_StartsAtFirstAliasAndSkipsReservations()
    {
        string first = _manager.AllocateLoopbackAlias();
        string second = _manager.AllocateLoopbackAlias();

        Assert.Equal(LoopbackBinding.FormatAlias(LoopbackBinding.FirstAliasOctet), first);
        Assert.Equal(LoopbackBinding.FormatAlias(LoopbackBinding.FirstAliasOctet + 1), second);
    }

    [Fact]
    public void ReleaseLoopbackAliasReservation_AllowsAliasReuse()
    {
        string alias = _manager.AllocateLoopbackAlias();

        _manager.ReleaseLoopbackAliasReservation(alias);

        Assert.Equal(alias, _manager.AllocateLoopbackAlias());
    }

    [Fact]
    public void AllocateLoopbackAlias_SkipsAliasBoundByActiveExternalTunnel()
    {
        string alias = _manager.AllocateLoopbackAlias();
        Assert.True(_manager.TryRegisterExternalTunnel(
            MakeInfo(10001, localBindHost: alias),
            new FakeHandle(),
            () => true));

        string nextAlias = _manager.AllocateLoopbackAlias();

        Assert.NotEqual(alias, nextAlias);
    }

    [Fact]
    public void ReleaseReference_LastRefReleasesLoopbackAliasReservation()
    {
        string alias = _manager.AllocateLoopbackAlias();
        Assert.True(_manager.TryRegisterExternalTunnel(
            MakeInfo(10001, localBindHost: alias),
            new FakeHandle(),
            () => true));

        Assert.True(_manager.ReleaseReference(10001));

        Assert.Equal(alias, _manager.AllocateLoopbackAlias());
    }

    [Fact]
    public void ForceCloseTunnel_ReleasesLoopbackAliasReservation()
    {
        string alias = _manager.AllocateLoopbackAlias();
        Assert.True(_manager.TryRegisterExternalTunnel(
            MakeInfo(10001, localBindHost: alias),
            new FakeHandle(),
            () => true));

        _manager.ForceCloseTunnel(10001);

        Assert.Equal(alias, _manager.AllocateLoopbackAlias());
    }

    [Fact]
    public void TryRegisterExternalTunnel_NormalizesLocalBindHost()
    {
        Assert.True(_manager.TryRegisterExternalTunnel(
            MakeInfo(10001, localBindHost: "127.000.000.002"),
            new FakeHandle(),
            () => true));

        TunnelInfo? info = _manager.GetTunnel(10001);

        Assert.NotNull(info);
        Assert.Equal(LoopbackBinding.FormatAlias(2), info.LocalBindHost);
    }

    // ── Multiple tunnels on different ports ────────────────────────────

    [Fact]
    public void MultipleTunnels_TrackedIndependently()
    {
        RegisterFake(10001);
        RegisterFake(10002);

        _manager.ForceCloseTunnel(10001);

        Assert.False(_manager.HasTunnel(10001));
        Assert.True(_manager.HasTunnel(10002));
    }

    [Fact]
    public void RefCounting_IndependentPerPort()
    {
        RegisterFake(10001);
        RegisterFake(10002);

        _manager.AddReference(10001);

        // Release 10002 (1 ref) -> should close
        Assert.True(_manager.ReleaseReference(10002));
        Assert.False(_manager.HasTunnel(10002));

        // 10001 still has 2 refs
        Assert.True(_manager.HasTunnel(10001));
    }

    // ── OpenTunnelAsync characterization ─────────────────────────────

    [Fact]
    public async Task OpenTunnelAsync_PortAlreadyTracked_ReturnsPortInUseWithoutEvents()
    {
        var openedCount = 0;
        var closedCount = 0;
        _manager.TunnelOpened += _ => openedCount++;
        _manager.TunnelClosed += (_, _) => closedCount++;
        RegisterFake(10001);
        openedCount = 0;

        var result = await _manager.OpenTunnelAsync(
            MakeSshParams(),
            "target.internal",
            3389,
            10001,
            TestHostKeyStore(),
            TestHostKeyVerifier());

        Assert.False(result.Success);
        Assert.Null(result.Tunnel);
        Assert.Equal(SshFailureCode.PortInUse, result.FailureCode);
        Assert.Contains("already in use", result.ErrorMessage);
        Assert.Equal(0, openedCount);
        Assert.Equal(0, closedCount);
    }

    [Fact]
    public async Task OpenTunnelAsync_CancelledBeforeConnect_ReturnsCancelledWithoutEvents()
    {
        var openedCount = 0;
        var closedCount = 0;
        _manager.TunnelOpened += _ => openedCount++;
        _manager.TunnelClosed += (_, _) => closedCount++;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _manager.OpenTunnelAsync(
            MakeSshParams(),
            "target.internal",
            3389,
            10001,
            TestHostKeyStore(),
            TestHostKeyVerifier(),
            cts.Token);

        Assert.False(result.Success);
        Assert.Null(result.Tunnel);
        Assert.Equal(SshFailureCode.Cancelled, result.FailureCode);
        Assert.Equal("Tunnel establishment was cancelled.", result.ErrorMessage);
        Assert.Equal(0, openedCount);
        Assert.Equal(0, closedCount);
        Assert.False(_manager.HasTunnel(10001));
    }

    // Removed: host-key store without verifier is enforced at compile time after C1 hardening.

    // ── OpenChainedTunnelAsync characterization ──────────────────────

    [Fact]
    public async Task OpenChainedTunnelAsync_EmptyChain_ReturnsUnknown()
    {
        var result = await _manager.OpenChainedTunnelAsync(
            [],
            "target.internal",
            3389,
            10001,
            TestHostKeyStore(),
            TestHostKeyVerifier());

        Assert.False(result.Success);
        Assert.Null(result.Tunnel);
        Assert.Equal(SshFailureCode.Unknown, result.FailureCode);
        Assert.Equal("Gateway chain must contain at least one gateway.", result.ErrorMessage);
    }

    [Fact]
    public async Task OpenChainedTunnelAsync_PortAlreadyTracked_ReturnsPortInUseWithoutEvents()
    {
        var openedCount = 0;
        var closedCount = 0;
        _manager.TunnelOpened += _ => openedCount++;
        _manager.TunnelClosed += (_, _) => closedCount++;
        RegisterFake(10001);
        openedCount = 0;

        var result = await _manager.OpenChainedTunnelAsync(
            [MakeSshParams("gateway1.example.com"), MakeSshParams("gateway2.example.com")],
            "target.internal",
            3389,
            10001,
            TestHostKeyStore(),
            TestHostKeyVerifier());

        Assert.False(result.Success);
        Assert.Null(result.Tunnel);
        Assert.Equal(SshFailureCode.PortInUse, result.FailureCode);
        Assert.Contains("already in use", result.ErrorMessage);
        Assert.Equal(0, openedCount);
        Assert.Equal(0, closedCount);
    }

    [Fact]
    public async Task OpenChainedTunnelAsync_CancelledBeforeRootConnect_ReturnsChainedCancelledWithoutEvents()
    {
        var openedCount = 0;
        var closedCount = 0;
        _manager.TunnelOpened += _ => openedCount++;
        _manager.TunnelClosed += (_, _) => closedCount++;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _manager.OpenChainedTunnelAsync(
            [MakeSshParams("gateway1.example.com"), MakeSshParams("gateway2.example.com")],
            "target.internal",
            3389,
            10001,
            TestHostKeyStore(),
            TestHostKeyVerifier(),
            cts.Token);

        Assert.False(result.Success);
        Assert.Null(result.Tunnel);
        Assert.Equal(SshFailureCode.Cancelled, result.FailureCode);
        Assert.Equal("Chained tunnel establishment was cancelled.", result.ErrorMessage);
        Assert.Equal(0, openedCount);
        Assert.Equal(0, closedCount);
        Assert.False(_manager.HasTunnel(10001));
    }

    [Fact]
    public async Task OpenChainedTunnelAsync_SingleGateway_DelegatesToSingleHopBehavior()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _manager.OpenChainedTunnelAsync(
            [MakeSshParams()],
            "target.internal",
            3389,
            10001,
            TestHostKeyStore(),
            TestHostKeyVerifier(),
            cts.Token);

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.Cancelled, result.FailureCode);
        Assert.Equal("Tunnel establishment was cancelled.", result.ErrorMessage);
    }

    [Fact]
    public void TryRegisterExternalTunnel_Duplicate_DoesNotFireOpenOrCloseEvents()
    {
        var openedCount = 0;
        var closedCount = 0;
        _manager.TunnelOpened += _ => openedCount++;
        _manager.TunnelClosed += (_, _) => closedCount++;
        RegisterFake(10001);
        openedCount = 0;

        var result = RegisterFake(10001);

        Assert.False(result);
        Assert.Equal(0, openedCount);
        Assert.Equal(0, closedCount);
    }

    // ── TunnelSession.Dispose ─────────────────────────────────────────

    [Fact]
    public void TunnelSession_Dispose_WhenIntermediateCleanupThrows_ContinuesWithRemainingResources()
    {
        RecordingForwardedPortLocal remainingIntermediatePort = new();
        ThrowingStopForwardedPortLocal throwingIntermediatePort = new();
        RecordingSshClient remainingIntermediateClient = new();
        ThrowingDisposeSshClient throwingIntermediateClient = new();
        RecordingSshClient finalClient = new();
        RecordingForwardedPortLocal finalPort = new();
        TunnelSession session = new(
            finalClient,
            finalPort,
            MakeInfo(10001),
            [remainingIntermediateClient, throwingIntermediateClient],
            [remainingIntermediatePort, throwingIntermediatePort]);

        Exception? exception = Record.Exception(session.Dispose);

        Assert.Null(exception);
        Assert.Equal(1, throwingIntermediatePort.StopCount);
        Assert.Equal(1, finalPort.DisposeCount);
        Assert.Equal(1, remainingIntermediatePort.DisposeCount);
        Assert.Equal(1, finalClient.DisposeCount);
        Assert.Equal(1, throwingIntermediateClient.DisposeCount);
        Assert.Equal(1, remainingIntermediateClient.DisposeCount);
    }

    // ── TunnelBuildContext.Cleanup ───────────────────────────────────

    [Fact]
    public void TunnelBuildContext_Cleanup_DisposesEveryIntermediateAndFinalResource()
    {
        TunnelManager.TunnelBuildContext context = new TunnelManager.TunnelBuildContext();
        SshClient firstIntermediateClient = new SshClient("127.0.0.1", "u", "p");
        SshClient secondIntermediateClient = new SshClient("127.0.0.1", "u", "p");
        SshClient finalClient = new SshClient("127.0.0.1", "u", "p");
        ForwardedPortLocal firstIntermediatePort = new ForwardedPortLocal("127.0.0.1", 0u, "remote.invalid", 22u);
        ForwardedPortLocal secondIntermediatePort = new ForwardedPortLocal("127.0.0.1", 0u, "remote.invalid", 22u);
        ForwardedPortLocal finalPort = new ForwardedPortLocal("127.0.0.1", 0u, "remote.invalid", 22u);

        context.IntermediateClients.Add(firstIntermediateClient);
        context.IntermediateClients.Add(secondIntermediateClient);
        context.IntermediatePorts.Add(firstIntermediatePort);
        context.IntermediatePorts.Add(secondIntermediatePort);
        context.FinalClient = finalClient;
        context.FinalPort = finalPort;

        Exception? cleanupException = Record.Exception(() => context.Cleanup());

        Assert.Null(cleanupException);
        Assert.Throws<ObjectDisposedException>(() => firstIntermediateClient.Connect());
        Assert.Throws<ObjectDisposedException>(() => secondIntermediateClient.Connect());
        Assert.Throws<ObjectDisposedException>(() => finalClient.Connect());
        Assert.Throws<ObjectDisposedException>(() => firstIntermediatePort.Start());
        Assert.Throws<ObjectDisposedException>(() => secondIntermediatePort.Start());
        Assert.Throws<ObjectDisposedException>(() => finalPort.Start());
    }

    [Fact]
    public void TunnelBuildContext_Cleanup_OnEmptyContext_DoesNotThrow()
    {
        TunnelManager.TunnelBuildContext context = new TunnelManager.TunnelBuildContext();

        Exception? cleanupException = Record.Exception(() => context.Cleanup());

        Assert.Null(cleanupException);
    }
}
