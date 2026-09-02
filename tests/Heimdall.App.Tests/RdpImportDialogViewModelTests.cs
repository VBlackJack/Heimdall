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
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class RdpImportDialogViewModelTests
{
    [Fact]
    public async Task Confirm_ReturnsSelectedEntriesAndResolutions()
    {
        var vm = await CreateViewModelAsync();
        vm.Rows[0].ConflictResolution = RdpConflictResolution.Replace;
        vm.Rows[1].IsSelected = false;

        vm.ConfirmCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Equal(3, vm.Result!.Entries.Count);
        Assert.Contains(vm.Result.Entries, entry => entry.ConflictResolution == RdpConflictResolution.Replace);
        Assert.Contains(
            vm.Result.Entries,
            entry => entry.SourceFilePath.EndsWith("b.rdp", StringComparison.OrdinalIgnoreCase) && !entry.IsSelected);
    }

    [Fact]
    public async Task Confirm_WhenNothingSelected_CannotExecute()
    {
        var vm = await CreateViewModelAsync();
        vm.SelectNoneCommand.Execute(null);

        Assert.False(vm.ConfirmCommand.CanExecute(null));
        vm.ConfirmCommand.Execute(null);
        Assert.Null(vm.Result);
    }

    [Fact]
    public async Task ApplyAllReplace_ChangesConflictRowsOnly()
    {
        var vm = await CreateViewModelAsync();

        vm.ApplyAllReplaceCommand.Execute(null);

        Assert.Equal(RdpConflictResolution.Replace, vm.Rows[0].ConflictResolution);
        Assert.Equal(RdpConflictResolution.Replace, vm.Rows[1].ConflictResolution);
        Assert.Equal(RdpConflictResolution.Skip, vm.Rows[2].ConflictResolution);
    }

    [Fact]
    public async Task ParseErrorRow_StartsDeselected()
    {
        var vm = await CreateViewModelAsync();

        Assert.False(vm.Rows[2].IsSelected);
    }

    [Fact]
    public async Task FileIssues_AreSurfaced()
    {
        var vm = await CreateViewModelAsync();

        Assert.True(vm.HasFileIssues);
        Assert.Contains("not found", vm.FileIssuesText!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectAll_LeavesParseErrorRowsDeselected()
    {
        var vm = await CreateViewModelAsync();
        vm.SelectAllCommand.Execute(null);

        Assert.True(vm.Rows[0].IsSelected);
        Assert.True(vm.Rows[1].IsSelected);
        Assert.False(vm.Rows[2].IsSelected);
    }

    [Fact]
    public async Task RowAccessibleSummary_IncludesSourceParseErrorAndConflict()
    {
        var localizer = await CreateLocalizerAsync();
        var row = new RdpImportRowViewModel(
            new RdpImportPreviewEntry
            {
                SourceFilePath = "C:\\broken.rdp",
                ProposedName = "Broken",
                Candidate = new ServerProfileDto
                {
                    DisplayName = "Broken",
                    RemoteServer = "",
                    RemotePort = 3389,
                    ConnectionType = "RDP"
                },
                HasParseError = true,
                ParseErrorMessage = "invalid host",
                HasNameConflict = true,
                ConflictingExistingName = "Broken"
            },
            localizer);

        Assert.Contains("broken.rdp", row.RowAccessibleSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invalid host", row.RowAccessibleSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(row.ConflictText, row.RowAccessibleSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyToAllButtons_KeepTheirOwnAutomationName()
    {
        // AutomationProperties.LabeledBy is consulted before a ButtonBase falls back to its Content,
        // so pointing three buttons at one shared label makes all three announce the same name.
        var document = XDocument.Load(RdpImportDialogPath());

        var offenders = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element =>
                !string.IsNullOrWhiteSpace(element.Attribute("Content")?.Value)
                && element.Attributes().Any(attribute =>
                    // The attached property is one XML name, dot included, so an equality test on
                    // "LabeledBy" alone never matches and would pass on the defect.
                    attribute.Name.LocalName.EndsWith("LabeledBy", StringComparison.Ordinal)))
            .Select(element => element.Attribute("Content")!.Value)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task RowAccessibleSummary_NamesTheGatewayTheImportWouldCommit()
    {
        var localizer = await CreateLocalizerAsync(GatewayLocaleOverride);
        var row = new RdpImportRowViewModel(
            new RdpImportPreviewEntry
            {
                SourceFilePath = "C:\\finance-vpn.rdp",
                ProposedName = "finance-vpn",
                Candidate = new ServerProfileDto
                {
                    DisplayName = "finance-vpn",
                    RemoteServer = "fileserver.corp.local",
                    RemotePort = 3389,
                    ConnectionType = "RDP",
                    RdpGateway = "gw.attacker.example"
                }
            },
            localizer);

        Assert.Contains("gw.attacker.example", row.RowAccessibleSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowAccessibleSummary_OmitsTheGateway_WhenTheFileCarriesNone()
    {
        var localizer = await CreateLocalizerAsync(GatewayLocaleOverride);
        var row = new RdpImportRowViewModel(
            new RdpImportPreviewEntry
            {
                SourceFilePath = "C:\\plain.rdp",
                ProposedName = "plain",
                Candidate = new ServerProfileDto
                {
                    DisplayName = "plain",
                    RemoteServer = "plain.example.com",
                    RemotePort = 3389,
                    ConnectionType = "RDP"
                }
            },
            localizer);

        // Positive control: the row above proves the summary can carry a gateway.
        Assert.Equal("plain.rdp", row.RowAccessibleSummary);
    }

    /// <summary>
    /// Resolves the dialog markup from the compiled-in source path, so the lookup survives a build
    /// whose output directory lives outside the repository.
    /// </summary>
    private static string RdpImportDialogPath([CallerFilePath] string testFilePath = "")
    {
        var dir = Path.GetDirectoryName(testFilePath);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                var path = Path.Combine(dir, "src", "Heimdall.App", "Views", "Dialogs", "RdpImportDialog.xaml");
                Assert.True(File.Exists(path), $"Dialog XAML not found: {path}");
                return path;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {testFilePath}");
    }

    private static async Task<RdpImportDialogViewModel> CreateViewModelAsync()
    {
        var localizer = await CreateLocalizerAsync();
        return new RdpImportDialogViewModel(localizer, new RdpImportPreview
        {
            Entries =
            [
                new RdpImportPreviewEntry
                {
                    SourceFilePath = "C:\\a.rdp",
                    ProposedName = "Alpha",
                    Candidate = new ServerProfileDto { DisplayName = "Alpha", RemoteServer = "a.example.com", RemotePort = 3389, ConnectionType = "RDP" },
                    HasNameConflict = true,
                    ConflictingExistingName = "Alpha"
                },
                new RdpImportPreviewEntry
                {
                    SourceFilePath = "C:\\b.rdp",
                    ProposedName = "Bravo",
                    Candidate = new ServerProfileDto { DisplayName = "Bravo", RemoteServer = "b.example.com", RemotePort = 3389, ConnectionType = "RDP" },
                    HasNameConflict = true,
                    ConflictingExistingName = "Bravo"
                },
                new RdpImportPreviewEntry
                {
                    SourceFilePath = "C:\\c.rdp",
                    ProposedName = "Charlie",
                    Candidate = new ServerProfileDto { DisplayName = "Charlie", RemoteServer = "", RemotePort = 3389, ConnectionType = "RDP" },
                    HasParseError = true,
                    ParseErrorMessage = "invalid"
                }
            ],
            FilesNotFound = ["missing.rdp"],
            FilesUnreadable = []
        });
    }

    /// <summary>
    /// Keys a fix introduces reach locales/*.json through the release pipeline. Supplying them from
    /// a private copy keeps the assertion on the view-model instead of on the merge state of the
    /// shared locale files.
    /// </summary>
    private static IReadOnlyDictionary<string, string> GatewayLocaleOverride { get; } =
        new Dictionary<string, string> { ["DialogImportRdpStatusGateway"] = "Gateway {0}" };

    private static async Task<LocalizationManager> CreateLocalizerAsync(
        IReadOnlyDictionary<string, string>? localeOverrides = null)
    {
        var manager = new LocalizationManager();
        var shippedLocalesPath = Path.Combine(AppContext.BaseDirectory, "locales");

        if (localeOverrides is null || localeOverrides.Count == 0)
        {
            await manager.LoadAsync(shippedLocalesPath, "en");
            return manager;
        }

        var localesPath = Path.Combine(
            Path.GetTempPath(),
            "heimdall-b56-locales",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localesPath);
        try
        {
            var shipped = JsonSerializer.Deserialize<Dictionary<string, string>>(
                await File.ReadAllTextAsync(Path.Combine(shippedLocalesPath, "en.json")))
                ?? [];

            foreach (var pair in localeOverrides)
            {
                shipped[pair.Key] = pair.Value;
            }

            await File.WriteAllTextAsync(
                Path.Combine(localesPath, "en.json"),
                JsonSerializer.Serialize(shipped));
            await manager.LoadAsync(localesPath, "en");
            return manager;
        }
        finally
        {
            Directory.Delete(localesPath, recursive: true);
        }
    }
}
