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

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Heimdall.App.Services;
using Heimdall.Core.SystemInfo;

namespace Heimdall.App.Tests;

public sealed class ServiceStatusServiceTests
{
    private static string DecodeEncodedCommand(string arguments)
    {
        var marker = "-EncodedCommand ";
        var start = arguments.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected -EncodedCommand in arguments.");
        var encoded = arguments[(start + marker.Length)..].Trim();
        return Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
    }

    [Fact]
    public void Constructor_NullLoadDelegate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceStatusService(null!, _ => null));
    }

    [Fact]
    public async Task LoadAsync_ParsesRunnerOutput()
    {
        var service = new ServiceStatusService(
            _ => Task.FromResult("""
"Name","DisplayName","Status","StartType"
"w32time","Windows Time","Running","Automatic"
"""),
            _ => null);

        var results = await service.LoadAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("w32time", results[0].Name);
    }

    [Fact]
    public async Task LoadAsync_PreCancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new ServiceStatusService(_ => Task.FromResult(string.Empty), _ => null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync(cts.Token));
    }

    [Fact]
    public async Task LoadAsync_RunnerException_Propagates()
    {
        var service = new ServiceStatusService(_ => throw new InvalidOperationException("boom"), _ => null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadAsync(CancellationToken.None));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task LoadAsync_NullRunnerOutput_ReturnsEmpty()
    {
        var service = new ServiceStatusService(_ => Task.FromResult<string>(null!), _ => null);

        var results = await service.LoadAsync(CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public void StartService_UsesRunasAndEncodedCommand()
    {
        ProcessStartInfo? captured = null;
        var service = new ServiceStatusService(_ => Task.FromResult(string.Empty), psi =>
        {
            captured = psi;
            return null;
        });

        service.StartService("O'Brien");

        Assert.NotNull(captured);
        Assert.Equal("powershell.exe", Path.GetFileName(captured!.FileName), ignoreCase: true);
        Assert.True(captured.UseShellExecute);
        Assert.Equal("runas", captured.Verb);
        Assert.Contains("-EncodedCommand ", captured.Arguments, StringComparison.Ordinal);

        var script = DecodeEncodedCommand(captured.Arguments);
        Assert.Equal("Start-Service 'O''Brien'", script);
    }

    [Fact]
    public void StopService_Win32Exception_IsSwallowed()
    {
        var service = new ServiceStatusService(
            _ => Task.FromResult(string.Empty),
            _ => throw new Win32Exception("denied"));

        service.StopService("bits");
    }

    [Fact]
    public void RestartService_TrimsAndEncodesServiceName()
    {
        ProcessStartInfo? captured = null;
        var service = new ServiceStatusService(_ => Task.FromResult(string.Empty), psi =>
        {
            captured = psi;
            return null;
        });

        service.RestartService("  bits  ");

        var script = DecodeEncodedCommand(captured!.Arguments);
        Assert.Equal("Restart-Service 'bits'", script);
    }

    [Fact]
    public void StartService_EmptyServiceName_Throws()
    {
        var service = new ServiceStatusService(_ => Task.FromResult(string.Empty), _ => null);

        Assert.Throws<ArgumentException>(() => service.StartService(" "));
    }

}
