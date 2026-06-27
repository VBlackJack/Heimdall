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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Pure decision for system-wide idle auto-lock. A threshold of zero disables
/// auto-lock; otherwise the workspace locks once the measured idle duration
/// reaches the threshold.
/// </summary>
public static class VaultIdlePolicy
{
    private const long MillisecondsPerMinute = 60_000;

    /// <summary>
    /// Whether the workspace should auto-lock for the given idle duration.
    /// </summary>
    /// <param name="idleMilliseconds">System-wide idle duration (from GetLastInputInfo).</param>
    /// <param name="thresholdMinutes">The configured auto-lock threshold; 0 disables it.</param>
    /// <returns><c>true</c> when auto-lock should fire.</returns>
    public static bool ShouldAutoLock(long idleMilliseconds, int thresholdMinutes)
    {
        if (thresholdMinutes <= 0)
        {
            return false;
        }

        return idleMilliseconds >= thresholdMinutes * MillisecondsPerMinute;
    }
}

/// <summary>
/// Pure decision for whether a reconnect attempt must be deferred because the
/// workspace is locked (the stored credential cannot be decrypted while locked).
/// </summary>
public static class VaultReconnectPolicy
{
    /// <summary>
    /// Whether a reconnect must be deferred (queued for resume after unlock)
    /// rather than attempted now.
    /// </summary>
    /// <param name="isWorkspaceLocked">Whether the vault workspace is currently locked.</param>
    /// <returns><c>true</c> to defer the reconnect.</returns>
    public static bool ShouldDeferReconnect(bool isWorkspaceLocked) => isWorkspaceLocked;
}
