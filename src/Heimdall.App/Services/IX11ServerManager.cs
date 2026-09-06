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

namespace Heimdall.App.Services;

/// <summary>
/// The part of <see cref="X11ServerManager"/> a connection handler needs: whether an X11
/// display server can be made available before a forwarding session is launched.
/// </summary>
/// <remarks>
/// Split out so the handler's answer to "no server" can be tested without scanning the test
/// box for VcXsrv, or starting one.
/// </remarks>
public interface IX11ServerManager
{
    /// <summary>
    /// Ensures an X11 server is available, starting the configured one when allowed. Returns
    /// <see langword="true"/> when a server is running, <see langword="false"/> when none could
    /// be found or started.
    /// </summary>
    Task<bool> EnsureRunningAsync();
}
