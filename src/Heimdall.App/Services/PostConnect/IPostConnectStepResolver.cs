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
using Heimdall.Core.Models;

namespace Heimdall.App.Services.PostConnect;

public interface IPostConnectStepResolver
{
    Task<PostConnectResolveResult> ResolveAsync(PostConnectStep step, CancellationToken ct);

    /// <summary>
    /// Resolves a step for a far end that runs <paramref name="remoteOs"/>, so a linked action is
    /// expanded from the template of the platform the profile declares.
    /// </summary>
    /// <remarks>
    /// Defaults to the platform-blind overload, so an implementation with nothing to choose
    /// between needs no change.
    /// </remarks>
    Task<PostConnectResolveResult> ResolveAsync(PostConnectStep step, RemoteOs remoteOs, CancellationToken ct)
        => ResolveAsync(step, ct);
}
