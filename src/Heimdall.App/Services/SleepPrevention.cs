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

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Heimdall.App.Services;

/// <summary>
/// Prevents Windows from entering sleep/standby while embedded sessions are active.
/// Uses <c>SetThreadExecutionState</c> with a reference-counted session model and
/// a 60-second heartbeat to reset the OS idle timer - required for VMs and RDP hosts
/// where a single <c>ES_CONTINUOUS</c> flag is insufficient.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SleepPrevention
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    private static int _activeSessionCount;
    private static System.Threading.Timer? _keepAliveTimer;
    private static bool _enabled = true;
    private static int _intervalSeconds = 60;

    /// <summary>
    /// Heartbeat interval in seconds. Set before the first session starts.
    /// </summary>
    public static int IntervalSeconds
    {
        get => _intervalSeconds;
        set => _intervalSeconds = value > 0 ? value : 60;
    }

    /// <summary>
    /// Controls whether sleep prevention is active. When false,
    /// SessionStarted/SessionEnded calls are no-ops.
    /// Reflects the <c>PreventSleepDuringSession</c> setting.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value && _activeSessionCount > 0)
            {
                // Setting turned off while sessions are active - release immediately
                Interlocked.Exchange(ref _activeSessionCount, 0);
                _keepAliveTimer?.Dispose();
                _keepAliveTimer = null;
                SetThreadExecutionState(ES_CONTINUOUS);
                Core.Logging.FileLogger.Info("Sleep prevention disabled by user setting");
            }
        }
    }

    /// <summary>
    /// Signals that an embedded session has started. When the first session is
    /// registered, sleep prevention is enabled with a periodic heartbeat.
    /// </summary>
    public static void SessionStarted()
    {
        if (!_enabled) return;

        if (Interlocked.Increment(ref _activeSessionCount) == 1)
        {
            // Set the continuous flag on the UI thread
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

            // Start a heartbeat that resets the OS idle timer every 60 seconds.
            // Without ES_CONTINUOUS, the call acts as a one-shot "mouse move" equivalent,
            // which overrides VM/group-policy idle timeouts that ignore ES_CONTINUOUS.
            var interval = TimeSpan.FromSeconds(_intervalSeconds);
            _keepAliveTimer = new System.Threading.Timer(
                _ => SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED),
                null,
                interval,
                interval);

            Core.Logging.FileLogger.Info("Sleep prevention enabled (sessions active, heartbeat started)");
        }
    }

    /// <summary>
    /// Signals that an embedded session has ended. When the last session is
    /// unregistered, sleep prevention and heartbeat are cleared.
    /// </summary>
    public static void SessionEnded()
    {
        if (Interlocked.Decrement(ref _activeSessionCount) <= 0)
        {
            Interlocked.Exchange(ref _activeSessionCount, 0);

            _keepAliveTimer?.Dispose();
            _keepAliveTimer = null;

            SetThreadExecutionState(ES_CONTINUOUS);
            Core.Logging.FileLogger.Info("Sleep prevention cleared (no sessions)");
        }
    }

    /// <summary>
    /// Unconditionally clears sleep prevention regardless of session count.
    /// Called during application shutdown.
    /// </summary>
    public static void ForceRelease()
    {
        Interlocked.Exchange(ref _activeSessionCount, 0);

        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;

        SetThreadExecutionState(ES_CONTINUOUS);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
