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
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Heimdall.App.Services;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The pages the quick file server sends to a browser are written in the interface language, and
/// what the operator named stays encoded inside them.
/// </summary>
/// <remarks>
/// The TFTP error strings are deliberately not covered: they are NetASCII text for remote
/// clients and stay English.
/// </remarks>
public sealed class EphemeralFileServerPageLocalizationTests : IDisposable
{
    private const string FolderName = "R&D";
    private const string EncodedFolderName = "R&amp;D";
    private const string BearerScheme = "Bearer";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"heimdall-page-l10n-{Guid.NewGuid():N}");

    public EphemeralFileServerPageLocalizationTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "docs"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* test cleanup */ }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("en", "Heimdall File Server", "Index of /docs", "Heimdall Ephemeral File Server")]
    [InlineData("fr", "Serveur de fichiers Heimdall", "Index de /docs", "Serveur de fichiers éphémère Heimdall")]
    [InlineData("es", "Servidor de archivos de Heimdall", "Índice de /docs", "Servidor de archivos efímero de Heimdall")]
    public async Task DirectoryListing_IsWrittenInTheInterfaceLanguage(
        string language, string title, string indexOf, string footer)
    {
        (EphemeralFileServer server, int port) = await StartAsync(language);
        await using EphemeralFileServer running = server;

        (HttpStatusCode status, string raw) = await GetAsync(running, port, "/docs", authorized: true);
        // Accents go out as character references; compare what a browser shows.
        string body = WebUtility.HtmlDecode(raw);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains($"<title>{title}</title>", body, StringComparison.Ordinal);
        Assert.Contains($"<h2>{indexOf}</h2>", body, StringComparison.Ordinal);
        Assert.Contains($"<em>{footer}</em>", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "404 - Not Found")]
    [InlineData("fr", "404 - Introuvable")]
    [InlineData("es", "404 - No encontrado")]
    public async Task NotFoundPage_IsWrittenInTheInterfaceLanguage(string language, string heading)
    {
        (EphemeralFileServer server, int port) = await StartAsync(language);
        await using EphemeralFileServer running = server;

        (HttpStatusCode status, string raw) = await GetAsync(running, port, "/missing.txt", authorized: true);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains($"<h1>{heading}</h1>", WebUtility.HtmlDecode(raw), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "Unauthorized")]
    [InlineData("fr", "Non autorisé")]
    [InlineData("es", "No autorizado")]
    public async Task UnauthorizedReply_IsWrittenInTheInterfaceLanguage(string language, string text)
    {
        (EphemeralFileServer server, int port) = await StartAsync(language);
        await using EphemeralFileServer running = server;

        (HttpStatusCode status, string body) = await GetAsync(running, port, "/", authorized: false);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(text, body);
    }

    [Fact]
    public async Task TheOperatorsFolderName_StaysEncodedInsideALocalizedPage()
    {
        // A folder name with a markup character must not become markup because the page around
        // it is now built from catalogue text.
        Directory.CreateDirectory(Path.Combine(_directory, FolderName));
        (EphemeralFileServer server, int port) = await StartAsync("fr");
        await using EphemeralFileServer running = server;

        (_, string body) = await GetAsync(running, port, "/", authorized: true);
        (_, string inner) = await GetAsync(running, port, "/" + Uri.EscapeDataString(FolderName), authorized: true);

        Assert.Contains(EncodedFolderName + "/", body, StringComparison.Ordinal);
        Assert.Contains($"<h2>Index de /{EncodedFolderName}</h2>", inner, StringComparison.Ordinal);
    }

    private async Task<(EphemeralFileServer Server, int Port)> StartAsync(string language)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), language);

        EphemeralFileServer server = new()
        {
            ShutdownTimeoutMs = 500,
            Localize = localizer.GetString,
        };
        int port = FreePort();
        await server.StartHttpServerAsync(_directory, port);
        return (server, port);
    }

    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(
        EphemeralFileServer server, int port, string path, bool authorized)
    {
        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Get, $"http://localhost:{port}{path}");
        if (authorized)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, server.AccessToken);
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
