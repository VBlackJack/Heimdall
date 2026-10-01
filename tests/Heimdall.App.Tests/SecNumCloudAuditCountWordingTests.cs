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
using Heimdall.App.Views.Tools;
using Heimdall.Core.Discovery;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The SecNumCloud audit words its counts by their number, in the language of the shell.
/// </summary>
/// <remarks>
/// The engine localizes through a key delegate, which cannot say which language it speaks, so it
/// takes the shell's localizer beside it for the counts. These run the checks that read only the
/// discovered hosts (no probe leaves the process): the network chapter without a web port, the
/// certificate validity check and the service inventory.
/// </remarks>
public sealed class SecNumCloudAuditCountWordingTests
{
    private const int MySqlPort = 3306;
    private const int PostgresPort = 5432;
    private const int RedisPort = 6379;
    private const int SshPort = 22;

    [Theory]
    [InlineData("en", 0, "Inventoried 0 open ports across 1 host.")]
    [InlineData("en", 1, "Inventoried 1 open port across 1 host.")]
    [InlineData("en", 2, "Inventoried 3 open ports across 2 hosts.")]
    [InlineData("fr", 0, "Inventaire : 0 port ouvert sur 1 hôte.")]
    [InlineData("fr", 1, "Inventaire : 1 port ouvert sur 1 hôte.")]
    [InlineData("fr", 2, "Inventaire : 3 ports ouverts sur 2 hôtes.")]
    [InlineData("es", 0, "Inventario: 0 puertos abiertos en 1 host.")]
    [InlineData("es", 1, "Inventario: 1 puerto abierto en 1 host.")]
    [InlineData("es", 2, "Inventario: 3 puertos abiertos en 2 hosts.")]
    public async Task PortInventory_WordsEachCountByItsNumber(string locale, int fixture, string expected)
    {
        AuditCheck net01 = (await RunNetworkChapterAsync(locale, Hosts(fixture)))[0];

        Assert.Equal(expected, net01.Summary);
    }

    [Theory]
    [InlineData("en", 1, "1 non-standard port found - review for necessity.", "Non-standard port: 3306/mysql")]
    [InlineData("en", 2, "3 non-standard ports found - review for necessity.", "Non-standard ports: 3306/mysql, 5432/postgresql")]
    [InlineData("fr", 1, "1 port non standard détecté - vérifier sa nécessité.", "Port non standard : 3306/mysql")]
    [InlineData("fr", 2, "3 ports non standard détectés - vérifier leur nécessité.", "Ports non standard : 3306/mysql, 5432/postgresql")]
    [InlineData("es", 1, "Se encontró 1 puerto no estándar: revisa si es necesario.", "Puerto no estándar: 3306/mysql")]
    public async Task NonStandardPorts_WordTheSummaryByTheCountAndTheEvidenceByTheList(
        string locale,
        int fixture,
        string expectedSummary,
        string expectedFirstEvidence)
    {
        AuditCheck net02 = (await RunNetworkChapterAsync(locale, Hosts(fixture)))[1];

        Assert.Equal(expectedSummary, net02.Summary);
        Assert.Equal(expectedFirstEvidence, net02.Evidence[0].Detail);
    }

    [Theory]
    [InlineData("en", 1, "Detected 1 subnet. ", "1 open port: 3306/mysql")]
    [InlineData("en", 2, "Detected 2 subnets. ", "2 open ports: 3306/mysql, 5432/postgresql")]
    [InlineData("fr", 1, "1 sous-réseau détecté. ", "1 port ouvert : 3306/mysql")]
    [InlineData("fr", 2, "2 sous-réseaux détectés. ", "2 ports ouverts : 3306/mysql, 5432/postgresql")]
    [InlineData("es", 1, "Se detectó 1 subred. ", "1 puerto abierto: 3306/mysql")]
    [InlineData("es", 2, "Se detectaron 2 subredes. ", "2 puertos abiertos: 3306/mysql, 5432/postgresql")]
    public async Task Segmentation_AndOpenPortEvidence_WordTheirCounts(
        string locale,
        int fixture,
        string expectedSegmentationStart,
        string expectedFirstOpenPorts)
    {
        List<AuditCheck> checks = await RunNetworkChapterAsync(locale, Hosts(fixture));

        Assert.StartsWith(expectedSegmentationStart, checks[3].Summary, StringComparison.Ordinal);
        Assert.Equal(expectedFirstOpenPorts, checks[0].Evidence[0].Detail);
    }

    [Theory]
    [InlineData("en", false, "1 certificate checked: it is valid.")]
    [InlineData("en", true, "1 expired certificate, 0 expiring soon, out of 2 total.")]
    [InlineData("fr", false, "1 certificat vérifié : valide.")]
    [InlineData("fr", true, "1 certificat expiré, 0 expirant prochainement, sur 2 au total.")]
    [InlineData("es", false, "1 certificado verificado: válido.")]
    [InlineData("es", true, "1 certificado caducado, 0 caducando pronto, de un total de 2.")]
    public async Task CertificateValidity_WordsItsCounts(string locale, bool withExpired, string expected)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);
        var engine = new SecNumCloudAuditEngine(localizer.GetString, localizer);
        List<ServiceResult> services = [Tls(443, expired: false)];
        if (withExpired)
        {
            services.Add(Tls(8443, expired: true));
        }

        var check = new AuditCheck { Id = "CRY-03", Name = "certs", SecNumCloudClause = "10.1" };
        engine.CheckCertificateValidity(check, [Host("10.0.0.1", services)]);

        Assert.Equal(expected, check.Summary);
    }

    [Theory]
    [InlineData("en", "Inventoried 1 unique service across 1 host, 1 OS variant detected.", "ssh: 1 instance", "OS: Linux (1 host)")]
    [InlineData("fr", "Inventaire : 1 service unique sur 1 hôte, 1 variante d'OS détectée.", "ssh : 1 instance", "OS : Linux (1 hôte)")]
    [InlineData("es", "Inventario: 1 servicio único en 1 host, 1 variante de SO detectada.", "ssh: 1 instancia", "SO: Linux (1 host)")]
    public async Task ServiceInventory_WordsItsCounts(
        string locale,
        string expectedSummary,
        string expectedService,
        string expectedOs)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);
        var engine = new SecNumCloudAuditEngine(localizer.GetString, localizer);
        HostScanResult host = Host("10.0.0.1", [Open(SshPort, "ssh")]) with
        {
            OsFingerprint = new OsFingerprint("Linux", "ttl", 80),
        };
        var check = new AuditCheck { Id = "OPS-04", Name = "inventory", SecNumCloudClause = "12.1" };

        engine.BuildServiceInventory(check, [host], snapshot: null);

        Assert.Equal(expectedSummary, check.Summary);
        Assert.Equal([expectedService, expectedOs], check.Evidence.Select(evidence => evidence.Detail));
    }

    /// <summary>Without a count localizer the engine words its counts under the English rule.</summary>
    [Fact]
    public async Task WithoutACountLocalizer_TheEnglishRuleApplies()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("fr");
        var engine = new SecNumCloudAuditEngine(localizer.GetString);
        var chapter = engine.BuildNetworkChapter();

        await engine.RunNetworkChecksAsync(chapter, Hosts(0), snapshot: null, CancellationToken.None);

        Assert.Equal("Inventaire : 0 ports ouverts sur 1 hôte.", chapter.Checks[0].Summary);
    }

    [Theory]
    [InlineData("en", 1, "1 host detected", "1 evidence item", "1 subnet detected on gw")]
    [InlineData("en", 2, "2 hosts detected", "2 evidence items", "2 subnets detected on gw")]
    [InlineData("fr", 0, "0 hôte détecté", "0 élément de preuve", "0 sous-réseau détecté sur gw")]
    [InlineData("fr", 2, "2 hôtes détectés", "2 éléments de preuve", "2 sous-réseaux détectés sur gw")]
    [InlineData("es", 1, "1 host detectado", "1 elemento de evidencia", "1 subred detectada en gw")]
    public async Task AuditView_WordsItsCounts(
        string locale,
        int count,
        string expectedHosts,
        string expectedEvidence,
        string expectedSubnets)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        Assert.Equal(expectedHosts, SecNumCloudAuditView.DescribeDetectedHosts(localizer, count));
        Assert.Equal(expectedEvidence, SecNumCloudAuditView.DescribeEvidenceCount(localizer, count));
        Assert.Equal(expectedSubnets, SecNumCloudAuditView.DescribeDetectedSubnets(localizer, count, "gw"));
    }

    private static async Task<List<AuditCheck>> RunNetworkChapterAsync(string locale, List<HostScanResult> hosts)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);
        var engine = new SecNumCloudAuditEngine(localizer.GetString, localizer);
        var chapter = engine.BuildNetworkChapter();

        await engine.RunNetworkChecksAsync(chapter, hosts, snapshot: null, CancellationToken.None);

        return chapter.Checks;
    }

    /// <summary>
    /// 0: one host, no open port. 1: one host, one non-standard port. 2: two hosts in two
    /// subnets, three non-standard ports. No web port, so the header check never probes.
    /// </summary>
    private static List<HostScanResult> Hosts(int fixture) => fixture switch
    {
        0 => [Host("10.0.0.1", [])],
        1 => [Host("10.0.0.1", [Open(MySqlPort, "mysql")])],
        _ =>
        [
            Host("10.0.0.1", [Open(MySqlPort, "mysql"), Open(PostgresPort, "postgresql")]),
            Host("10.0.1.1", [Open(RedisPort, "redis")]),
        ],
    };

    private static HostScanResult Host(string ip, List<ServiceResult> services) =>
        new(ip, null, true, 1, services, null, []);

    private static ServiceResult Open(int port, string name) =>
        new(port, true, name, null, null, 1);

    private static ServiceResult Tls(int port, bool expired) =>
        new(port, true, "https", null, null, 1, new CertificateInfo(
            "CN=test",
            "CN=ca",
            DateTime.UtcNow.AddYears(-2),
            expired ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddYears(1),
            expired,
            false,
            "RSA",
            "sha256RSA",
            [],
            "TLS 1.3",
            "00"));

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return localizer;
    }
}
