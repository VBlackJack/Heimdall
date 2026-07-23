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
/// Shared editing state for nodes that can be renamed directly in the session tree.
/// </summary>
public interface IInlineRenameNode
{
    bool IsEditing { get; set; }

    string EditName { get; set; }

    void BeginInlineEdit();

    void CancelInlineEdit();

    void CompleteInlineEdit();
}

/// <summary>
/// Starts inline editing only for supported, persistent tree nodes.
/// </summary>
internal static class SessionTreeInlineRename
{
    public static bool TryBeginEdit(object? node)
    {
        if (node is FolderViewModel { FullPath.Length: 0 })
        {
            return false;
        }

        if (node is not IInlineRenameNode editableNode)
        {
            return false;
        }

        editableNode.BeginInlineEdit();
        return true;
    }

    public static void CompleteEdit(
        IInlineRenameNode node,
        Action<IInlineRenameNode> restoreFocus)
    {
        node.CompleteInlineEdit();
        restoreFocus(node);
    }

    public static void CancelEdit(
        IInlineRenameNode node,
        Action<IInlineRenameNode> restoreFocus)
    {
        node.CancelInlineEdit();
        restoreFocus(node);
    }
}
