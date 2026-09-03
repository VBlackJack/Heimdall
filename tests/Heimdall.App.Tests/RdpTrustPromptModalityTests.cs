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

using System.IO;
using System.Windows;
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.Core.Certificates;

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes the property a comment used to assert: the RDP certificate question is not modal to
/// the application.
/// </summary>
/// <remarks>
/// <para><b>Why a test and not a comment.</b> The watchdog suspension shipped with a paragraph
/// explaining that the Cancel button was unreachable "because the trust dialog is
/// application-modal". That sentence was true when it was written and false the moment the
/// question moved into the pane, and nothing would have failed had it been left standing. What
/// makes a claim like that safe to rely on is a test that goes red when it stops holding, so
/// the claim is pinned here rather than restated in prose.</para>
/// <para><b>What the property is.</b> <c>Window.ShowDialog()</c> is application-modal whatever
/// its owner: while one is up, every other window in the process is disabled at the Win32 level,
/// so the Cancel button each connecting session shows reports itself enabled and cannot be
/// clicked. Re-owning the window does not change that; not being a window is what changes it.
/// So the invariant measured is: nothing on the RDP trust path is a <c>Window</c>, and nothing
/// on it shows one.</para>
/// </remarks>
public sealed class RdpTrustPromptModalityTests
{
    [Fact]
    public void TheOnlyWayToAskAboutAnRdpCertificateIsThePanePrompt()
    {
        // Reflection rather than a source reading, and over the whole assembly: a second
        // implementation is exactly how a modal window would come back, and it would come back
        // beside this one rather than inside it.
        Type[] implementations = typeof(PaneRdpCertificateTrustPrompt).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface)
            .Where(typeof(IRdpCertificateTrustPrompt).IsAssignableFrom)
            .ToArray();

        Assert.Equal([typeof(PaneRdpCertificateTrustPrompt)], implementations);
    }

    [Fact]
    public void NothingOnTheTrustPathIsAWindow()
    {
        // The positive control for the count is the assertion itself: Window is the type whose
        // ShowDialog carries application modality, so a prompt type deriving from it is the
        // whole defect back.
        foreach (Type type in TrustPathTypes())
        {
            Assert.False(
                typeof(Window).IsAssignableFrom(type),
                $"{type.Name} is a Window, so showing it would disable every other window in "
                    + "the application while one certificate question is open.");
        }
    }

    [Fact]
    public void TheReflectionAboveIsNotLookingAtAnEmptySet()
    {
        // Without this the two tests above pass by finding nothing, which is the failure mode
        // of every reflection guard.
        Assert.NotEmpty(TrustPathTypes());
        Assert.Contains(typeof(PaneRdpCertificateTrustPrompt), TrustPathTypes());
    }

    [Fact]
    public void ThePromptShowsNoDialogOfItsOwn()
    {
        // An absence over production source, which inverts the risk: folding a statement can
        // only keep this passing, and the only way to break it is to write back the call it
        // forbids. Read from code rather than from text, because the type's own remarks say
        // what it replaced and name ShowDialog while doing so.
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Heimdall.App", "Services", "PaneRdpCertificateTrustPrompt.cs"));
        string logic = ViewSource.WithoutCommentsAndLiterals(source);

        // The blanking really ran and really only blanked: same length, different content. A
        // file that came back unchanged, or empty, would satisfy the absence below for the
        // wrong reason.
        Assert.Equal(source.Length, logic.Length);
        Assert.NotEqual(source, logic);
        Assert.DoesNotContain("ShowDialog", logic, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWatchdogPolicyNoLongerJustifiesItselfByModality()
    {
        // The stale claim itself, named. It said the Cancel button could not be clicked while
        // any question was on screen; the button is reachable now, in this pane and every
        // other, and a reader who believed the old sentence would conclude the opposite of
        // what the code does.
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(),
            "src", "Heimdall.App", "Views", "EmbeddedRdp", "RdpConnectWatchdogPolicy.cs"));

        Assert.DoesNotContain(
            "The button reports itself enabled and is not clickable",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>Every type on the RDP trust path, by name.</summary>
    /// <remarks>
    /// Deliberately not every certificate prompt in the application. SSH host keys and FTPS
    /// certificates are still asked through top-level modal windows, which is correct for them:
    /// their questions do not belong to a session pane and have nowhere else to go. This lot
    /// changed the RDP question only, and a guard that swept them all in would be asserting a
    /// decision nobody took.
    /// </remarks>
    private static Type[] TrustPathTypes() => typeof(PaneRdpCertificateTrustPrompt).Assembly
        .GetTypes()
        .Where(type => type.Name.Contains("RdpTrustPrompt", StringComparison.Ordinal)
            || type.Name.Contains("RdpCertificatePrompt", StringComparison.Ordinal)
            || type.Name.Contains("RdpCertificateTrustPrompt", StringComparison.Ordinal))
        .ToArray();

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root from test binary directory: {AppContext.BaseDirectory}");
    }
}
