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

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Heimdall.App.Controls;

/// <summary>
/// A row of the session tree, carrying a peer that reports the view model's selection.
/// </summary>
/// <remarks>
/// It produces itself as the container for its own children, so every level of the tree - not just
/// the top one - reports selection through <see cref="SessionTreeViewItemAutomationPeer"/>. Styling
/// is unaffected: a style or template targeting <see cref="TreeViewItem"/> applies to this type.
/// </remarks>
public class SessionTreeViewItem : TreeViewItem
{
    protected override DependencyObject GetContainerForItemOverride() => new SessionTreeViewItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is SessionTreeViewItem;

    protected override AutomationPeer OnCreateAutomationPeer()
        => new Automation.SessionTreeViewItemAutomationPeer(this);
}
