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
/// a periodic heartbeat to reset the OS idle timer - required for VMs and RDP hosts
/// where a single <c>ES_CONTINUOUS</c> flag is insufficient.
/// </summary>
/// <remarks>
/// <para>Sessions are counted whether or not the setting is on, so turning it back on while
/// sessions are open resumes the prevention instead of waiting for the next session.</para>
/// <para>Windows ties an <c>ES_CONTINUOUS</c> request to the thread that made it, and only that
/// thread can clear it. The setting is changed from whichever thread saved the settings, so every
/// continuous request goes through <see cref="ContinuousStateThread"/>, which the application
/// points at the UI thread.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class SleepPrevention
{
    internal const uint ES_CONTINUOUS = 0x80000000;
    internal const uint ES_SYSTEM_REQUIRED = 0x00000001;
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;

    private const int DefaultIntervalSeconds = 60;

    private static readonly object Gate = new();
    private static int _activeSessionCount;
    private static System.Threading.Timer? _keepAliveTimer;
    private static bool _enabled = true;
    private static bool _holding;
    private static int _intervalSeconds = DefaultIntervalSeconds;

    /// <summary>
    /// Runs a continuous execution-state request on the thread that owns it. Runs inline until
    /// the application sets it; it must not wait for that thread, since it is called under a lock.
    /// </summary>
    public static Action<Action> ContinuousStateThread { get; set; } = action => action();

    /// <summary>Where execution-state requests go; replaced by tests.</summary>
    internal static Action<uint> ExecutionStateSink { get; set; } = flags => SetThreadExecutionState(flags);

    /// <summary>The sessions currently registered, whether or not the prevention is on.</summary>
    public static int ActiveSessionCount
    {
        get
        {
            lock (Gate)
            {
                return _activeSessionCount;
            }
        }
    }

    /// <summary>Whether the system is currently kept awake.</summary>
    public static bool IsHolding
    {
        get
        {
            lock (Gate)
            {
                return _holding;
            }
        }
    }

    /// <summary>
    /// Heartbeat interval in seconds. A change restarts a running heartbeat.
    /// </summary>
    public static int IntervalSeconds
    {
        get
        {
            lock (Gate)
            {
                return _intervalSeconds;
            }
        }
        set
        {
            lock (Gate)
            {
                int interval = value > 0 ? value : DefaultIntervalSeconds;
                if (interval == _intervalSeconds)
                {
                    return;
                }

                _intervalSeconds = interval;
                if (_holding)
                {
                    StartHeartbeat();
                }
            }
        }
    }

    /// <summary>
    /// Controls whether sleep prevention is active. Reflects the
    /// <c>PreventSleepDuringSession</c> setting.
    /// </summary>
    public static bool Enabled
    {
        get
        {
            lock (Gate)
            {
                return _enabled;
            }
        }
        set
        {
            lock (Gate)
            {
                _enabled = value;
                Apply();
            }
        }
    }

    /// <summary>
    /// Signals that an embedded session has started.
    /// </summary>
    public static void SessionStarted()
    {
        lock (Gate)
        {
            _activeSessionCount++;
            Apply();
        }
    }

    /// <summary>
    /// Signals that an embedded session has ended. When the last session is
    /// unregistered, sleep prevention and heartbeat are cleared.
    /// </summary>
    public static void SessionEnded()
    {
        lock (Gate)
        {
            if (_activeSessionCount > 0)
            {
                _activeSessionCount--;
            }

            Apply();
        }
    }

    /// <summary>
    /// Unconditionally clears sleep prevention regardless of session count.
    /// Called during application shutdown.
    /// </summary>
    public static void ForceRelease()
    {
        lock (Gate)
        {
            _activeSessionCount = 0;
            Apply();
        }
    }

    /// <summary>Restores the initial state; for tests.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            StopHeartbeat();
            _activeSessionCount = 0;
            _enabled = true;
            _holding = false;
            _intervalSeconds = DefaultIntervalSeconds;
        }
    }

    private static void Apply()
    {
        bool shouldHold = _enabled && _activeSessionCount > 0;
        if (shouldHold == _holding)
        {
            return;
        }

        _holding = shouldHold;
        if (shouldHold)
        {
            ContinuousStateThread(() => ExecutionStateSink(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED));
            StartHeartbeat();
            Core.Logging.FileLogger.Info("Sleep prevention enabled (sessions active, heartbeat started)");
        }
        else
        {
            StopHeartbeat();
            ContinuousStateThread(() => ExecutionStateSink(ES_CONTINUOUS));
            Core.Logging.FileLogger.Info("Sleep prevention cleared");
        }
    }

    private static void StartHeartbeat()
    {
        StopHeartbeat();

        // Without ES_CONTINUOUS, the call acts as a one-shot "mouse move" equivalent,
        // which overrides VM/group-policy idle timeouts that ignore ES_CONTINUOUS.
        TimeSpan interval = TimeSpan.FromSeconds(_intervalSeconds);
        _keepAliveTimer = new System.Threading.Timer(
            _ => ExecutionStateSink(ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED),
            null,
            interval,
            interval);
    }

    private static void StopHeartbeat()
    {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
