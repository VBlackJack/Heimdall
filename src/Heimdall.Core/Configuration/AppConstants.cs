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

namespace Heimdall.Core.Configuration;

/// <summary>
/// Application-wide constants that are not protocol-specific.
/// </summary>
public static class AppConstants
{
    /// <summary>Maximum import file size for Command Library JSON imports.</summary>
    public const long MaxImportFileSizeBytes = 50 * 1024 * 1024;

    /// <summary>Maximum size of a single Command Library seed JSON file read at first launch.</summary>
    public const long MaxSeedFileSizeBytes = 100 * 1024;

    /// <summary>Maximum accepted length of an imported action Title.</summary>
    public const int MaxImportActionTitleLength = 200;

    /// <summary>Maximum accepted length of an imported action Category.</summary>
    public const int MaxImportActionCategoryLength = 100;

    /// <summary>Legacy application folder name used for migration detection.</summary>
    public const string LegacyAppFolderName = "RDPManager";

    /// <summary>Subdirectory under BaseDirectory for embedded CLI tools.</summary>
    public const string EmbeddedToolsSubdir = "Assets/Tools";

    /// <summary>
    /// Maximum number of bytes a ConPTY session buffers before the first
    /// <c>DataReceived</c> subscriber attaches. This preserves the child shell's
    /// bootstrap output (e.g. the initial prompt/banner) that is otherwise emitted
    /// before the terminal view subscribes. Once the cap is hit the session stops
    /// buffering further bootstrap bytes.
    /// </summary>
    public const int MaxConPtyBootstrapBufferBytes = 256 * 1024;
}
