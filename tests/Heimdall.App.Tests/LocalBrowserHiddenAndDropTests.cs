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
using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using DataObject = System.Windows.DataObject;

namespace Heimdall.App.Tests;

/// <summary>
/// The local list of an SFTP tab: the hidden files toggle, and files dropped from Explorer.
/// </summary>
public sealed class LocalBrowserHiddenAndDropTests
{
    private const string ThisList = "list-a";
    private const string OtherList = "list-b";

    [Fact]
    public async Task HiddenFilesAreShownUntilTheToggleHidesThem()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        File.WriteAllText(Path.Combine(scratch.Path, "visible.txt"), "x");
        File.WriteAllText(Path.Combine(scratch.Path, ".env"), "x");
        string hidden = Path.Combine(scratch.Path, "desktop.ini");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        LocalFileBrowserViewModel viewModel = new(scratch.Path);
        await viewModel.LoadDirectory(scratch.Path);

        // Shown by default, as before the toggle existed.
        Assert.True(viewModel.ShowHidden);
        Assert.Equal(3, viewModel.Files.Count);

        viewModel.ShowHidden = false;
        Assert.Equal(["visible.txt"], viewModel.Files.Select(entry => entry.Name));

        viewModel.ShowHidden = true;
        Assert.Equal(3, viewModel.Files.Count);
    }

    [Fact]
    public async Task TheFilterAppliesToWhatTheToggleLeaves()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        File.WriteAllText(Path.Combine(scratch.Path, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(scratch.Path, ".notes.swp"), "x");
        LocalFileBrowserViewModel viewModel = new(scratch.Path) { ShowHidden = false };
        await viewModel.LoadDirectory(scratch.Path);

        viewModel.FilterText = "notes";

        Assert.Equal(["notes.txt"], viewModel.Files.Select(entry => entry.Name));
    }

    [Fact]
    public void ADropOnAFolderRowLandsInThatFolder()
    {
        Assert.Equal(@"C:\work\sub", LocalFileBrowserViewModel.PasteTarget(@"C:\work", @"C:\work\sub"));
        Assert.Equal(@"C:\work", LocalFileBrowserViewModel.PasteTarget(@"C:\work", null));
        Assert.Equal(@"C:\work", LocalFileBrowserViewModel.PasteTarget(@"C:\work", " "));
    }

    [Fact]
    public void FilesFromExplorerAreCopied()
    {
        Assert.Equal(LocalBrowserDropKind.LocalCopy, LocalBrowserDrop.Classify(FileDrop(origin: null), ThisList));
    }

    [Fact]
    public void FilesFromAnotherLocalListAreCopied()
    {
        Assert.Equal(LocalBrowserDropKind.LocalCopy, LocalBrowserDrop.Classify(FileDrop(origin: OtherList), ThisList));
    }

    /// <summary>
    /// The list drags its own selection out as real files. Released over itself, a short drag
    /// would otherwise copy the files onto themselves or into the folder under the pointer.
    /// </summary>
    [Fact]
    public void ADragThatStartedInThisListIsRefused()
    {
        Assert.Equal(LocalBrowserDropKind.None, LocalBrowserDrop.Classify(FileDrop(origin: ThisList), ThisList));
    }

    [Fact]
    public void NothingDroppableIsRefused()
    {
        DataObject text = new();
        text.SetData(DataFormats.UnicodeText, "not a file");
        DataObject empty = new();
        empty.SetData(DataFormats.FileDrop, Array.Empty<string>());

        Assert.Equal(LocalBrowserDropKind.None, LocalBrowserDrop.Classify(text, ThisList));
        Assert.Equal(LocalBrowserDropKind.None, LocalBrowserDrop.Classify(empty, ThisList));
        Assert.Equal(LocalBrowserDropKind.None, LocalBrowserDrop.Classify(null, ThisList));
    }

    private static DataObject FileDrop(string? origin)
    {
        DataObject data = new();
        data.SetData(DataFormats.FileDrop, new[] { @"C:\work\report.txt" });
        if (origin is not null)
        {
            data.SetData(LocalBrowserDrop.OriginFormat, origin);
        }

        return data;
    }
}
