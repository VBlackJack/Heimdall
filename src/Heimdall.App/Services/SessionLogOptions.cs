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

using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>
/// Tunable options for <see cref="SessionLogService"/>. Kept explicit (rather than read from
/// global settings) so the service stays unit-testable with a temporary directory and a tiny cap.
/// </summary>
/// <param name="MaxFileBytes">
/// Maximum size in bytes of a single log file before it rolls over to a ".N.log" continuation.
/// Must be strictly positive.
/// </param>
/// <param name="FlushIntervalMs">
/// Interval in milliseconds at which buffered output is flushed to disk. Must be strictly positive.
/// </param>
public sealed record SessionLogOptions(long MaxFileBytes, int FlushIntervalMs)
{
    /// <summary>
    /// Default options derived from <see cref="AppConstants.DefaultSessionLogMaxBytes"/> and
    /// <see cref="AppConstants.SessionLogFlushIntervalMs"/>.
    /// </summary>
    public static SessionLogOptions CreateDefault() => new(
        AppConstants.DefaultSessionLogMaxBytes,
        AppConstants.SessionLogFlushIntervalMs);
}
