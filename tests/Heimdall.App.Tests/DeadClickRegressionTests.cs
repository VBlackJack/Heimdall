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
using Heimdall.App.ViewModels.Tools;

namespace Heimdall.App.Tests;

public sealed class DeadClickRegressionTests
{
    [Fact]
    public void HashBeginFileHash_SourceContract_GuardsCanExecuteBeforeViewMutation()
    {
        var sourcePath = Path.Combine(
            FindRepoRoot(),
            "src",
            "Heimdall.App",
            "Views",
            "Tools",
            "HashGeneratorView.xaml.cs");
        var source = File.ReadAllText(sourcePath);
        var methodBody = ExtractMethodBody(source, "private void BeginFileHash(string filePath)");

        // This is deliberately a static source guard, like the repository's other source-contract
        // tests. The private WPF view method is not unit-test reachable without a forbidden view
        // refactor, so this proxy checks statement order rather than claiming behavioural coverage.
        var guardIndex = methodBody.IndexOf("_vm.HashFileCommand.CanExecute(filePath)", StringComparison.Ordinal);
        var timerIndex = methodBody.IndexOf("_debounceTimer?.Stop();", StringComparison.Ordinal);
        var suppressIndex = methodBody.IndexOf("_suppressInputTextChanged = true;", StringComparison.Ordinal);
        var clearIndex = methodBody.IndexOf("TxtInput.Text = string.Empty;", StringComparison.Ordinal);
        var executeIndex = methodBody.IndexOf("_vm.HashFileCommand.Execute(filePath);", StringComparison.Ordinal);

        Assert.True(guardIndex >= 0, "BeginFileHash must check HashFileCommand.CanExecute.");
        Assert.True(timerIndex > guardIndex, "BeginFileHash must check CanExecute before stopping the debounce timer.");
        Assert.True(suppressIndex > timerIndex, "BeginFileHash must suppress input changes after stopping the debounce timer.");
        Assert.True(clearIndex > suppressIndex, "BeginFileHash must clear the text input only after CanExecute succeeds.");
        Assert.True(executeIndex > clearIndex, "BeginFileHash must execute the command after preparing the view.");
    }

    [Fact]
    public void SmbCanToggleEnumeration_TracksBlankBusyAndReadyStates()
    {
        var vm = new SmbEnumeratorViewModel
        {
            HostInput = " ",
            IsBusy = false,
        };
        Assert.False(vm.CanToggleEnumeration);

        vm.IsBusy = true;
        Assert.True(vm.CanToggleEnumeration);

        vm.IsBusy = false;
        vm.HostInput = "server.local";
        Assert.True(vm.CanToggleEnumeration);
    }

    [Fact]
    public void DeadClickButtons_XamlContract_CarriesExpectedIsEnabledBindings()
    {
        AssertButtonIsEnabledBinding(
            Path.Combine("src", "Heimdall.App", "Views", "Tools", "SmbEnumeratorView.xaml"),
            "BtnEnumerate",
            "CanToggleEnumeration");
        AssertButtonIsEnabledBinding(
            Path.Combine("src", "Heimdall.App", "Views", "Tools", "HashGeneratorView.xaml"),
            "BtnBrowseFile",
            "IsFileHashing");
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Method signature was not found: {signature}");

        var openingBraceIndex = source.IndexOf('{', signatureIndex + signature.Length);
        Assert.True(openingBraceIndex >= 0, $"Opening brace was not found for: {signature}");

        var depth = 0;
        for (var index = openingBraceIndex; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[(openingBraceIndex + 1)..index];
                    }

                    break;
            }
        }

        throw new InvalidDataException($"Closing brace was not found for: {signature}");
    }

    private static void AssertButtonIsEnabledBinding(string relativePath, string buttonName, string expectedBinding)
    {
        var sourcePath = Path.Combine(FindRepoRoot(), relativePath);
        var document = XDocument.Load(sourcePath);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = document
            .Descendants()
            .SingleOrDefault(element =>
                element.Name.LocalName == "Button" &&
                string.Equals((string?)element.Attribute(xaml + "Name"), buttonName, StringComparison.Ordinal));

        Assert.True(button is not null, $"{relativePath}: button {buttonName} was not found.");

        var isEnabled = button.Attribute("IsEnabled");
        Assert.True(
            isEnabled is not null &&
            isEnabled.Value.Contains(expectedBinding, StringComparison.Ordinal),
            $"{relativePath}: button {buttonName} must carry an IsEnabled binding referring to {expectedBinding}.");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
