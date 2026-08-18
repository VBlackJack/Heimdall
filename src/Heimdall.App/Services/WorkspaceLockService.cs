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

using System.Runtime.Versioning;
using System.Windows.Threading;
using Heimdall.Core.Logging;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Services;

/// <summary>
/// Owns the "workspace locked" state on top of <see cref="VaultLifecycleService"/>.
/// Locking zeroes the in-memory DEK (via the lifecycle) and raises
/// <see cref="LockStateChanged"/> so the UI can mask the session host and show the
/// re-unlock overlay. Only active when a master-password vault is enabled.
/// </summary>
/// <remarks>
/// Idle auto-lock is measured system-wide via <see cref="SystemIdle"/>
/// (GetLastInputInfo), focus-independent so it does not false-fire while a
/// terminal/RDP/VNC surface owns focus. The D3 live-session policy is
/// survive-and-mask by default; <c>DisconnectOnLock</c> opts into tearing sessions
/// down on lock through the supplied disconnector.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WorkspaceLockService : IDisposable
{
    private const int IdlePollSeconds = 5;

    private readonly VaultLifecycleService _lifecycle;
    private readonly DispatcherTimer _idleTimer;

    private bool _vaultEnabled;
    private int _autoLockIdleMinutes;
    private bool _disconnectOnLock;
    private Action? _disconnectAllSessions;

    /// <summary>Create the lock service over the vault lifecycle.</summary>
    /// <param name="lifecycle">The vault lifecycle (Lock zeroes the DEK; UnlockAsync re-unwraps it).</param>
    public WorkspaceLockService(VaultLifecycleService lifecycle)
    {
        _lifecycle = lifecycle;
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(IdlePollSeconds) };
        _idleTimer.Tick += OnIdleTick;
    }

    /// <summary>Raised whenever the locked state changes (lock or successful unlock).</summary>
    public event Action? LockStateChanged;

    /// <summary>Whether a master-password vault is configured (lock is a no-op otherwise).</summary>
    public bool IsVaultEnabled => _vaultEnabled;

    /// <summary>Whether the workspace is currently locked (DEK zeroed, overlay shown).</summary>
    public bool IsWorkspaceLocked { get; private set; }

    /// <summary>Whether a manual lock is currently available (vault enabled and unlocked).</summary>
    public bool CanLock => _vaultEnabled && !IsWorkspaceLocked;

    /// <summary>Whether a reconnect must be deferred (workspace locked).</summary>
    public bool ShouldDeferReconnect => VaultReconnectPolicy.ShouldDeferReconnect(IsWorkspaceLocked);

    /// <summary>
    /// Apply the current settings: enable/disable lock, set the idle threshold and
    /// the disconnect-on-lock policy. Call at startup (after the unlock gate) and on
    /// every settings change. Restarts the idle watcher accordingly.
    /// </summary>
    /// <param name="vaultEnabled">Whether a vault is configured.</param>
    /// <param name="autoLockIdleMinutes">Idle auto-lock threshold in minutes (0 disables).</param>
    /// <param name="disconnectOnLock">Whether to disconnect live sessions on lock.</param>
    public void Configure(bool vaultEnabled, int autoLockIdleMinutes, bool disconnectOnLock)
    {
        _vaultEnabled = vaultEnabled;
        _autoLockIdleMinutes = autoLockIdleMinutes;
        _disconnectOnLock = disconnectOnLock;
        RefreshIdleTimer();
    }

    /// <summary>
    /// Supply the session-teardown action used when <c>DisconnectOnLock</c> is set.
    /// </summary>
    /// <param name="disconnectAllSessions">Tears down every active session.</param>
    public void SetSessionDisconnector(Action disconnectAllSessions)
    {
        _disconnectAllSessions = disconnectAllSessions;
    }

    /// <summary>
    /// Lock the workspace: zero the DEK and (optionally) disconnect sessions.
    /// No-op when no vault is enabled or already locked.
    /// </summary>
    public void Lock()
    {
        if (!_vaultEnabled || IsWorkspaceLocked)
        {
            return;
        }

        _idleTimer.Stop();
        _lifecycle.Lock();
        IsWorkspaceLocked = true;

        if (_disconnectOnLock)
        {
            _disconnectAllSessions?.Invoke();
        }

        FileLogger.Info("Workspace locked.");
        LockStateChanged?.Invoke();
    }

    /// <summary>
    /// Re-unlock the workspace with the master password (re-unwraps the DEK from the
    /// persisted wrapped blob). Propagates the single generic
    /// <see cref="VaultUnlockException"/> on a wrong password; the caller surfaces it.
    /// </summary>
    /// <param name="masterPassword">The master password (caller zeroes after).</param>
    public async Task UnlockAsync(char[] masterPassword)
    {
        await _lifecycle.UnlockAsync(masterPassword).ConfigureAwait(true);
        IsWorkspaceLocked = false;
        RefreshIdleTimer();
        FileLogger.Info("Workspace unlocked.");
        LockStateChanged?.Invoke();
    }

    /// <summary>
    /// Re-unlock the workspace with Windows Hello. Failure leaves the workspace
    /// locked and returns a coarse reason so the UI can fall back to the master
    /// password path.
    /// </summary>
    public async Task<VaultHelloUnlockResult> UnlockWithHelloAsync(CancellationToken ct = default)
    {
        var result = await _lifecycle.UnlockWithHelloDetailedAsync(ct).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            return result;
        }

        IsWorkspaceLocked = false;
        RefreshIdleTimer();
        FileLogger.Info("Workspace unlocked with Windows Hello.");
        LockStateChanged?.Invoke();
        return result;
    }

    /// <summary>
    /// Enrols Windows Hello for the vault this service governs.
    /// </summary>
    /// <remarks>
    /// Enrolment does not change the locked state: a locked workspace stays locked, and the unlock
    /// methods above remain the only things that release it.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    public Task EnrollHelloAsync(CancellationToken ct = default) => _lifecycle.EnrollHelloAsync(ct);

    private void RefreshIdleTimer()
    {
        var shouldWatch = _vaultEnabled && !IsWorkspaceLocked && _autoLockIdleMinutes > 0;
        if (shouldWatch)
        {
            _idleTimer.Start();
        }
        else
        {
            _idleTimer.Stop();
        }
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (!_vaultEnabled || IsWorkspaceLocked)
        {
            _idleTimer.Stop();
            return;
        }

        var idle = SystemIdle.GetIdleDuration();
        if (VaultIdlePolicy.ShouldAutoLock((long)idle.TotalMilliseconds, _autoLockIdleMinutes))
        {
            Lock();
        }
    }

    public void Dispose()
    {
        _idleTimer.Stop();
        _idleTimer.Tick -= OnIdleTick;
    }
}
