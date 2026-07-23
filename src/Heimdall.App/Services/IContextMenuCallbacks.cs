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

using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>
/// Callbacks surfaced by the window layer to <see cref="ContextMenuFactory"/>.
/// These actions require access to <see cref="MainWindow"/> state (DataContext,
/// modal dialog ownership, or instance helpers) and therefore cannot be inlined
/// into the factory.
/// </summary>
public interface IContextMenuCallbacks
{
    /// <summary>
    /// Opens the Notes tool tab for the supplied server, pre-selecting the
    /// given template kind (blank, daily, incident, procedure).
    /// </summary>
    void OpenNotesForServer(ServerItemViewModel server, NoteTemplateKind templateKind);

    /// <summary>
    /// Launches a user-configured external tool against the supplied server,
    /// resolving placeholder variables from the server's profile.
    /// </summary>
    void LaunchExternalTool(ServerItemViewModel server, ExternalToolDefinition tool);

    /// <summary>
    /// Launches an auto-detected third-party tool (Sysinternals, NirSoft, …)
    /// against the supplied server, handling UAC elevation when required.
    /// </summary>
    void LaunchDetectedTool(ServerItemViewModel server, ExternalToolInfo tool);

    /// <summary>
    /// Opens the Add-Tool picker dialog for the supplied folder path.
    /// <paramref name="group"/> is <c>null</c> when the menu was opened from
    /// an empty area of the TreeView.
    /// </summary>
    void AddToolFromMenu(string? group);

    /// <summary>
    /// Starts the shared inline rename editor for a server or folder node.
    /// </summary>
    void BeginInlineRename(object node)
    {
    }

    /// <summary>
    /// Selects and reveals a folder after a tree-rebuilding operation.
    /// </summary>
    void SelectFolder(string fullPath)
    {
    }
}
