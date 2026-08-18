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

namespace Heimdall.App.ViewModels.CommandPalette;

/// <summary>
/// Creates the command palette for a given owner.
/// </summary>
/// <remarks>
/// The palette needs its owner, and the owner needs the palette. Registering the palette itself would
/// therefore close a cycle in the container. A factory breaks it by taking the owner as an argument
/// rather than as a dependency: the container resolves the factory, and the owner supplies itself at
/// the moment it builds its palette.
/// <para>
/// The factory also holds the dependencies that exist only to serve the palette, so they no longer
/// have to travel through the owner's constructor.
/// </para>
/// </remarks>
public interface ICommandPaletteViewModelFactory
{
    /// <summary>
    /// Builds a palette bound to <paramref name="owner"/>.
    /// </summary>
    /// <param name="owner">The view model the palette acts upon.</param>
    CommandPaletteViewModel Create(MainViewModel owner);
}
