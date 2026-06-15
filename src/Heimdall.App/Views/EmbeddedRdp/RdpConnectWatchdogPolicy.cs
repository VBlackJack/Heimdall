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

namespace Heimdall.App.Views.EmbeddedRdp;

internal static class RdpConnectWatchdogPolicy
{
    public const int DisabledTimeoutMs = 0;
    public const int DefaultTimeoutMs = 45_000;
    public const int MinTimeoutMs = 5_000;
    public const int MaxTimeoutMs = 600_000;

    /// <summary>
    /// Extra time added on top of the credential pipeline budget so the connect
    /// watchdog strictly outlives the autofill's own timeout. This guarantees the
    /// autofill graceful TimedOut/Failed retry path runs instead of a hard
    /// watchdog teardown.
    /// </summary>
    public const int CredentialWaitGraceMs = 15_000;

    public static bool ShouldArm(RdpConnectionPhase phase)
        => phase is RdpConnectionPhase.Preparing
            or RdpConnectionPhase.Connecting
            or RdpConnectionPhase.Loading;

    public static bool ShouldCancel(RdpConnectionPhase phase)
        => phase is RdpConnectionPhase.None
            or RdpConnectionPhase.Connected;

    public static int ResolveTimeoutMs(int configured)
        => configured <= DisabledTimeoutMs
            ? DisabledTimeoutMs
            : Math.Clamp(configured, MinTimeoutMs, MaxTimeoutMs);

    /// <summary>
    /// Resolves the Stage 2 watchdog budget applied once the credential-autofill
    /// watcher proves the RDP stack is reachable and is blocked waiting on the
    /// remote NLA credential prompt. A disabled watchdog stays disabled. Otherwise
    /// the budget is the larger of the configured watchdog and the autofill timeout
    /// plus <see cref="CredentialWaitGraceMs"/>, clamped to
    /// [<see cref="MinTimeoutMs"/>, <see cref="MaxTimeoutMs"/>]. Total and
    /// overflow-safe over the full <see cref="int"/> range.
    /// </summary>
    public static int ResolveStageTwoTimeoutMs(int configuredWatchdogMs, int autofillTimeoutMs)
    {
        if (configuredWatchdogMs <= DisabledTimeoutMs)
        {
            return DisabledTimeoutMs;
        }

        int baseTimeoutMs = Math.Max(configuredWatchdogMs, autofillTimeoutMs);

        // Promote to long before adding the grace window to guard against int overflow.
        long extendedTimeoutMs = (long)baseTimeoutMs + CredentialWaitGraceMs;

        return (int)Math.Clamp(extendedTimeoutMs, MinTimeoutMs, MaxTimeoutMs);
    }
}
