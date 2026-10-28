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

using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Services;

/// <summary>
/// Creates the view model of a tool view without handing its lifetime to the container.
/// </summary>
/// <remarks>
/// <para>Microsoft.Extensions.DependencyInjection keeps a reference to every disposable
/// transient it resolves from the ROOT provider until the container itself is disposed. A tool
/// view model that implements <see cref="IDisposable"/> and is resolved that way therefore
/// outlives its tab: every closed Command Library or Notes tab kept its view model, and through
/// the handlers the view had set, its whole view tree and session closures, alive until exit.</para>
/// <para>The tool view owns the view model it creates and disposes it in its own
/// <see cref="IDisposable.Dispose"/>, so the container has no business tracking it.
/// <see cref="ActivatorUtilities.CreateInstance{T}"/> builds the instance from the container's
/// services without registering it for disposal. The type needs no registration of its own.</para>
/// </remarks>
internal static class ToolViewModelFactory
{
    /// <summary>
    /// Builds <typeparamref name="T"/> from the services of <paramref name="services"/>. The
    /// caller owns the instance and must dispose it.
    /// </summary>
    public static T Create<T>(IServiceProvider services)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);

        return ActivatorUtilities.CreateInstance<T>(services);
    }
}
