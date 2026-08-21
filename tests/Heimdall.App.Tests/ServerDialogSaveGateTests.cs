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
using System.Xml.Linq;
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Tests;

/// <summary>
/// The Add Session dialog opens on a protocol picker with no fields. Save carries
/// IsDefault, so on that step a click and the Enter key both used to reach a handler
/// that returned without a word: the guard existed, the feedback did not.
/// </summary>
/// <remarks>
/// The predicate and the binding fail independently, so they are asserted separately.
/// A test that only exercised ShowFormFields would still pass with the IsEnabled
/// attribute deleted from the XAML, which is the half that actually reaches the user.
/// </remarks>
public sealed class ServerDialogSaveGateTests
{
    private const string DialogXamlRelativePath =
        "src/Heimdall.App/Views/Dialogs/ServerDialog.xaml";

    [Fact]
    public void SaveButton_IsGatedOnShowFormFields()
    {
        XElement saveButton = LoadDialog()
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "Button"
                && element.Attributes().Any(a =>
                    a.Name.LocalName == "Name" && a.Value == "DlgSrv_SaveBtn"));

        string? isEnabled = saveButton.Attribute("IsEnabled")?.Value;

        Assert.NotNull(isEnabled);
        Assert.Contains("ShowFormFields", isEnabled);
    }

    [Fact]
    public void SaveButton_IsStillTheDefaultButton()
    {
        // Disabling it on the picker step must not cost Enter its meaning once a
        // protocol has been chosen.
        XElement saveButton = LoadDialog()
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "Button"
                && element.Attributes().Any(a =>
                    a.Name.LocalName == "Name" && a.Value == "DlgSrv_SaveBtn"));

        Assert.Equal("True", saveButton.Attribute("IsDefault")?.Value);
    }

    [Fact]
    public void ShowFormFields_IsFalseOnThePickerStep_AndTrueOnceAProtocolIsChosen()
    {
        var viewModel = new ServerDialogViewModel
        {
            IsEditMode = false,
            IsProtocolSelected = false
        };

        Assert.False(viewModel.ShowFormFields);

        viewModel.IsProtocolSelected = true;

        Assert.True(viewModel.ShowFormFields);
    }

    [Fact]
    public void ShowFormFields_IsTrueImmediatelyWhenEditingAnExistingProfile()
    {
        // Editing skips the picker, so the button must be live from the first frame.
        var viewModel = new ServerDialogViewModel
        {
            IsEditMode = true,
            IsProtocolSelected = false
        };

        Assert.True(viewModel.ShowFormFields);
    }

    private static XDocument LoadDialog()
    {
        string path = Path.Combine(
            FindRepoRoot(),
            DialogXamlRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(path), $"Server dialog XAML not found: {path}");
        return XDocument.Load(path);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }
}
