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
/// Single-target probe abstraction. The production implementation
/// (<see cref="TcpHealthProbe"/>) opens a TCP socket with a bounded timeout;
/// unit tests substitute a stub to verify scheduler / state-machine logic
/// without hitting the network.
/// </summary>
public interface IHealthProbe
{
    /// <summary>
    /// Attempts a single probe against <paramref name="host"/>:<paramref name="port"/>
    /// with a hard deadline of <paramref name="timeoutMs"/>. Implementations must
    /// never throw - failures are encoded in the returned <see cref="HealthState"/>.
    /// </summary>
    Task<HealthState> ProbeAsync(string host, int port, int timeoutMs, CancellationToken ct);
}
