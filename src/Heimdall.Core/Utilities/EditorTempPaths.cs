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

namespace Heimdall.Core.Utilities;

/// <summary>Where the remote file editor stages the files it opens.</summary>
/// <remarks>
/// One definition, read by the code that creates these directories and by the code that
/// sweeps them. Two independent copies of one path is how a sweeper ends up tidying a
/// directory nobody writes to while the real one grows forever.
/// </remarks>
public static class EditorTempPaths
{
    private const string ApplicationFolderName = "Heimdall";

    private const string EditFolderName = "edit";

    /// <summary>The editor's temporary root, one directory per open file beneath it.</summary>
    public static string Root =>
        Path.Combine(Path.GetTempPath(), ApplicationFolderName, EditFolderName);
}
