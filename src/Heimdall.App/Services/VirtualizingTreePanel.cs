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

using System.Windows.Controls;

namespace Heimdall.App.Services;

/// <summary>
/// Recycling items host used by the session tree. It exposes the protected
/// index realization hook so an off-screen tree path can be materialized
/// before focus, selection, or BringIntoView is requested.
/// </summary>
public sealed class VirtualizingTreePanel : VirtualizingStackPanel
{
    /// <summary>
    /// Generates and scrolls the container at <paramref name="index"/> into
    /// the panel's realized range.
    /// </summary>
    public void RealizeIndex(int index)
    {
        BringIndexIntoView(index);
    }
}
