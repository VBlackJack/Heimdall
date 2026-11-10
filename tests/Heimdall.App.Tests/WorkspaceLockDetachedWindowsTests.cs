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
using Heimdall.App.ViewModels;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

/// <summary>
/// Locking the workspace covers the main window only, so the floating session windows must be
/// hidden with it, and disconnect-on-lock must reach the sessions they host.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class WorkspaceLockDetachedWindowsTests
{
    [Fact]
    public async Task LockWorkspace_HidesTheDetachedWindows_AndUnlockShowsThemAgain()
    {
        RecordingSessionWindowService windows = new();
        WorkspaceLockService workspaceLock = await CreateEnabledWorkspaceLockAsync(disconnectOnLock: false);
        try
        {
            using SessionCoordinatorPreMountTests.TestHarness harness = SessionCoordinatorPreMountTests.TestHarness.Create(sessionWindows: windows, workspaceLock: workspaceLock);

            workspaceLock.Lock();

            Assert.Equal(1, windows.HideCalls);
            Assert.Equal(0, windows.ShowCalls);

            await workspaceLock.UnlockAsync(LockTestPassword.ToCharArray());

            Assert.Equal(1, windows.HideCalls);
            Assert.Equal(1, windows.ShowCalls);
        }
        finally
        {
            CredentialProtectorStateScope.Reset();
        }
    }

    [Fact]
    public async Task LockWorkspace_WithDisconnectOnLock_AlsoDisconnectsTheDetachedSessions()
    {
        RecordingSessionWindowService windows = new();
        WorkspaceLockService workspaceLock = await CreateEnabledWorkspaceLockAsync(disconnectOnLock: true);
        try
        {
            using SessionCoordinatorPreMountTests.TestHarness harness = SessionCoordinatorPreMountTests.TestHarness.Create(sessionWindows: windows, workspaceLock: workspaceLock);
            SessionTabViewModel docked = harness.Main.Connection.AddSession("docked-runtime", "Docked", "SSH");
            SessionTabViewModel detached = harness.Main.Connection.AddSession("detached-runtime", "Detached", "SSH");
            harness.Main.Connection.RemoveFromStrip(detached);
            windows.Detached.Add(detached);

            workspaceLock.Lock();

            await WaitUntilAsync(() => docked.IsClosed && detached.IsClosed);
            Assert.True(docked.IsClosed);
            Assert.True(detached.IsClosed);
        }
        finally
        {
            CredentialProtectorStateScope.Reset();
        }
    }

    [Fact]
    public async Task LockWorkspace_WithoutDisconnectOnLock_KeepsTheDetachedSessionsOpen()
    {
        RecordingSessionWindowService windows = new();
        WorkspaceLockService workspaceLock = await CreateEnabledWorkspaceLockAsync(disconnectOnLock: false);
        try
        {
            using SessionCoordinatorPreMountTests.TestHarness harness = SessionCoordinatorPreMountTests.TestHarness.Create(sessionWindows: windows, workspaceLock: workspaceLock);
            SessionTabViewModel detached = harness.Main.Connection.AddSession("detached-runtime", "Detached", "SSH");
            harness.Main.Connection.RemoveFromStrip(detached);
            windows.Detached.Add(detached);

            workspaceLock.Lock();

            Assert.False(detached.IsClosed);
        }
        finally
        {
            CredentialProtectorStateScope.Reset();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + SessionCoordinatorPreMountTests.TestTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Condition was not met before timeout.");
    }

    private const string LockTestPassword = "LockTest2026!Pass";

    private static readonly Argon2idParameters FastLockParameters = new(MemoryKib: 256, Iterations: 1, Parallelism: 1);

    private static async Task<WorkspaceLockService> CreateEnabledWorkspaceLockAsync(bool disconnectOnLock)
    {
        string root = Path.Combine(Path.GetTempPath(), "heimdall-lock-detached-tests", Guid.NewGuid().ToString("N"));
        Heimdall.Core.Configuration.ConfigManager configManager = new(root);
        await configManager.InitializeAsync();
        CredentialProtectorStateScope.Reset(HmacIntegrity.GenerateRawKey());
        VaultLifecycleService lifecycle = new(configManager);
        await lifecycle.EnableAsync(LockTestPassword.ToCharArray(), FastLockParameters);
        WorkspaceLockService workspaceLock = new(lifecycle);
        workspaceLock.Configure(vaultEnabled: true, autoLockIdleMinutes: 0, disconnectOnLock: disconnectOnLock);
        return workspaceLock;
    }

    private sealed class RecordingSessionWindowService : ISessionWindowService
    {
        public List<SessionTabViewModel> Detached { get; } = [];

        public int HideCalls { get; private set; }

        public int ShowCalls { get; private set; }

        public event EventHandler? SplitPaletteRequested
        {
            add { }
            remove { }
        }

        public IReadOnlyList<SessionTabViewModel> DetachedSessions => Detached;

        public bool BringDetachedSessionToFront(SessionTabViewModel session) => false;

        public void HideDetachedWindows() => HideCalls++;

        public void ShowDetachedWindows() => ShowCalls++;

        public void DetachSessionToFloatingWindow(SessionTabViewModel session, MainViewModel vm)
        {
        }

        public void DetachSecondaryToFloatingWindow(SessionTabViewModel session, MainViewModel vm)
        {
        }

        public void RequestSplitSession(SessionTabViewModel session, SplitOrientation orientation, MainViewModel vm)
        {
        }

        public void UnsplitSession(SessionTabViewModel session, MainViewModel vm)
        {
        }

        public void HandleEmbeddedSplitRequest(SessionTabViewModel session, MainViewModel vm)
        {
        }
    }
}
