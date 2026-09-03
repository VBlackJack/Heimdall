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

namespace Heimdall.App.Services;

/// <summary>Locale keys for the line that says where a certificate question came from.</summary>
public static class RdpTrustPromptOwnerLocaleKeys
{
    /// <summary>The tab is known and its window is not, or adds nothing.</summary>
    public const string Tab = "RdpCertPromptOwnerTab";

    /// <summary>Both are known, which is the case that matters for detached windows.</summary>
    public const string TabInWindow = "RdpCertPromptOwnerTabInWindow";

    /// <summary>Only the window is known.</summary>
    public const string Window = "RdpCertPromptOwnerWindow";
}

/// <summary>Which sentence names the tab or window a certificate question belongs to.</summary>
/// <param name="Key">The locale key to format.</param>
/// <param name="Arguments">Its arguments, in order.</param>
public readonly record struct RdpTrustPromptOwnerText(string Key, string[] Arguments);

/// <summary>
/// Decides how a question names the tab or window that owns it.
/// </summary>
/// <remarks>
/// <para>Pure, and separate from any view, because this is the part worth pinning: building a
/// WPF window in a test seals application-level styles onto the shared dispatcher and takes
/// unrelated tests down with it.</para>
/// <para><b>Three sentences rather than one with optional halves.</b> A single format with an
/// empty argument leaves the window clause standing with nothing in it, and a question whose
/// own text looks broken is a question the user stops reading.</para>
/// <para><b>What this line is for, stated plainly because it was once expected to do more.</b>
/// It says where on screen the question is. It does NOT tell two machines apart: the profile
/// name, the endpoint and the gateway route do that, and they are the fields to look at when two
/// questions read alike. Two sessions of one profile reach one machine and share one trust set,
/// so the same answer is the right answer for both - there is nothing here that needs
/// distinguishing, and nothing user-facing that would distinguish it.</para>
/// <para><b>The tab name passed in is the announced one, not the displayed one.</b>
/// <c>SessionTabViewModel.DisplayTitle</c> is identical by construction for two sessions of one
/// profile; <c>AccessibleName</c> is the same string with an ordinal added where the titles
/// collide, which is the only index this application already computes. It computes it and
/// ANNOUNCES it: the ordinal is carried to a screen reader through the tab container's
/// AutomationProperties.Name and is nowhere on the tab header, which binds DisplayTitle.</para>
/// <para><b>The window is dropped when it says nothing the tab has not.</b> The rule used to be
/// equality, justified by a detached window taking its tab's title - which it does not:
/// <c>FloatingSessionWindow</c> titles itself with the <c>SessionDetachTitle</c> format, so the
/// two strings never matched and the window clause always stood. Two same-named sessions in two
/// detached windows then read character for character alike, at twice the length. Containment is
/// the rule that implements what equality was documented to do.</para>
/// </remarks>
public static class RdpTrustPromptOwner
{
    /// <summary>The sentence to show, or null when neither name is known.</summary>
    /// <param name="tabTitle">How the tab holding the session is announced.</param>
    /// <param name="windowTitle">The title of the window holding that tab.</param>
    public static RdpTrustPromptOwnerText? Describe(string? tabTitle, string? windowTitle)
    {
        string? tab = Trimmed(tabTitle);
        string? window = Trimmed(windowTitle);

        if (tab is not null && window is not null && !RepeatsTheTab(tab, window))
        {
            return new RdpTrustPromptOwnerText(
                RdpTrustPromptOwnerLocaleKeys.TabInWindow,
                [tab, window]);
        }

        if (tab is not null)
        {
            return new RdpTrustPromptOwnerText(RdpTrustPromptOwnerLocaleKeys.Tab, [tab]);
        }

        return window is null
            ? null
            : new RdpTrustPromptOwnerText(RdpTrustPromptOwnerLocaleKeys.Window, [window]);
    }

    /// <summary>The name to show for a tab: the announced one, or the displayed one.</summary>
    /// <param name="accessibleName">
    /// How the tab is announced. Assigned by <c>ConnectionViewModel</c>, the only owner able to
    /// see the sibling tabs and so to add an ordinal where two titles collide.
    /// </param>
    /// <param name="displayTitle">What the tab shows, which is the fallback.</param>
    /// <remarks>
    /// <b>Which of the two, and why it is a decision rather than a field read.</b>
    /// <c>DisplayTitle</c> is identical by construction for two sessions of one profile, so
    /// feeding the owner line that made it read the same twice in exactly the case two
    /// same-named sessions were the problem. The announced name is the same string with the
    /// ordinal already in it - the only index this application computes for colliding titles. It
    /// is ANNOUNCED and not drawn: the tab header binds DisplayTitle, and the ordinal reaches the
    /// UI only as the container's AutomationProperties.Name, so a reader comparing this line
    /// against the tab strip finds no number there and has to count the tabs instead. It is blank
    /// until its owner has run once, and a session detached into its own window keeps whatever
    /// it was last given, so the displayed title stays the fallback rather than being replaced.
    /// </remarks>
    public static string? AnnouncedName(string? accessibleName, string? displayTitle)
        => string.IsNullOrWhiteSpace(accessibleName) ? displayTitle : accessibleName;

    /// <summary>Whether the window title is built out of the tab's own name.</summary>
    /// <remarks>
    /// Containment rather than equality, because the one window title this application derives
    /// from a session decorates it: "Production" becomes "Production - Detached". A window whose
    /// title merely wraps the tab's adds a clause that is identical in every such question, so it
    /// lengthens the sentence without identifying anything.
    /// </remarks>
    private static bool RepeatsTheTab(string tab, string window)
        => window.Contains(tab, StringComparison.Ordinal);

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
