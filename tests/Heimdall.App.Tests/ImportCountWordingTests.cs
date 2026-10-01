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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

/// <summary>
/// The import summaries word every count by its own number. They report several counts in one
/// sentence, so each count is a fragment of its own, where the catalogues used to append "(s)"
/// to every participle.
/// </summary>
public sealed class ImportCountWordingTests
{
    [Theory]
    [InlineData("en", "1 imported, 0 replaced, 2 auto-renamed, 1 skipped.")]
    [InlineData("fr", "1 import\u00e9, 0 remplac\u00e9, 2 renomm\u00e9s auto, 1 ignor\u00e9.")]
    [InlineData("es", "1 importado, 0 reemplazados, 2 renombrados autom\u00e1ticamente, 1 omitido.")]
    public async Task ProfileImportSummary_WordsEachCountByItsNumber(string locale, string expected)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        Assert.Equal(expected, ImportSummaryText.Profiles(localizer, 1, 0, 2, 1));
    }

    [Theory]
    [InlineData("en", 2, "1 imported, 0 replaced, 0 auto-renamed, 0 skipped, 2 password blobs ignored.")]
    [InlineData("fr", 2, "1 import\u00e9, 0 remplac\u00e9, 0 renomm\u00e9 auto, 0 ignor\u00e9, 2 blobs mot de passe ignor\u00e9s.")]
    [InlineData("es", 1, "1 importado, 0 reemplazados, 0 renombrados autom\u00e1ticamente, 0 omitidos, 1 contrase\u00f1a ignorada.")]
    public async Task RdpImportSummary_WordsEachCountByItsNumber(string locale, int passwordsIgnored, string expected)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        Assert.Equal(expected, ImportSummaryText.RdpFiles(localizer, 1, 0, 0, 0, passwordsIgnored));
    }

    [Theory]
    [InlineData("en", 1, 2, 1, "SSH gateways: 1 created, 2 merged, 1 orphan reference.")]
    [InlineData("fr", 1, 2, 1, "Passerelles SSH : 1 cr\u00e9\u00e9e, 2 fusionn\u00e9es, 1 r\u00e9f\u00e9rence orpheline.")]
    [InlineData("fr", 0, 0, 2, "Passerelles SSH : 0 cr\u00e9\u00e9e, 0 fusionn\u00e9e, 2 r\u00e9f\u00e9rences orphelines.")]
    [InlineData("es", 1, 2, 1, "Pasarelas SSH: 1 creada, 2 fusionadas, 1 referencia hu\u00e9rfana.")]
    public async Task GatewayImportSummary_WordsEachCountByItsNumber(
        string locale,
        int created,
        int merged,
        int orphans,
        string expected)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        Assert.Equal(expected, ImportSummaryText.Gateways(localizer, created, merged, orphans));
    }

    [Theory]
    [InlineData("en", "1 imported, 2 skipped (duplicates), 0 warnings", "2 imported, 1 skipped (duplicate), 0 invalid, 1 warning")]
    [InlineData("fr", "1 import\u00e9, 2 ignor\u00e9s (doublons), 0 avertissement", "2 import\u00e9s, 1 ignor\u00e9 (doublon), 0 invalide, 1 avertissement")]
    [InlineData("es", "1 importado, 2 omitidos (duplicados), 0 avisos", "2 importados, 1 omitido (duplicado), 0 no v\u00e1lidos, 1 aviso")]
    public async Task SessionImportResults_WordEachCountByItsNumber(string locale, string expectedOpenSsh, string expectedPutty)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        Assert.Equal(expectedOpenSsh, ImportSummaryText.OpenSsh(localizer, 1, 2, 0));
        Assert.Equal(expectedPutty, ImportSummaryText.Putty(localizer, 2, 1, 0, 1));
    }

    [Theory]
    [InlineData("en", "1 imported, 0 skipped (already trusted), 2 skipped (conflict), 1 warning")]
    [InlineData("fr", "1 import\u00e9e, 0 ignor\u00e9e (d\u00e9j\u00e0 approuv\u00e9e), 2 ignor\u00e9es (conflit), 1 avertissement")]
    [InlineData("es", "1 importada, 0 omitidas (ya de confianza), 2 omitidas (conflicto), 1 aviso")]
    public async Task KnownHostsImportResult_WordsEachCountByItsNumber(string locale, string expected)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        Assert.Equal(expected, ImportSummaryText.KnownHosts(localizer, 1, 0, 2, 1));
    }

    [Theory]
    [InlineData("en", "Migration completed with skipped profiles: 2 examined, 1 imported, 1 skipped.")]
    [InlineData("fr", "Migration termin\u00e9e avec des profils ignor\u00e9s : 2 examin\u00e9s, 1 import\u00e9, 1 ignor\u00e9.")]
    [InlineData("es", "Migraci\u00f3n completada con perfiles omitidos: 2 examinados, 1 importado, 1 omitido.")]
    public async Task MigrationPartialSummary_WordsEachCountByItsNumber(string locale, string expected)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);
        MigrationResult result = new()
        {
            Success = true,
            ServersExamined = 2,
            ServersImported = 1,
            Warnings = [new MigrationWarning(2, "Rejected", MigrationWarningReason.InvalidLegacyField)],
        };

        MigrationPresentation presentation = MigrationPresentationPolicy.Create(result, localizer);

        Assert.StartsWith(expected, presentation.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", 1, "1 file ready to import.", "1 file selected out of 1, 0 conflicts, 0 password warnings.")]
    [InlineData("fr", 1, "1 fichier pr\u00eat \u00e0 l'import.", "1 fichier s\u00e9lectionn\u00e9 sur 1, 0 conflit, 0 avertissement mot de passe.")]
    [InlineData("es", 2, "2 archivos listos para importar.", "2 archivos seleccionados de 2, 0 conflictos, 0 avisos de contrase\u00f1a.")]
    public async Task RdpImportDialog_WordsItsCounts(string locale, int entries, string expectedSubtitle, string expectedSummary)
    {
        var viewModel = new RdpImportDialogViewModel(await CreateLocalizerAsync(locale), Preview(entries));

        Assert.Equal(expectedSubtitle, viewModel.SubtitleText);
        Assert.Equal(expectedSummary, viewModel.SummaryText);
    }

    [Theory]
    [InlineData("en", 1, "1 profile ready to import.", "1 profile selected out of 1, 0 conflicts.")]
    [InlineData("en", 2, "2 profiles ready to import.", "2 profiles selected out of 2, 0 conflicts.")]
    [InlineData("fr", 1, "1 profil pr\u00eat \u00e0 l'import.", "1 profil s\u00e9lectionn\u00e9 sur 1, 0 conflit.")]
    [InlineData("es", 1, "1 perfil listo para importar.", "1 perfil seleccionado de 1, 0 conflictos.")]
    public async Task ProfileImportDialog_WordsItsCounts(string locale, int entries, string expectedSubtitle, string expectedSummary)
    {
        var viewModel = new RdpImportDialogViewModel(
            await CreateLocalizerAsync(locale),
            Preview(entries),
            RdpImportDialogTextOptions.ProfileImport);

        Assert.Equal(expectedSubtitle, viewModel.SubtitleText);
        Assert.Equal(expectedSummary, viewModel.SummaryText);
    }

    [Theory]
    [InlineData("en", "3 candidates - 2 new, 0 duplicates, 1 invalid", "1 candidate - 1 new, 0 duplicates")]
    [InlineData("fr", "3 candidats - 2 nouveaux, 0 doublon, 1 invalide", "1 candidat - 1 nouveau, 0 doublon")]
    [InlineData("es", "3 candidatos - 2 nuevos, 0 duplicados, 1 no v\u00e1lido", "1 candidato - 1 nuevo, 0 duplicados")]
    public async Task SessionImportPreview_WordsEachCountByItsNumber(string locale, string expectedWithInvalid, string expectedWithout)
    {
        using var fixture = await PuttyFixture.CreateAsync(locale);
        await fixture.ViewModel.InitializeAsync(new PuttySessionParseResult(
            [
                new PuttySessionCandidate { DisplayName = "Prod", HostName = "prod.example.com" },
                new PuttySessionCandidate { DisplayName = "Broken", HostName = null },
                new PuttySessionCandidate { DisplayName = "Fresh", HostName = "fresh.example.com" },
            ],
            []));
        Assert.Equal(expectedWithInvalid, fixture.ViewModel.SummaryText);

        await fixture.ViewModel.InitializeAsync(new PuttySessionParseResult(
            [new PuttySessionCandidate { DisplayName = "Fresh", HostName = "fresh.example.com" }],
            []));
        Assert.Equal(expectedWithout, fixture.ViewModel.SummaryText);
    }

    [Theory]
    [InlineData("en", "1", "Session 'S' defines 1 tunnel that was captured but not mapped")]
    [InlineData("en", "2", "Session 'S' defines 2 tunnels that were captured but not mapped")]
    [InlineData("fr", "1", "La session 'S' d\u00e9finit 1 tunnel captur\u00e9 mais non mapp\u00e9")]
    [InlineData("fr", "2", "La session 'S' d\u00e9finit 2 tunnels captur\u00e9s mais non mapp\u00e9s")]
    [InlineData("es", "1", "La sesi\u00f3n \"S\" define 1 t\u00fanel que se captur\u00f3 pero no se asign\u00f3")]
    public async Task PuttyForwardingDiagnostic_WordsTheTunnelCountByItsNumber(string locale, string count, string expected)
    {
        using var fixture = await PuttyFixture.CreateAsync(locale);

        await fixture.ViewModel.InitializeAsync(new PuttySessionParseResult(
            [new PuttySessionCandidate { DisplayName = "S", HostName = "s.example.com" }],
            [new PuttySessionDiagnostic(PuttyDiagnosticLevel.Info, PuttyDiagnosticCode.PortForwardingsCapturedButNotMapped, "S", count)]));

        Assert.Equal(expected, Assert.Single(fixture.ViewModel.Diagnostics).Message);
    }

    [Theory]
    [InlineData("en", false, "1 entry: 1 new, 0 already trusted, 0 conflicting")]
    [InlineData("en", true, "2 entries: 1 new, 0 already trusted, 1 conflicting")]
    [InlineData("fr", false, "1 entr\u00e9e : 1 nouvelle, 0 d\u00e9j\u00e0 approuv\u00e9e, 0 en conflit")]
    [InlineData("fr", true, "2 entr\u00e9es : 1 nouvelle, 0 d\u00e9j\u00e0 approuv\u00e9e, 1 en conflit")]
    [InlineData("es", true, "2 entradas: 1 nueva, 0 ya de confianza, 1 en conflicto")]
    public async Task KnownHostsPreview_WordsEachCountByItsNumber(string locale, bool withConflict, string expected)
    {
        var viewModel = new ImportKnownHostsDialogViewModel(
            new KnownHostsImporter(new InMemoryConfigManager(), new HostKeyStore()),
            await CreateLocalizerAsync(locale));
        List<KnownHostsPreviewRow> rows =
        [
            new(Candidate("new", "SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"), KnownHostsCandidateStatus.New, null),
        ];
        if (withConflict)
        {
            rows.Add(new(
                Candidate("conflict", "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                KnownHostsCandidateStatus.Conflict,
                null));
        }

        await viewModel.InitializeAsync(new KnownHostsImportPreview(rows, []));

        Assert.Equal(expected, viewModel.SummaryText);
    }

    private static KnownHostsImportCandidate Candidate(string host, string fingerprint) =>
        new() { Host = host, Port = 22, Fingerprint = fingerprint, SourceLineNumber = 1 };

    private static RdpImportPreview Preview(int entries) => new()
    {
        Entries = Enumerable.Range(0, entries)
            .Select(i => new RdpImportPreviewEntry
            {
                SourceFilePath = $"C:\\{i}.rdp",
                ProposedName = $"Server{i}",
                Candidate = new ServerProfileDto
                {
                    DisplayName = $"Server{i}",
                    RemoteServer = $"s{i}.example.com",
                    RemotePort = 3389,
                    ConnectionType = "RDP",
                },
            })
            .ToList(),
        FilesNotFound = [],
        FilesUnreadable = [],
    };

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return localizer;
    }

    private sealed class PuttyFixture : IDisposable
    {
        private readonly string _rootPath;

        private PuttyFixture(string rootPath, ImportPuttySessionsDialogViewModel viewModel)
        {
            _rootPath = rootPath;
            ViewModel = viewModel;
        }

        public ImportPuttySessionsDialogViewModel ViewModel { get; }

        public static async Task<PuttyFixture> CreateAsync(string locale)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "heimdall-count-wording-putty", Guid.NewGuid().ToString("N"));
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();
            var importer = new PuttySessionImporter(new FakePuttySessionRegistrySource([]), configManager);
            var viewModel = new ImportPuttySessionsDialogViewModel(await CreateLocalizerAsync(locale), importer);
            return new PuttyFixture(rootPath, viewModel);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
