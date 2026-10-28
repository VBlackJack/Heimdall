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

using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Heimdall.App.Controls;

/// <summary>
/// A <see cref="TextBlock"/> that draws an icon-font glyph and stays out of the UI Automation
/// control and content views.
/// </summary>
/// <remarks>
/// <para>A glyph is a private-use code point: a screen reader that meets it as a text element
/// says nothing useful, or says the code point. WPF has no attached property to hide an element
/// (<c>AccessibilityView</c> is a UWP property), and <see cref="TextBlock"/> always exposes its
/// text, so the peer is replaced. The meaning the glyph carries must be written beside it as text
/// that is exposed.</para>
/// </remarks>
public sealed class DecorativeGlyphText : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    private sealed class DecorativePeer(DecorativeGlyphText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
