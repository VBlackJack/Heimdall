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

using System.Reflection;

namespace Heimdall.Sftp.Tests;

public sealed class FtpBrowserDisconnectTests
{
    [Fact]
    public async Task DisconnectAsync_WhenOperationIsActive_WaitsWithoutBlockingCaller()
    {
        using FtpBrowser browser = new();
        SemaphoreSlim operationLock = GetOperationLock(browser);
        await operationLock.WaitAsync();

        try
        {
            Task disconnect = browser.DisconnectAsync(CancellationToken.None);

            Assert.False(disconnect.IsCompleted);

            operationLock.Release();
            await disconnect.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (operationLock.CurrentCount == 0)
            {
                operationLock.Release();
            }
        }
    }

    [Fact]
    public async Task DisconnectAsync_WhenOperationIsActive_HonorsCancellation()
    {
        using FtpBrowser browser = new();
        SemaphoreSlim operationLock = GetOperationLock(browser);
        await operationLock.WaitAsync();
        using CancellationTokenSource cancellation = new();

        try
        {
            Task disconnect = browser.DisconnectAsync(cancellation.Token);

            Assert.False(disconnect.IsCompleted);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnect);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private static SemaphoreSlim GetOperationLock(FtpBrowser browser)
    {
        FieldInfo field = typeof(FtpBrowser).GetField(
            "_opLock",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FtpBrowser operation lock was not found.");

        return (SemaphoreSlim)(field.GetValue(browser)
            ?? throw new InvalidOperationException("FtpBrowser operation lock was null."));
    }
}
