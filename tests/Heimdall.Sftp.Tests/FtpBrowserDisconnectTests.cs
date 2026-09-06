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

    /// <remarks>
    /// The synchronous Disconnect waited on the operation lock with no timeout, pinning a
    /// thread for the whole of a stalled transfer, and Dispose then disposed the semaphore
    /// under any waiter - the opposite of the decision the SFTP browser pins by test.
    /// </remarks>
    [Fact]
    public async Task Disconnect_WhenOperationIsActive_ReturnsWithinItsBound()
    {
        using FtpBrowser browser = new();
        SemaphoreSlim operationLock = GetOperationLock(browser);
        await operationLock.WaitAsync();

        try
        {
            Task disconnect = Task.Run(browser.Disconnect);
            Task finished = await Task.WhenAny(disconnect, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(disconnect, finished);
            await disconnect;
        }
        finally
        {
            operationLock.Release();
        }
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeTheOperationLock()
    {
        FtpBrowser browser = new();
        SemaphoreSlim operationLock = GetOperationLock(browser);

        browser.Dispose();

        // A waiter arriving after the teardown meets the lock, not an ObjectDisposedException.
        await operationLock.WaitAsync();
        operationLock.Release();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("///")]
    public async Task DeleteAsync_RejectsProtectedRootBeforeConnectionCheck(string path)
    {
        using FtpBrowser browser = new();

        InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => browser.DeleteAsync(path));

        Assert.Contains("protected remote root", refusal.Message, StringComparison.Ordinal);
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
