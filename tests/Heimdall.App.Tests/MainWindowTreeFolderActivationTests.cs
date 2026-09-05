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

using System.Windows.Input;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Enter on a focused folder toggles it, and nothing else does.
/// </summary>
/// <remarks>
/// The session activation resolver has always refused a folder, so that a folder holding the
/// focus never connects a session selected elsewhere. That refusal stays; this is the decision
/// for what the folder itself does with the key.
/// </remarks>
public sealed class MainWindowTreeFolderActivationTests
{
    [Fact]
    public void Enter_OnFocusedFolder_ResolvesThatFolder()
    {
        FolderViewModel folder = new() { Name = "Production", FullPath = "Production" };

        FolderViewModel? resolved = MainWindow.ResolveTreeFolderActivation(
            Key.Enter,
            ModifierKeys.None,
            isInlineRenameEditorSource: false,
            isRepeat: false,
            folder);

        Assert.Same(folder, resolved);
    }

    [Fact]
    public void Enter_OnTheNoGroupFolder_ResolvesIt()
    {
        FolderViewModel noGroup = new() { Name = "No group", FullPath = "" };

        Assert.Same(
            noGroup,
            MainWindow.ResolveTreeFolderActivation(Key.Enter, ModifierKeys.None, false, false, noGroup));
    }

    [Fact]
    public void Enter_OnAServer_ResolvesNoFolder()
    {
        ServerItemViewModel server = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = "alpha",
            DisplayName = "Alpha",
            RemoteServer = "alpha.example.test",
            ConnectionType = "SSH"
        });

        Assert.Null(MainWindow.ResolveTreeFolderActivation(Key.Enter, ModifierKeys.None, false, false, server));
        Assert.Null(MainWindow.ResolveTreeFolderActivation(Key.Enter, ModifierKeys.None, false, false, null));
    }

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Alt)]
    public void Enter_WithModifiers_ResolvesNoFolder(ModifierKeys modifiers)
    {
        FolderViewModel folder = new() { Name = "Production", FullPath = "Production" };

        Assert.Null(MainWindow.ResolveTreeFolderActivation(Key.Enter, modifiers, false, false, folder));
    }

    [Fact]
    public void Enter_WhileRenamingOrRepeating_ResolvesNoFolder()
    {
        // A held Enter would flap the branch open and closed thirty times a second, and an Enter
        // inside the rename editor belongs to the editor.
        FolderViewModel folder = new() { Name = "Production", FullPath = "Production" };

        Assert.Null(MainWindow.ResolveTreeFolderActivation(Key.Enter, ModifierKeys.None, isInlineRenameEditorSource: true, isRepeat: false, folder));
        Assert.Null(MainWindow.ResolveTreeFolderActivation(Key.Enter, ModifierKeys.None, isInlineRenameEditorSource: false, isRepeat: true, folder));
    }

    [Fact]
    public void OtherKeys_ResolveNoFolder()
    {
        FolderViewModel folder = new() { Name = "Production", FullPath = "Production" };

        Assert.Null(MainWindow.ResolveTreeFolderActivation(Key.Space, ModifierKeys.None, false, false, folder));
    }
}
