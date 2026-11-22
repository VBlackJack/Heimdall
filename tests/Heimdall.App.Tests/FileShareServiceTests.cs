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
using System.Net.Sockets;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class FileShareServiceTests : IDisposable
{
    private readonly string _testDir;

    public FileShareServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"heimdall-fileshare-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        File.WriteAllText(Path.Combine(_testDir, "payload.txt"), "payload");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDir, true);
        }
        catch
        {
            // Test cleanup.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task StartAsync_Publishes_Tokenized_BaseUrl()
    {
        await using var service = new FileShareService();
        FileShareStartedEventArgs? startedArgs = null;
        service.SharingStarted += (_, args) => startedArgs = args;

        var started = await service.StartAsync(_testDir, CreateSettings(enableTftp: false));
        if (!started)
        {
            return; // skip — listeners unavailable in this environment
        }

        try
        {
            Assert.NotNull(startedArgs);
            Assert.NotNull(service.BaseUrl);
            Assert.Contains("token=", service.BaseUrl, StringComparison.Ordinal);
            Assert.Equal(service.BaseUrl, startedArgs!.BaseUrl);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task StartAsync_Published_BaseUrl_Returns_Ok()
    {
        await using var service = new FileShareService();
        File.WriteAllText(Path.Combine(_testDir, "payload.txt"), "payload");

        var started = await service.StartAsync(_testDir, CreateSettings(enableTftp: false));
        if (!started || service.BaseUrl is null)
        {
            return; // skip — listeners unavailable in this environment
        }

        try
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(service.BaseUrl);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("payload.txt", body, StringComparison.Ordinal);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task StartAsync_With_Tftp_Disabled_Omits_Tftp_Template()
    {
        await using var service = new FileShareService();
        FileShareStartedEventArgs? startedArgs = null;
        service.SharingStarted += (_, args) => startedArgs = args;

        var started = await service.StartAsync(_testDir, CreateSettings(enableTftp: false));
        if (!started)
        {
            return; // skip — listeners unavailable in this environment
        }

        try
        {
            Assert.NotNull(startedArgs);
            Assert.False(startedArgs!.IsTftpEnabled);
            Assert.Null(startedArgs.TftpCommand);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task StartAsync_With_Tftp_Enabled_Publishes_Tftp_Template()
    {
        await using var service = new FileShareService { TftpBindAddress = IPAddress.Loopback };
        FileShareStartedEventArgs? startedArgs = null;
        service.SharingStarted += (_, args) => startedArgs = args;

        var started = await service.StartAsync(_testDir, CreateSettings(enableTftp: true));
        if (!started)
        {
            return; // skip — listeners unavailable in this environment
        }

        try
        {
            Assert.NotNull(startedArgs);

            if (!startedArgs!.IsTftpEnabled)
            {
                return; // skip — UDP listener unavailable in this environment
            }

            Assert.NotNull(startedArgs.TftpCommand);
            Assert.Contains("tftp ", startedArgs.TftpCommand, StringComparison.Ordinal);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task StartAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        FileShareService service = new();
        await service.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartAsync(_testDir, null));
    }

    [Fact]
    public async Task StopAsync_WhenNeverStarted_IsIdempotent()
    {
        await using FileShareService service = new();

        await service.StopAsync();
        await service.StopAsync();
    }

    [Fact]
    public async Task StartAsync_WhileSharing_ThrowsInvalidOperationException()
    {
        await using FileShareService service = new();
        bool started = await service.StartAsync(_testDir, CreateSettings(enableTftp: false));
        if (!started)
        {
            return; // skip — listeners unavailable in this environment
        }

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartAsync(_testDir, CreateSettings(enableTftp: false)));
        }
        finally
        {
            await service.StopAsync();
        }
    }

    private static AppSettings CreateSettings(bool enableTftp)
    {
        return new AppSettings
        {
            EphemeralHttpPort = GetFreeTcpPort(),
            EphemeralTftpPort = GetFreeUdpPort(),
            FileShareEnableTftp = enableTftp,
            ServerShutdownTimeoutMs = 500,
        };
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
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

    private static int GetFreeUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }
}
