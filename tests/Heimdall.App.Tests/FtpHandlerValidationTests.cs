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
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class FtpHandlerValidationTests
{
    [Fact]
    public async Task ConnectAsync_RejectsWhitespaceHost()
    {
        var handler = CreateHandler();
        var server = CreateServer("   ", 21);

        var result = await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorInvalidTargetHost", result.ErrorMessage);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task ConnectAsync_RejectsInvalidHost()
    {
        var handler = CreateHandler();
        var server = CreateServer("this is not a host", 21);

        var result = await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorInvalidTargetHost", result.ErrorMessage);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task ConnectAsync_RejectsOutOfRangePort()
    {
        var handler = CreateHandler();
        var server = CreateServer("ftp.example.com", 70_000);

        var result = await handler.ConnectAsync(server, new AppSettings(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorInvalidPort", result.ErrorMessage);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task ComputeCleartextWarning_NonTlsWithUsername_ReturnsLocalizedWarning()
    {
        var localizer = await CreateLocalizerAsync();

        var warning = FtpHandler.ComputeCleartextWarning(
            useSsl: false,
            username: "operator",
            host: "ftp.example.com",
            port: 21,
            localizer);

        Assert.NotNull(warning);
        Assert.Contains("ftp.example.com:21", warning, StringComparison.Ordinal);
        Assert.Contains("cleartext", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, "operator")]
    [InlineData(false, null)]
    [InlineData(false, "")]
    public void ComputeCleartextWarning_TlsOrEmptyUsername_ReturnsNull(
        bool useSsl,
        string? username)
    {
        var warning = FtpHandler.ComputeCleartextWarning(
            useSsl,
            username,
            "ftp.example.com",
            21,
            new LocalizationManager());

        Assert.Null(warning);
    }

    private static FtpHandler CreateHandler()
        => new(
            new ConnectionStateMachine(),
            new LocalizationManager(),
            new FtpsCertificateStore(),
            RejectingFtpsCertificateVerifier.Instance);

    private static ServerProfileDto CreateServer(string host, int port)
        => new()
        {
            Id = "ftp-test",
            DisplayName = "FTP Test",
            RemoteServer = host,
            ConnectionType = "FTP",
            FtpPort = port,
            FtpUsername = "operator",
            UseDirectConnection = true
        };

    private static async Task<LocalizationManager> CreateLocalizerAsync()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(FindLocalesPath(), "en").ConfigureAwait(false);
        return localizer;
    }

    private static string FindLocalesPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "locales");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Cannot find locales directory.");
    }
}
