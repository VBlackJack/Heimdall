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
using Heimdall.App.Services.Import;
using Heimdall.App.Tests.Fakes;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Tools;
using Heimdall.App.Views.Tools;
using Heimdall.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// The tool tabs word their counts by their number in the shell's language: French takes the
/// singular for 0, English and Spanish for 1 only.
/// </summary>
public sealed class ToolCountWordingTests
{
    [Theory]
    [InlineData("en", "a", "1 match")]
    [InlineData("en", "a a", "2 matches")]
    [InlineData("fr", "a", "1 correspondance")]
    [InlineData("fr", "a a", "2 correspondances")]
    [InlineData("es", "a", "1 coincidencia")]
    [InlineData("es", "a a", "2 coincidencias")]
    public async Task RegexTester_WordsTheMatchCountByItsNumber(string locale, string text, string expected)
    {
        using var vm = new RegexTesterViewModel();
        vm.Initialize(await CreateLocalizerAsync(locale));
        vm.PatternText = "a";
        vm.TestText = text;

        vm.FlushPendingMatch();

        Assert.Equal(expected, vm.MatchCountText);
    }

    [Theory]
    [InlineData("en", 0, "0 notes")]
    [InlineData("en", 1, "1 note")]
    [InlineData("fr", 0, "0 note")]
    [InlineData("fr", 2, "2 notes")]
    [InlineData("es", 1, "1 nota")]
    [InlineData("es", 0, "0 notas")]
    public async Task NotesFooter_WordsTheNoteCountByItsNumber(string locale, int count, string expected)
    {
        FakeNoteSeed[] seeds = Enumerable.Range(0, count)
            .Select(i => new FakeNoteSeed($"note{i}.md", $"# Note {i}", null))
            .ToArray();
        var storage = new FakeNotesStorage(
            Path.Combine(Path.GetTempPath(), $"heimdall-notes-fake-{Guid.NewGuid():N}"),
            seeds);
        var vm = new NotesToolViewModel(storage, await CreateLocalizerAsync(locale), new FakeUiDispatcher());

        await vm.InitializeAsync();
        await vm.ReloadAsync();

        Assert.Equal(expected, vm.ListFooterText);
    }

    [Theory]
    [InlineData("en", 1, "1 note imported", "1 row", "1 more saved in another mode.", "1 subnet detected on gw")]
    [InlineData("en", 2, "2 notes imported", "2 rows", "2 more saved in other modes.", "2 subnets detected on gw")]
    [InlineData("fr", 0, "0 note importée", "0 ligne", "0 autre enregistré dans un autre mode.", "0 sous-réseau détecté sur gw")]
    [InlineData("fr", 2, "2 notes importées", "2 lignes", "2 autres enregistrés dans d'autres modes.", "2 sous-réseaux détectés sur gw")]
    [InlineData("es", 1, "1 nota importada", "1 fila", "1 más guardado en otro modo.", "1 subred detectada en gw")]
    [InlineData("es", 0, "0 notas importadas", "0 filas", "0 más guardados en otros modos.", "0 subredes detectadas en gw")]
    public async Task ToolViewLines_WordTheirCountsByTheirNumber(
        string locale,
        int count,
        string expectedImported,
        string expectedRows,
        string expectedSavedElsewhere,
        string expectedSubnets)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        Assert.Equal(expectedImported, NotesToolView.DescribeImported(localizer, count));
        Assert.Equal(expectedRows, ExternalToolWrapperView.DescribeRowCount(localizer, count));
        Assert.Equal(expectedSavedElsewhere, PasswordGeneratorView.DescribeSavedElsewhere(localizer, count));
        Assert.Equal(expectedSubnets, NetworkCartographyView.DescribeDetectedSubnets(localizer, count, "gw"));
    }

    [Theory]
    [InlineData("en", 1, "Scan complete: 1 host found.")]
    [InlineData("en", 2, "Scan complete: 2 hosts found.")]
    [InlineData("fr", 1, "Scan terminé : 1 hôte trouvé.")]
    [InlineData("fr", 2, "Scan terminé : 2 hôtes trouvés.")]
    [InlineData("es", 1, "Escaneo completo: 1 host encontrado.")]
    [InlineData("es", 2, "Escaneo completo: 2 hosts encontrados.")]
    public async Task NetworkScanner_WordsTheHostCountByItsNumber(string locale, int count, string expected)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        Assert.Equal(expected, NetworkScannerService.DescribeCompletion(localizer.GetString, localizer, count));
    }

    [Theory]
    [InlineData("en", 1, 0, 2, "1 imported, 0 updated, 2 skipped.")]
    [InlineData("fr", 1, 0, 2, "1 importée, 0 mise à jour, 2 ignorées.")]
    [InlineData("fr", 2, 1, 0, "2 importées, 1 mise à jour, 0 ignorée.")]
    [InlineData("es", 1, 0, 2, "1 importado, 0 actualizados, 2 omitidos.")]
    [InlineData("es", 2, 1, 1, "2 importados, 1 actualizado, 1 omitido.")]
    public async Task CommandLibraryImport_WordsEachCountByItsNumber(
        string locale,
        int imported,
        int updated,
        int skipped,
        string expected)
    {
        var dialogs = new SilentDialogService();
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService(Array.Empty<ActionModel>()));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();
        using var vm = new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(locale),
            dialogs,
            gitSyncService: null!,
            new FixedTransferService(CommandLibraryImportResult.Success(imported, updated, skipped)));
        vm.ShowOpenFileDialog = _ => "library.json";

        await vm.ImportAsync();

        Assert.Equal(expected, Assert.Single(dialogs.InfoMessages));
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return localizer;
    }

    private sealed class FixedTransferService(CommandLibraryImportResult result) : ICommandLibraryTransferService
    {
        public Task<CommandLibraryImportResult> ImportAsync(IActionService actionService, string path) =>
            Task.FromResult(result);

        public Task<int> ExportAsync(IActionService actionService, string path) => Task.FromResult(0);
    }
}
