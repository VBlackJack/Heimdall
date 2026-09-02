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

namespace Heimdall.Core.SessionHealth;

/// <summary>
/// Reachability state surfaced by <see cref="SessionHealthMonitor"/> for every
/// server in the inventory. The sidebar badge color is driven by this value.
/// </summary>
public enum HealthStatus
{
    /// <summary>No verdict - server is behind a gateway, has no declared probe port, or has never been probed.</summary>
    Unknown,
    /// <summary>A probe is currently in flight for this server.</summary>
    Probing,
    /// <summary>Last probe succeeded within the configured timeout.</summary>
    Up,
    /// <summary>Last probe failed (timeout, refused, host unreachable, DNS error).</summary>
    Down
}
