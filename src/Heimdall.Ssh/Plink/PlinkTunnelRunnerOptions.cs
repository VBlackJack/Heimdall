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

namespace Heimdall.Ssh.Plink;

/// <summary>
/// Tunable parameters for <see cref="PlinkTunnelRunner"/>. Surface kept narrow
/// - only the values that legitimately differ between environments (slow VPN,
/// CI, debugging) are exposed. Defaults match the historical hard-coded
/// values and are wire-compatible with existing call sites.
/// </summary>
/// <param name="PortCheckIntervalMs">
/// Delay between TCP probes to the local forwarded port while waiting for the
/// tunnel to become reachable. Shorter values shorten startup time on fast
/// links; longer values reduce log noise on slow ones.
/// </param>
/// <param name="KillGracePeriodMs">
/// Maximum time <see cref="PlinkTunnelRunner.Stop"/> waits for plink.exe to
/// exit cleanly after <c>Process.Kill</c> before giving up.
/// </param>
public sealed record PlinkTunnelRunnerOptions(
    int PortCheckIntervalMs = 2000,
    int KillGracePeriodMs = 2000);
