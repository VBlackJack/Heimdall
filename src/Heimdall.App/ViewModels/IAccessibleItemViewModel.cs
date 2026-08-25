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
/// Item view model that supplies its own UI Automation identity.
/// </summary>
/// <remarks>
/// UI Automation reads the generated item container, never the visual tree a data template
/// builds inside it: a plain <c>Border</c> or <c>Grid</c> has no automation peer at all, so an
/// <c>AutomationProperties.Name</c> written there is inert and the container falls back to
/// <c>ToString()</c> of the bound item - which is the raw view model type name. Implementing this
/// interface is what makes an item eligible for
/// <c>Heimdall.App.Behaviors.ItemContainerAccessibilityBehavior</c>, which applies the metadata
/// on the container itself.
/// </remarks>
public interface IAccessibleItemViewModel
{
    /// <summary>Localized identity announced by screen readers for this item.</summary>
    string AccessibleName { get; }

    /// <summary>
    /// Localized keyboard guidance, or <see langword="null"/> when the item offers none.
    /// </summary>
    string? AccessibleHelpText { get; }
}
