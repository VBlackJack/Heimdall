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

/// <summary>
/// A step resolver fixed to the operating system one profile declares, so the sequence runner
/// can stay unaware of profiles and still expand each linked step for the right platform.
/// </summary>
internal sealed class RemoteOsBoundStepResolver(IPostConnectStepResolver inner, RemoteOs remoteOs)
    : IPostConnectStepResolver
{
    private readonly IPostConnectStepResolver _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<PostConnectResolveResult> ResolveAsync(PostConnectStep step, CancellationToken ct)
        => _inner.ResolveAsync(step, remoteOs, ct);
}
