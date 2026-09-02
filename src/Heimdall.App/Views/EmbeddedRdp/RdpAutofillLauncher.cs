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

/// <summary>
/// Starts the credential-dialog watcher away from the thread that asked for it.
/// </summary>
/// <remarks>
/// The watcher's first scan enumerates every visible top-level window and resolves a process name
/// for each one, then walks the process's own threads. Nothing is awaited before that, so starting
/// the watcher with a bare call runs scan one inline - on the UI thread, inside the render-priority
/// operation that has just called Connect(), with the control's own callbacks queued behind it.
/// </remarks>
internal static class RdpAutofillLauncher
{
    internal static Task StartAsync(Func<CancellationToken, Task> watcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watcher);

        return Task.Run(() => watcher(cancellationToken), cancellationToken);
    }
}
