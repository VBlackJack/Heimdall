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

using System.Diagnostics;
using System.Net;

namespace Heimdall.Terminal.Tests;

internal static class TerminalTestHelpers
{
    internal static string ResolvePowerShellExecutable()
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string systemPowerShell = Path.Combine(
            windowsDirectory,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return File.Exists(systemPowerShell) ? systemPowerShell : "powershell.exe";
    }

    internal static int ReserveLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static async Task SendBytesOneAtATimeAsync(
        TcpListener listener,
        byte[] bytes,
        TimeSpan? delayBetweenBytes = null)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        client.NoDelay = true;
        await using NetworkStream stream = client.GetStream();
        TimeSpan delay = delayBetweenBytes ?? TimeSpan.FromMilliseconds(25);
        byte[] singleByte = new byte[1];

        foreach (byte value in bytes)
        {
            singleByte[0] = value;
            await stream.WriteAsync(singleByte);
            await stream.FlushAsync();
            await Task.Delay(delay);
        }
    }

    internal static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string? failureMessage = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail(failureMessage ?? "Condition was not satisfied within the allotted time.");
    }

    internal static void AssertProcessHasExited(int processId)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited || process.WaitForExit(100))
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
        }

        Assert.Fail($"Terminal process {processId} was still running after session disposal.");
    }
}
