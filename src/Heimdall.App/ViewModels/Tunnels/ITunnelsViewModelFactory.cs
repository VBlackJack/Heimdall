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

namespace Heimdall.App.ViewModels.Tunnels;

/// <summary>
/// Creates the tunnels view model for a given owner.
/// </summary>
/// <remarks>
/// The tunnels view model needs its owner, and the owner exposes it, so registering it directly would
/// close a cycle in the container. Taking the owner as an argument to <see cref="Create"/> breaks that,
/// the same way the command palette is built.
/// <para>
/// The factory also owns the tunnelling collaborators. They existed on the owner's constructor only to
/// be handed straight to this view model, which meant the owner named three services it never speaks to.
/// </para>
/// </remarks>
public interface ITunnelsViewModelFactory
{
    /// <summary>
    /// Builds a tunnels view model bound to <paramref name="owner"/>.
    /// </summary>
    /// <param name="owner">The shell the tunnels view model reports to.</param>
    TunnelsViewModel Create(MainViewModel owner);
}
