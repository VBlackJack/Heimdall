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
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The KeePass presets start with the installed program's full path when it is found.
/// </summary>
public sealed class CredentialProviderPresetResolutionTests
{
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string KpScriptRelative = Path.Combine("KeePass Password Safe 2", "KPScript.exe");
    private static readonly string KeePassXcRelative = Path.Combine("KeePassXC", "keepassxc-cli.exe");

    private const string KpScriptLabel = "KeePass2 (KPScript)";
    private const string KeePassXcLabel = "KeePassXC";
    private const string KeePassXcKeyFileLabel = "KeePassXC (key file)";

    [Fact]
    public void KpScript_UsesTheProgramFilesPath_WhenTheFileExists()
    {
        string expected = Path.Combine(ProgramFiles, KpScriptRelative);
        var service = new CredentialProviderPresetService(path => path == expected);

        string command = CommandOf(service, KpScriptLabel);

        Assert.StartsWith($"\"{expected}\" -c:GetEntryString", command);
    }

    [Fact]
    public void KpScript_FallsBackToTheX86Folder()
    {
        string expected = Path.Combine(ProgramFilesX86, KpScriptRelative);
        var service = new CredentialProviderPresetService(path => path == expected);

        Assert.StartsWith($"\"{expected}\" -c:GetEntryString", CommandOf(service, KpScriptLabel));
    }

    [Fact]
    public void KpScript_KeepsTheBareName_WhenNothingIsInstalled()
    {
        var service = new CredentialProviderPresetService(_ => false);

        Assert.StartsWith("KPScript.exe -c:GetEntryString", CommandOf(service, KpScriptLabel));
    }

    [Theory]
    [InlineData(KeePassXcLabel)]
    [InlineData(KeePassXcKeyFileLabel)]
    public void KeePassXc_UsesTheInstalledPath_WhenTheFileExists(string label)
    {
        string expected = Path.Combine(ProgramFiles, KeePassXcRelative);
        var bare = new CredentialProviderPresetService(_ => false);
        var service = new CredentialProviderPresetService(path => path == expected);

        string command = CommandOf(service, label);
        string bareCommand = CommandOf(bare, label);

        Assert.StartsWith($"\"{expected}\" show", command);
        Assert.StartsWith("keepassxc-cli show", bareCommand);
        Assert.Equal(bareCommand["keepassxc-cli".Length..], command[(expected.Length + 2)..]);
    }

    [Theory]
    [InlineData(KpScriptLabel)]
    [InlineData(KeePassXcLabel)]
    public void IndexOfCommand_RecognisesTheBareAndTheFullPathForms(string label)
    {
        var installed = new CredentialProviderPresetService(_ => true);
        var absent = new CredentialProviderPresetService(_ => false);
        int index = IndexOf(absent, label);

        string fullPath = CommandOf(installed, label);
        string bare = CommandOf(absent, label);

        Assert.StartsWith("\"", fullPath);
        Assert.DoesNotContain("\"", bare[..1]);
        Assert.Equal(index, absent.IndexOfCommand(fullPath));
        Assert.Equal(index, installed.IndexOfCommand(bare));
        Assert.Equal(index, installed.IndexOfCommand(fullPath));
        Assert.Equal(index, absent.IndexOfCommand(bare));
    }

    [Fact]
    public void IndexOfCommand_RecognisesAFullPathUnderAnotherInstallRoot()
    {
        var service = new CredentialProviderPresetService(_ => false);
        string bare = CommandOf(service, KpScriptLabel);
        string rest = bare["KPScript.exe".Length..];
        string otherRoot = $"\"D:\\Tools\\KeePass\\KPScript.exe\"{rest}";

        Assert.Equal(IndexOf(service, KpScriptLabel), service.IndexOfCommand(otherRoot));
    }

    [Theory]
    [InlineData("\"D:\\Tools\\KeePass\\KPScript.exe\" -c:GetEntryString \"{Database}\" -Field:UserName")]
    [InlineData("\"D:\\Tools\\other.exe\" -c:GetEntryString \"{Database}\" -Field:Password -ref-Title:\"{Title}\" -FailIfNoEntry")]
    public void IndexOfCommand_ADifferentArgumentListOrProgramIsCustom(string command)
    {
        var service = new CredentialProviderPresetService(_ => false);

        Assert.Equal(0, service.IndexOfCommand(command));
    }

    private static string CommandOf(CredentialProviderPresetService service, string label)
    {
        Assert.True(service.TryGetPreset(IndexOf(service, label), out string command, out _));
        return command;
    }

    private static int IndexOf(CredentialProviderPresetService service, string label)
    {
        int index = service.GetPresetLabels(key => key).ToList().IndexOf(label);
        Assert.True(index > 0, $"preset '{label}' not found");
        return index;
    }
}
