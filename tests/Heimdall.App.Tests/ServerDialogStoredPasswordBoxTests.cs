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
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Xml.Linq;
using Heimdall.App.Views.Dialogs;

namespace Heimdall.App.Tests;

/// <summary>
/// Removing a stored password has two halves and the view model owns only one of them: a
/// PasswordBox keeps its own copy of what was typed, and OnSaveClick reads every box back over the
/// view model on the way out. Empty the view model alone and the card announces the removal while
/// the save writes the text still sitting in the box.
/// </summary>
/// <remarks>
/// The Clear button is read out of the shipped XAML rather than described here, so the pair that
/// makes the removal work - the click handler and the Tag naming the card's own box - is observed
/// where it has to hold, and that same pair is what drives the behaviour test. A ServerDialog
/// cannot be built here to press the button for real: it needs a WPF Application, a process gets
/// exactly one, and another dialog test already owns it. What stays unobserved is the handler in
/// between, whose body is the single call these two tests bracket; a Click naming a method the
/// dialog does not declare fails the XAML compiler rather than this suite.
/// </remarks>
public sealed class ServerDialogStoredPasswordBoxTests
{
    private const string ClickHandler = "OnClearStoredCredentialClick";
    private const string TypedReplacement = "the-user-changed-his-mind";

    private static readonly string[] Credentials = ["Rdp", "WinRm", "Ssh", "Vnc", "Ftp"];

    [Fact]
    public void EveryClearButtonIsWiredToEmptyItsOwnCardsPasswordBox()
    {
        XDocument document = LoadServerDialogXaml();

        foreach (string credential in Credentials)
        {
            (string passwordBoxName, XElement clearButton) = CredentialCard(document, credential);

            Assert.Equal(ClickHandler, clearButton.Attribute("Click")?.Value);
            Assert.Equal(passwordBoxName, clearButton.Attribute("Tag")?.Value);
        }
    }

    [Fact]
    public void ClearingAStoredPassword_EmptiesTheBoxTheSavePathReadsBack()
    {
        XDocument document = LoadServerDialogXaml();

        foreach (string credential in Credentials)
        {
            // Two independent readings of the shipped card: the name the box answers to, and what
            // the button asks for. The resolver keys on the first, so a button that asks for
            // nothing - or for the wrong box - leaves the typed text where the save path finds it.
            (string passwordBoxName, XElement clearButton) = CredentialCard(document, credential);
            string? tag = clearButton.Attribute("Tag")?.Value;

            RunOnStaThread(() =>
            {
                // The user opened the card, typed a replacement, then decided to store nothing.
                PasswordBox passwordBox = new() { Password = TypedReplacement };

                ServerDialogCredentialBoxes.ClearTaggedBox(
                    new Button { Tag = tag },
                    name => string.Equals(name, passwordBoxName, StringComparison.Ordinal) ? passwordBox : null);

                Assert.Equal("", passwordBox.Password);
            });
        }
    }

    // The counterweight: a card whose Clear button was never pressed must keep what was typed in
    // it. A fix that empties every box on any removal loses the password the user is setting.
    [Fact]
    public void ClearingOneCardLeavesTheOtherBoxesAlone()
    {
        RunOnStaThread(() =>
        {
            PasswordBox cleared = new() { Password = TypedReplacement };
            PasswordBox untouched = new() { Password = TypedReplacement };
            Button clearButton = new() { Tag = "RdpPasswordBox" };

            ServerDialogCredentialBoxes.ClearTaggedBox(
                clearButton,
                name => name == "RdpPasswordBox" ? cleared : untouched);

            Assert.Equal("", cleared.Password);
            Assert.Equal(TypedReplacement, untouched.Password);
        });
    }

    /// <summary>
    /// Reads one credential card out of the shipped dialog: the name of its password box, and the
    /// Clear button that removes that card's stored secret.
    /// </summary>
    /// <remarks>
    /// The button is looked up inside the card rather than anywhere in the file, so a Tag naming
    /// another card's box - the copy-paste this row invites - is caught here and not by a user
    /// whose FTP password disappeared when he removed the RDP one.
    /// </remarks>
    private static (string PasswordBoxName, XElement ClearButton) CredentialCard(
        XDocument document,
        string credential)
    {
        XElement passwordBox = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "PasswordBox"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value == credential + "PasswordBox"));

        string passwordBoxName = Assert.Single(
            passwordBox.Attributes(),
            attribute => attribute.Name.LocalName == "Name").Value;

        XElement card = passwordBox.Ancestors().First(element => element.Name.LocalName == "Border");

        XElement clearButton = Assert.Single(
            card.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value
                    == $"{{Binding ClearStored{credential}PasswordCommand}}");

        return (passwordBoxName, clearButton);
    }

    private static XDocument LoadServerDialogXaml()
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string path = Path.Combine(
            repoRoot,
            "src",
            "Heimdall.App",
            "Views",
            "Dialogs",
            "ServerDialog.xaml");

        Assert.True(File.Exists(path), $"Server dialog XAML not found: {path}");
        return XDocument.Load(path);
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
