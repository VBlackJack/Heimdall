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

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// What a Command Library export writes. The values are the answer indexes of the export
/// choice, in the order its buttons are shown.
/// </summary>
public enum CommandLibraryExportScope
{
    /// <summary>The actions the operator created or imported; the default.</summary>
    Mine = 0,

    /// <summary>What the action list shows now, every filter and the search applied.</summary>
    CurrentFilter = 1,

    /// <summary>Every action in the store, seeded ones included.</summary>
    Everything = 2,
}
