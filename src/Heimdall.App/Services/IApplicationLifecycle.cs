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
/// Seam over the application shutdown so view-models stay testable without a live WPF host.
/// </summary>
public interface IApplicationLifecycle
{
    /// <summary>
    /// Persists what the ordinary close gesture persists - unsaved settings, the tree's
    /// expand state, the window bounds - without any prompt. For a shutdown the user
    /// already asked for, such as an update install, where the close pass that would
    /// have saved them is skipped.
    /// </summary>
    Task PersistStateAsync();

    /// <summary>Requests an orderly shutdown of the running application.</summary>
    void RequestShutdown();
}
