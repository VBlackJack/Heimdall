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
using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using static Heimdall.App.Services.SharedSessions.SharedSessionImportReportBuilder;

namespace Heimdall.App.Tests;

/// <summary>
/// The import report of a shared source: each check with the case it must find and the case it
/// must leave alone, the report a real file gives, its rebuild on reload, and the dialog that shows
/// it. Fake hosts only.
/// </summary>
public sealed class SharedSessionImportReportTests : IDisposable
{
    private const string SourceId = "reportsrc1";
    private const string SourceName = "Team";
    private const string Gw1 = "gw1-id";
    private const string Gw2 = "gw2-id";

    // One case of each check, anonymized: app02 is named after one machine and reaches app03 (name
    // and host), which app03 also reaches (duplicate host); app04 alone takes gw2 (outlier); app05
    // logs in as [NOPE] (unresolved variable); wsl01 is a WSL distribution (skipped: nothing to connect to).
    private const string OneOfEach = """
        [Bookmarks]
        SubRep=
        ImgNum=42
        [Bookmarks_1]
        SubRep=PROD\Linux
        ImgNum=41
        app01=#109#0%app01.example.test%22%[DAS]%%0%-1%%gw1.example.test%22%jdoe%-1#MobaFont%10#0#
        app02=#109#0%app03.example.test%22%[DAS]%%0%-1%%gw1.example.test%22%jdoe%-1#MobaFont%10#0#
        app03=#109#0%app03.example.test%22%[DAS]%%0%-1%%gw1.example.test%22%jdoe%-1#MobaFont%10#0#
        app04=#109#0%app04.example.test%22%[DAS]%%0%-1%%gw2.example.test%22%jdoe%-1#MobaFont%10#0#
        app05=#109#0%app05.example.test%22%[NOPE]%%0%-1%%gw1.example.test%22%jdoe%-1#MobaFont%10#0#
        wsl01=#151#14%AlmaLinux-9%
        """;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "heimdall-shared-report-tests",
        Guid.NewGuid().ToString("N"));

    public SharedSessionImportReportTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // --- Name and host ---

    [Theory]
    [InlineData("app02", "app03.example.test", false)]
    [InlineData("web-front (primary)", "srv-web01", false)]
    [InlineData("app03", "app03.example.test", true)]
    [InlineData("APP03", "app03.Example.Test", true)]
    [InlineData("app03.example.test", "app03.example.test", true)]
    [InlineData("App03.Example.TEST", "app03.example.test", true)]
    [InlineData("app03", "app03", true)]
    [InlineData("router", "10.0.0.5", null)]
    [InlineData("router", "fe80::1", null)]
    [InlineData("anything", "", null)]
    public void NameMatchesHost_TheHostOrItsFirstLabel_InAnyCase(string name, string host, bool? expected)
    {
        Assert.Equal(expected, NameMatchesHost(name, host));
    }

    // --- Gateway outlier ---

    [Fact]
    public void GatewayOutlier_ListsTheSessionsLeavingTheFolderMajority()
    {
        SharedSessionImportReport report = BuildReport(
        [
            Session("a1", "PROD", Gw1), Session("a2", "PROD", Gw1), Session("a3", "PROD", Gw1), Session("a4", "PROD", Gw2),
        ]);

        SharedSessionReportEntry outlier = Assert.Single(report.Entries, entry => entry.Check == SharedSessionReportCheck.GatewayOutlier);
        Assert.Equal("a4", outlier.SessionName);
        Assert.Equal("gw2", outlier.Detail);
        Assert.Equal("gw1", outlier.Context);
        Assert.Equal("PROD", outlier.FolderPath);
    }

    [Fact]
    public void GatewayOutlier_ADirectSessionInAGatewayFolder_IsListed()
    {
        SharedSessionImportReport report = BuildReport(
            [Session("a1", "PROD", Gw1), Session("a2", "PROD", Gw1), Session("a3", "PROD", null)]);

        SharedSessionReportEntry outlier = Assert.Single(report.Entries, entry => entry.Check == SharedSessionReportCheck.GatewayOutlier);
        Assert.Equal("a3", outlier.SessionName);
        Assert.Equal(string.Empty, outlier.Detail);
    }

    [Fact]
    public void GatewayOutlier_AFolderWithoutAClearMajority_HasNone()
    {
        SharedSessionImportReport split = BuildReport(
            [Session("a1", "PROD", Gw1), Session("a2", "PROD", Gw1), Session("a3", "PROD", Gw2), Session("a4", "PROD", Gw2)]);
        SharedSessionImportReport plurality = BuildReport(
            [Session("b1", "LAB", Gw1), Session("b2", "LAB", Gw1), Session("b3", "LAB", Gw2), Session("b4", "LAB", null), Session("b5", "LAB", Gw2)]);

        Assert.Equal(0, split.CountOf(SharedSessionReportCheck.GatewayOutlier));
        Assert.Equal(0, plurality.CountOf(SharedSessionReportCheck.GatewayOutlier));
    }

    [Fact]
    public void GatewayOutlier_FollowsTheConfiguredThresholds()
    {
        List<ServerProfileDto> threeOfFour =
            [Session("a1", "PROD", Gw1), Session("a2", "PROD", Gw1), Session("a3", "PROD", Gw1), Session("a4", "PROD", Gw2)];
        List<ServerProfileDto> small = [Session("c1", "OPS", Gw1), Session("c2", "OPS", Gw2)];

        Assert.Equal(0, BuildReport(threeOfFour, new Options(80, 3)).CountOf(SharedSessionReportCheck.GatewayOutlier));
        Assert.Equal(1, BuildReport(threeOfFour, new Options(75, 3)).CountOf(SharedSessionReportCheck.GatewayOutlier));
        Assert.Equal(0, BuildReport(threeOfFour, new Options(60, 5)).CountOf(SharedSessionReportCheck.GatewayOutlier));
        Assert.Equal(0, BuildReport(small).CountOf(SharedSessionReportCheck.GatewayOutlier));
    }

    [Fact]
    public void Options_ComeFromTheSettings_Clamped()
    {
        Options options = Options.FromSettings(new AppSettings
        {
            SharedReportGatewayMajorityPercent = 10,
            SharedReportGatewayMinFolderSessions = 7,
        });

        Assert.Equal(51, options.GatewayMajorityPercent);
        Assert.Equal(7, options.GatewayMinFolderSessions);
        Assert.Equal(new Options(60, 3), Options.FromSettings(new AppSettings()));
    }

    // --- Duplicate host ---

    [Fact]
    public void DuplicateHost_SameHostInTheSameFolder_InAnyCase_ListsEach()
    {
        SharedSessionImportReport report = BuildReport(
        [
            Session("db01", "PROD", Gw1, host: "db01.example.test"),
            Session("db01-bis", "PROD", Gw1, host: "DB01.example.test"),
            Session("db01", "LAB", Gw1, host: "db01.example.test"),
        ]);

        List<SharedSessionReportEntry> duplicates = [.. report.Entries.Where(entry => entry.Check == SharedSessionReportCheck.DuplicateHost)];
        Assert.Equal(2, duplicates.Count);
        Assert.All(duplicates, entry => Assert.Equal("PROD", entry.FolderPath));
        Assert.Equal("db01-bis", duplicates.Single(entry => entry.SessionName == "db01").Context);
    }

    [Fact]
    public void DuplicateHost_DistinctHostsOrDistinctFolders_HaveNone()
    {
        SharedSessionImportReport report = BuildReport(
        [
            Session("db01", "PROD", Gw1, host: "db01.example.test"),
            Session("db02", "PROD", Gw1, host: "db02.example.test"),
            Session("db01", "LAB", Gw1, host: "db01.example.test"),
        ]);

        Assert.Equal(0, report.CountOf(SharedSessionReportCheck.DuplicateHost));
    }

    // Producer: SharedSessionImportReportBuilder.DuplicateHosts, which now keys by host AND
    // protocol. An SSH and an SFTP session to one host are two ways to use it, not a repetition;
    // two sessions of the same protocol still are.
    [Fact]
    public void DuplicateHost_SshAndSftpToOneHost_AreNotDuplicates_TwoOfTheSameProtocolStillAre()
    {
        SharedSessionImportReport mixed = BuildReport(
        [
            Session("db01", "PROD", Gw1, host: "db01.example.test"),
            Session("db01-files", "PROD", Gw1, host: "db01.example.test", connectionType: "SFTP"),
        ]);
        SharedSessionImportReport same = BuildReport(
        [
            Session("db02", "PROD", Gw1, host: "db02.example.test", connectionType: "SFTP"),
            Session("db02-bis", "PROD", Gw1, host: "DB02.example.test", connectionType: "sftp"),
        ]);

        Assert.Equal(0, mixed.CountOf(SharedSessionReportCheck.DuplicateHost));
        Assert.Equal(2, same.CountOf(SharedSessionReportCheck.DuplicateHost));
    }

    // Producer: SharedSessionImportReportBuilder.GatewayOutliers, which counts only sessions whose
    // protocol supports a gateway (ProtocolCapabilities.SupportsSshGateway). A Telnet or VNC
    // session is direct whatever its folder does: it is never an outlier and it casts no vote.
    [Fact]
    public void GatewayOutlier_DirectTelnetAndVncSessions_AreNeverOutliers()
    {
        SharedSessionImportReport report = BuildReport(
        [
            Session("a1", "PROD", Gw1), Session("a2", "PROD", Gw1), Session("a3", "PROD", Gw1),
            Session("t1", "PROD", null, connectionType: "Telnet"),
            Session("v1", "PROD", null, connectionType: "VNC"),
        ]);

        Assert.Equal(0, report.CountOf(SharedSessionReportCheck.GatewayOutlier));
    }

    [Fact]
    public void GatewayOutlier_DirectTelnetSessions_DoNotChangeTheMajority()
    {
        // Three Telnet sessions would make "direct" the biggest route of the folder (3 of 6) and
        // hide the direct SSH session; they cast no vote, so it is still the one outlier.
        SharedSessionImportReport report = BuildReport(
        [
            Session("a1", "PROD", Gw1), Session("a2", "PROD", Gw1), Session("a3", "PROD", null),
            Session("t1", "PROD", null, connectionType: "Telnet"),
            Session("t2", "PROD", null, connectionType: "Telnet"),
            Session("t3", "PROD", null, connectionType: "Telnet"),
        ]);

        SharedSessionReportEntry outlier = Assert.Single(report.Entries, entry => entry.Check == SharedSessionReportCheck.GatewayOutlier);
        Assert.Equal("a3", outlier.SessionName);
        Assert.Equal("gw1", outlier.Context);
    }

    // --- Parser warnings, refused sessions ---

    [Fact]
    public void Warnings_AreFiledUnderTheFolderOfTheirSection_AndRefusedSessionsAreSkipped()
    {
        const string content = "[Bookmarks_3]\r\nSubRep=Old\\PROD\r\nImgNum=41\r\n";
        MobaXtermImportWarning[] warnings =
        [
            new("Bookmarks_3", "wsl01", MobaXtermImportWarningKind.UnsupportedSessionType, "14"),
            new("Bookmarks_3", "app05", MobaXtermImportWarningKind.UnresolvedVariable, "NOPE"),
            new("Bookmarks_9", "lost", MobaXtermImportWarningKind.MissingHost, ""),
        ];
        ServerProfileDto refused = Session("web01", "Old/PROD", null, host: "web01.example.test");

        SharedSessionImportReport report = Build(
            SourceId,
            SourceName,
            content,
            warnings,
            [],
            [],
            [new SharedRefusedSession(refused, SharedProfileRefusal.UnsupportedProtocol, "WEB")],
            new Options(60, 3));

        Assert.Equal(3, report.CountOf(SharedSessionReportCheck.SkippedSession));
        Assert.Equal(1, report.CountOf(SharedSessionReportCheck.UnresolvedVariable));
        Assert.Equal("Old/PROD", report.Entries.Single(entry => entry.SessionName == "wsl01").FolderPath);
        Assert.Equal("Old/PROD", report.Entries.Single(entry => entry.SessionName == "app05").FolderPath);
        Assert.Equal(string.Empty, report.Entries.Single(entry => entry.SessionName == "lost").FolderPath);
        SharedSessionReportEntry web = report.Entries.Single(entry => entry.SessionName == "web01");
        Assert.Null(web.SkipReason);
        Assert.Equal(SharedProfileRefusal.UnsupportedProtocol, web.Refusal);
        Assert.Equal("WEB", web.Detail);
        Assert.Equal(MobaXtermImportWarningKind.UnsupportedSessionType, report.Entries.Single(entry => entry.SessionName == "wsl01").SkipReason);
    }

    [Fact]
    public async Task Explain_NamesWhyTheSharedFilterRefusedASession_ForEveryReason()
    {
        LocalizationManager localizer = await LocalizerAsync();
        var formatter = new SharedSessionImportReportFormatter(localizer);
        Dictionary<SharedProfileRefusal, string> expected = new()
        {
            [SharedProfileRefusal.ForeignId] = localizer["SharedReportRefusedForeignId"],
            [SharedProfileRefusal.UnsupportedProtocol] = localizer.Format("SharedReportRefusedProtocol", "value"),
            [SharedProfileRefusal.InvalidHost] = localizer.Format("SharedReportRefusedHost", "value"),
            [SharedProfileRefusal.ForeignGateway] = localizer.Format("SharedReportRefusedForeignGateway", "value"),
            [SharedProfileRefusal.UnknownGateway] = localizer.Format("SharedReportRefusedUnknownGateway", "value"),
        };

        Assert.Equal(Enum.GetValues<SharedProfileRefusal>().Order(), expected.Keys.Order());
        foreach ((SharedProfileRefusal refusal, string text) in expected)
        {
            var entry = new SharedSessionReportEntry(SharedSessionReportCheck.SkippedSession, "PROD", "srv01", "value", Refusal: refusal);
            Assert.Equal(text, formatter.Explain(entry));
            Assert.NotEqual(localizer["SharedReportSkipRefused"], text);
        }

        Assert.Equal(expected.Count, expected.Values.Distinct(StringComparer.Ordinal).Count());
    }

    // Producer: SharedSessionImportReportFormatter.ExplainSkip and ExplainUnsupportedType; the
    // reason is worded from MobaXtermImporter.ClassifySkippedType, shared with the Settings importer.
    [Fact]
    public async Task Explain_WordsTheKnownSkippedTypes_TheJumpHostAndTheProxy()
    {
        LocalizationManager localizer = await LocalizerAsync();
        var formatter = new SharedSessionImportReportFormatter(localizer);

        string Explain(MobaXtermImportWarningKind kind, string detail) => formatter.Explain(new SharedSessionReportEntry(
            SharedSessionReportCheck.SkippedSession, "PROD", "srv01", detail, SkipReason: kind));

        string wsl = Explain(MobaXtermImportWarningKind.UnsupportedSessionType, "14");
        string ftp = Explain(MobaXtermImportWarningKind.UnsupportedSessionType, "6");
        string unknown = Explain(MobaXtermImportWarningKind.UnsupportedSessionType, "999");
        string jump = Explain(MobaXtermImportWarningKind.JumpHostNotSupported, "jump.example.test");
        string proxy = Explain(MobaXtermImportWarningKind.ProxyNotSupported, "proxy.example.test");

        Assert.Equal(localizer["SharedReportSkipWslType"], wsl);
        Assert.Equal(localizer["SharedReportSkipFtpType"], ftp);
        Assert.Equal(localizer.Format("SharedReportSkipUnsupportedType", "999"), unknown);
        Assert.Contains("999", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("SSH and RDP", unknown, StringComparison.Ordinal);
        Assert.Equal(localizer.Format("SharedReportSkipJumpHost", "jump.example.test"), jump);
        Assert.Contains("jump.example.test", jump, StringComparison.Ordinal);
        Assert.Equal(localizer.Format("SharedReportSkipProxy", "proxy.example.test"), proxy);
        Assert.Contains("proxy.example.test", proxy, StringComparison.Ordinal);
        Assert.Equal(5, new[] { wsl, ftp, unknown, jump, proxy }.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("SSH and RDP", localizer["SharedReportRefusedProtocol"], StringComparison.Ordinal);
    }

    // --- A real file through the provider ---

    // Producer: SharedSessionProvider.LoadAsync over MobaXtermImporter, a file that mixes mapped
    // and refused sessions (jump host, proxy, WSL, FTP): what the report lists is what was skipped.
    [Fact]
    public async Task Load_TelnetVncSftpSessions_AreLoaded_AndTheRefusedOnesReportedWithTheirReason()
    {
        const string content = """
            [Bookmarks_1]
            SubRep=PROD
            ImgNum=41
            telnet-a= #98#1%telnet-a.example.test%2323%telusera%%2%%%%%0%0%%1080%%%0%-1%0#MobaFont%10#0#
            telnet-gw= #98#1%telnet-gw.example.test%23%telusergw%%1%jumpt.example.test%2299%jumpusert%%0%1%proxyt.example.test%3128%proxyusert%%0#MobaFont%10#0#
            sftp-px= #140#7%sftp-px.example.test%22%sftpuserpx%-1%0%%0%0%%1%proxys.example.test%3130%proxyusers%%%0#MobaFont%10#0#
            wsl= #151#14%AlmaLinux-9%
            ftp-a= #130#6%ftp-a.example.test%2121%ftpusera%-1%/srv/ftpa%0%-1%0%0%%21%%%0%0%-1%0%-1%0%%0#MobaFont%10#0#
            """;
        LocalizationManager localizer = await LocalizerAsync();
        SharedSessionProvider provider = await LoadedProviderAsync(content);

        Assert.Equal("telnet-a", Assert.Single(provider.Profiles).DisplayName);
        SharedSessionImportReport report = Assert.Single(provider.Sources).Report!;
        var formatter = new SharedSessionImportReportFormatter(localizer);
        Dictionary<string, string> skipped = report.Entries
            .Where(entry => entry.Check == SharedSessionReportCheck.SkippedSession)
            .ToDictionary(entry => entry.SessionName, formatter.Explain, StringComparer.Ordinal);
        Assert.Equal(4, skipped.Count);
        Assert.Equal(localizer.Format("SharedReportSkipJumpHost", "jumpt.example.test"), skipped["telnet-gw"]);
        Assert.Equal(localizer.Format("SharedReportSkipProxy", "proxys.example.test"), skipped["sftp-px"]);
        Assert.Equal(localizer["SharedReportSkipWslType"], skipped["wsl"]);
        Assert.Equal(localizer["SharedReportSkipFtpType"], skipped["ftp-a"]);
        Assert.Empty(provider.Gateways);
    }

    [Fact]
    public async Task Load_ASessionTheSharedFilterRefusesForItsHost_IsReportedWithThatHost()
    {
        const string content = """
            [Bookmarks]
            SubRep=
            ImgNum=42
            [Bookmarks_1]
            SubRep=PROD
            ImgNum=41
            app01=#109#0%app01.example.test%22%jdoe%%0%-1%%%%%-1#MobaFont%10#0#
            bad01=#109#0%-oProxyCommand=calc%22%jdoe%%0%-1%%%%%-1#MobaFont%10#0#
            """;
        LocalizationManager localizer = await LocalizerAsync();
        SharedSessionProvider provider = await LoadedProviderAsync(content);

        SharedSessionImportReport report = Assert.Single(provider.Sources).Report!;
        SharedSessionReportEntry refused = Assert.Single(report.Entries, entry => entry.Check == SharedSessionReportCheck.SkippedSession);
        Assert.Equal("bad01", refused.SessionName);
        Assert.Equal(SharedProfileRefusal.InvalidHost, refused.Refusal);
        Assert.Equal(
            localizer.Format("SharedReportRefusedHost", "-oProxyCommand=calc"),
            new SharedSessionImportReportFormatter(localizer).Explain(refused));
        Assert.Equal("app01", Assert.Single(provider.Profiles).DisplayName);
    }

    [Fact]
    public async Task Load_ReportsOneCaseOfEachCheck_InCheckOrder()
    {
        SharedSessionProvider provider = await LoadedProviderAsync(OneOfEach);

        SharedSessionSourceState state = Assert.Single(provider.Sources);
        SharedSessionImportReport report = Assert.IsType<SharedSessionImportReport>(state.Report);

        Assert.Equal(1, report.CountOf(SharedSessionReportCheck.SkippedSession));
        Assert.Equal("wsl01", report.Entries.Single(entry => entry.Check == SharedSessionReportCheck.SkippedSession).SessionName);
        Assert.Equal(1, report.CountOf(SharedSessionReportCheck.UnresolvedVariable));
        Assert.Equal("app02", report.Entries.Single(entry => entry.Check == SharedSessionReportCheck.NameHostMismatch).SessionName);
        Assert.Equal("app04", report.Entries.Single(entry => entry.Check == SharedSessionReportCheck.GatewayOutlier).SessionName);
        Assert.Equal(2, report.CountOf(SharedSessionReportCheck.DuplicateHost));
        Assert.Equal(6, state.IssueCount);
        Assert.All(report.Entries.Where(entry => entry.Check != SharedSessionReportCheck.SkippedSession), entry => Assert.Equal("PROD/Linux", entry.FolderPath));
        Assert.Equal(report.Entries.Select(entry => entry.Check).Order(), report.Entries.Select(entry => entry.Check));

        // Informational only: every session the parser took is loaded, the reported ones included.
        Assert.Equal(5, state.ProfileCount);
    }

    [Fact]
    public async Task Reload_RebuildsTheReport_AndCarriesThePreviousCount()
    {
        string path = WriteSource(OneOfEach);
        SharedSessionProvider provider = await LoadedProviderAsync(path: path);
        List<SharedSessionSourceReload> reloads = [];
        provider.SourceReloaded += reloads.Add;

        // app02 pointed at its own host: the mismatch and the duplicate both go.
        File.WriteAllText(path, OneOfEach.Replace("app02=#109#0%app03", "app02=#109#0%app02", StringComparison.Ordinal));
        await provider.ReloadSourceAsync(SourceId);

        SharedSessionSourceReload reload = Assert.Single(reloads);
        Assert.Equal(6, reload.PreviousIssueCount);
        Assert.Equal(3, reload.State.IssueCount);
        Assert.True(reload.IssueCountChanged);
        Assert.Equal(0, reload.State.Report!.CountOf(SharedSessionReportCheck.NameHostMismatch));
        Assert.Equal(0, reload.State.Report.CountOf(SharedSessionReportCheck.DuplicateHost));
    }

    [Fact]
    public async Task CleanFile_HasAnEmptyReport()
    {
        const string clean = """
            [Bookmarks_1]
            SubRep=PROD
            ImgNum=41
            app01=#109#0%app01.example.test%22%jdoe%%0%-1%%%%%-1#MobaFont%10#0#
            app02=#109#0%app02.example.test%22%jdoe%%0%-1%%%%%-1#MobaFont%10#0#
            """;
        SharedSessionProvider provider = await LoadedProviderAsync(clean);

        SharedSessionSourceState state = Assert.Single(provider.Sources);

        Assert.True(state.Report!.IsEmpty);
        Assert.Equal(0, state.IssueCount);
    }

    // --- Dialog ---

    [Fact]
    public async Task Dialog_SummarizesEveryCheck_AndGroupsTheFindings()
    {
        LocalizationManager localizer = await LocalizerAsync();
        SharedSessionImportReport report = Assert.IsType<SharedSessionImportReport>(
            Assert.Single((await LoadedProviderAsync(OneOfEach)).Sources).Report);

        var dialog = new SharedSessionImportReportDialogViewModel(report, localizer, new RecordingClipboard(), _ => null);

        Assert.Equal(5, dialog.Summary.Count);
        Assert.Equal(localizer["SharedReportCheckDuplicateHost"], dialog.Summary[^1].Label);
        Assert.Equal(2, dialog.Summary[^1].Count);
        Assert.Equal(5, dialog.Groups.Count);
        Assert.Equal(localizer.Format("SharedReportDialogTitle", SourceName), dialog.DialogTitle);
        Assert.Equal("6 findings", dialog.TotalText);
        Assert.Contains(
            dialog.Groups.SelectMany(group => group.Rows),
            row => row.Session == "app02" && row.Explanation == "The session is named app02 but reaches app03.example.test.");
    }

    [Fact]
    public async Task Copy_PutsThePlainTextOnTheClipboard()
    {
        LocalizationManager localizer = await LocalizerAsync();
        SharedSessionImportReport report = Assert.Single((await LoadedProviderAsync(OneOfEach)).Sources).Report!;
        var clipboard = new RecordingClipboard();
        var dialog = new SharedSessionImportReportDialogViewModel(report, localizer, clipboard, _ => null);

        dialog.CopyCommand.Execute(null);

        string text = Assert.Single(clipboard.Texts);
        Assert.StartsWith("Import report: Team", text, StringComparison.Ordinal);
        Assert.Contains("Gateway outliers: 1", text, StringComparison.Ordinal);
        Assert.Contains("PROD/Linux / app04: Goes through jdoe@gw2.example.test while most sessions of this folder go through jdoe@gw1.example.test.", text, StringComparison.Ordinal);
        Assert.Equal(localizer["SharedReportCopied"], dialog.StatusText);
    }

    [Fact]
    public async Task ExportCsv_WritesEveryEntry_WithEveryCellNeutralized()
    {
        LocalizationManager localizer = await LocalizerAsync();
        SharedSessionImportReport report = new(
            SourceId,
            SourceName,
            [
                new SharedSessionReportEntry(SharedSessionReportCheck.NameHostMismatch, "=PROD", "=cmd|' /C calc'!A0", "+evil.example.test"),
                new SharedSessionReportEntry(SharedSessionReportCheck.DuplicateHost, "LAB", "db01", "db01.example.test", "@other"),
            ]);
        string target = Path.Combine(_directory, "report.csv");
        string? proposed = null;
        var dialog = new SharedSessionImportReportDialogViewModel(report, localizer, new RecordingClipboard(), name =>
        {
            proposed = name;
            return target;
        });

        await dialog.ExportCsvCommand.ExecuteAsync(null);

        Assert.Equal("import-report-" + SourceId + ".csv", proposed);
        string[] lines = File.ReadAllLines(target);
        Assert.Equal(3, lines.Length);
        Assert.Equal("\"Check\",\"Folder\",\"Session\",\"Detail\",\"Explanation\"", lines[0]);
        Assert.StartsWith("\"Name and host mismatch\",\"'=PROD\",\"'=cmd|' /C calc'!A0\",\"'+evil.example.test\",", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain(",\"=", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.DoesNotContain(",\"+", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Equal(localizer.Format("SharedReportExported", target), dialog.StatusText);
    }

    [Fact]
    public async Task ExportCsv_Cancelled_WritesNothing()
    {
        LocalizationManager localizer = await LocalizerAsync();
        var dialog = new SharedSessionImportReportDialogViewModel(
            new SharedSessionImportReport(SourceId, SourceName, [new SharedSessionReportEntry(SharedSessionReportCheck.DuplicateHost, "", "a", "h", "b")]),
            localizer,
            new RecordingClipboard(),
            _ => null);

        await dialog.ExportCsvCommand.ExecuteAsync(null);

        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Equal(string.Empty, dialog.StatusText);
    }

    // --- Helpers ---

    private static SharedSessionImportReport BuildReport(IReadOnlyList<ServerProfileDto> loaded, Options? options = null) =>
        Build(
            SourceId,
            SourceName,
            null,
            [],
            loaded,
            [Gateway(Gw1, "gw1"), Gateway(Gw2, "gw2")],
            [],
            options ?? new Options(60, 3));

    private static ServerProfileDto Session(
        string name,
        string folder,
        string? gatewayId,
        string? host = null,
        string connectionType = "SSH")
    {
        return new ServerProfileDto
        {
            Id = SharedSessionIds.PrefixFor(SourceId) + Guid.NewGuid().ToString("N")[..8],
            DisplayName = name,
            Group = folder,
            ConnectionType = connectionType,
            RemoteServer = host ?? name + ".example.test",
            SshGatewayId = gatewayId,
            UseDirectConnection = gatewayId is null,
        };
    }

    private static SshGatewayDto Gateway(string id, string name) => new() { Id = id, Name = name, Host = name + ".example.test" };

    private string WriteSource(string content)
    {
        string path = Path.Combine(_directory, "team.mxtsessions");
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<SharedSessionProvider> LoadedProviderAsync(string? content = null, string? path = null)
    {
        path ??= WriteSource(content!);
        var provider = new SharedSessionProvider(await LocalizerAsync());
        var settings = new AppSettings();
        settings.MobaXtermUserVariables["DAS"] = "jdoe";
        settings.SharedSessionSources.Add(new SharedSessionSourceDto { Id = SourceId, Name = SourceName, Path = path });
        await provider.LoadAsync(settings);
        return provider;
    }

    private static async Task<LocalizationManager> LocalizerAsync()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public List<string> Texts { get; } = [];

        public void SetText(string text) => Texts.Add(text);
    }
}
