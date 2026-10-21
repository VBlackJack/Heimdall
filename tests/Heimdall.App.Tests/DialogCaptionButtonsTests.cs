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

using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Xml.Linq;
using Heimdall.App.Behaviors;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// A modal dialog offers no Minimize and no Maximize button. Minimizing one sent it to the
/// taskbar while its owner stayed disabled, so the main window ignored every click until the user
/// found the dialog again.
/// </summary>
[Collection(DialogCaptionButtonsCollection.Name)]
public sealed class DialogCaptionButtonsTests
{
    private const string ToolWindowStyle = "ToolWindow";
    private const string BehaviorAttribute = "DialogChromeBehavior.HideCaptionButtons";
    private const string BehaviorNamespace = "clr-namespace:Heimdall.App.Behaviors";

    // WS_MINIMIZEBOX | WS_MAXIMIZEBOX, written out rather than read from the behavior so that the
    // test does not trust the constants it is checking.
    private const int CaptionBoxes = 0x00020000 | 0x00010000;

    /// <summary>Dialogs counted on 2026-10-21: 35. A floor, so a broken glob cannot pass.</summary>
    private const int MinimumDialogCount = 30;

    [Fact]
    public void EveryDialogWindow_HidesItsMinimizeAndMaximizeButtons()
    {
        string dialogsDirectory = Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Dialogs");
        string[] files = Directory.GetFiles(dialogsDirectory, "*.xaml", SearchOption.TopDirectoryOnly);

        List<string> windows = [];
        List<string> offenders = [];
        foreach (string file in files)
        {
            XElement root = XDocument.Load(file).Root!;
            if (root.Name.LocalName != "Window")
            {
                continue;
            }

            windows.Add(Path.GetFileName(file));
            if (!IsToolWindow(root) && !HidesCaptionButtons(root))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            windows.Count >= MinimumDialogCount,
            $"Only {windows.Count} dialog window(s) found under {dialogsDirectory}; the walk is "
            + "broken, not the dialogs.");
        Assert.True(
            offenders.Count == 0,
            "These dialogs keep their Minimize and Maximize buttons. Declare "
            + "behaviors:DialogChromeBehavior.HideCaptionButtons=\"True\" on the Window, or "
            + "WindowStyle=\"ToolWindow\": " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheStyleArithmetic_ClearsBothBoxesAndNothingElse()
    {
        const int overlappedWindow = 0x00CF0000;

        int cleared = DialogChromeBehavior.WithoutCaptionButtons(overlappedWindow);

        Assert.Equal(0, cleared & CaptionBoxes);
        Assert.Equal(overlappedWindow & ~CaptionBoxes, cleared);
    }

    [Fact]
    public void TheProperty_SubscribesToSourceInitialized_AndClearingItUnsubscribes()
    {
        // No handle is created and no window is shown: the native style call itself is not
        // exercised in the test host, only the arithmetic above and the subscription below.
        RunOnStaThread(() =>
        {
            Window window = new();

            // Positive control: a window without the property carries no handler of ours, so
            // the census below can tell the two states apart.
            Assert.Equal(0, CountBehaviorHandlers(window));

            DialogChromeBehavior.SetHideCaptionButtons(window, true);
            Assert.Equal(1, CountBehaviorHandlers(window));

            // Setting it twice must not stack a second handler.
            DialogChromeBehavior.SetHideCaptionButtons(window, false);
            DialogChromeBehavior.SetHideCaptionButtons(window, true);
            Assert.Equal(1, CountBehaviorHandlers(window));

            DialogChromeBehavior.SetHideCaptionButtons(window, false);
            Assert.Equal(0, CountBehaviorHandlers(window));
        });
    }

    /// <summary>
    /// The SourceInitialized handlers on <paramref name="window"/> declared by the behavior.
    /// </summary>
    /// <remarks>
    /// WPF keeps the event in the window's private handler list under a private key and exposes no
    /// public way to enumerate it, so the store is read by reflection. Each lookup asserts that
    /// it resolved, so a WPF change that renames a member fails here instead of reading as zero.
    /// </remarks>
    private static int CountBehaviorHandlers(Window window)
    {
        const BindingFlags NonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;
        const BindingFlags AnyInstance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        FieldInfo? keyField = typeof(Window).GetField("EVENT_SOURCEINITIALIZED", NonPublicStatic);
        Assert.NotNull(keyField);
        object? key = keyField.GetValue(null);
        Assert.NotNull(key);

        // Window keeps its own events in a System.ComponentModel.EventHandlerList.
        PropertyInfo? eventsProperty = typeof(Window).GetProperty("Events", AnyInstance);
        Assert.NotNull(eventsProperty);
        EventHandlerList? events = (EventHandlerList?)eventsProperty.GetValue(window);
        Assert.NotNull(events);
        Delegate? handlers = events[key];

        return handlers?.GetInvocationList()
            .Count(handler => handler.Method.DeclaringType == typeof(DialogChromeBehavior)) ?? 0;
    }

    private static bool IsToolWindow(XElement root)
        => string.Equals((string?)root.Attribute("WindowStyle"), ToolWindowStyle, StringComparison.Ordinal);

    private static bool HidesCaptionButtons(XElement root)
    {
        foreach (XAttribute attribute in root.Attributes())
        {
            if (!attribute.Name.LocalName.Equals(BehaviorAttribute, StringComparison.Ordinal))
            {
                continue;
            }

            bool mapsToBehaviors = string.Equals(
                attribute.Name.NamespaceName, BehaviorNamespace, StringComparison.Ordinal);
            if (mapsToBehaviors
                && string.Equals(attribute.Value, bool.TrueString, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DialogCaptionButtonsCollection
{
    public const string Name = "DialogCaptionButtons";
}
