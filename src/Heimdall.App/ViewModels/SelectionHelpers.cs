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

namespace Heimdall.App.ViewModels;

/// <summary>
/// Pure helpers for computing the visible server leaf order in the sessions tree.
/// The traversal mirrors the UI order: folders depth-first, then servers at each level.
/// </summary>
public static class SelectionHelpers
{
    public static IEnumerable<ServerItemViewModel> EnumerateVisibleLeaves(IEnumerable<FolderViewModel> folders)
    {
        foreach (var folder in folders)
        {
            if (folder.IsExpanded)
            {
                foreach (var child in EnumerateVisibleLeaves(folder.SubFolders))
                {
                    yield return child;
                }

                foreach (var server in folder.Servers)
                {
                    yield return server;
                }
            }
        }
    }

    /// <summary>
    /// Every row the tree shows, folders included, in display order: a folder, then its children
    /// when it is expanded.
    /// </summary>
    /// <param name="folders">The root folders.</param>
    public static IEnumerable<object> EnumerateVisibleNodes(IEnumerable<FolderViewModel> folders)
    {
        foreach (FolderViewModel folder in folders)
        {
            yield return folder;
            if (!folder.IsExpanded)
            {
                continue;
            }

            foreach (object child in folder.Children)
            {
                if (child is FolderViewModel subFolder)
                {
                    foreach (object node in EnumerateVisibleNodes([subFolder]))
                    {
                        yield return node;
                    }
                }
                else
                {
                    yield return child;
                }
            }
        }
    }
}
