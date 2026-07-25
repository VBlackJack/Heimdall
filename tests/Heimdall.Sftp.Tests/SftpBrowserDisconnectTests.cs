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
using System.Reflection;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Renci.SshNet;

namespace Heimdall.Sftp.Tests;

public sealed class SftpBrowserDisconnectTests
{
    /// <summary>
    /// Failure bound, not a synchronisation point. The waits it bounds complete on
    /// an event: an unwind signal raised from the client's Disposing handler, or the
    /// client lock being released by the code under test. The value only has to be
    /// generous enough that a saturated thread pool cannot exhaust it before that
    /// work is scheduled at all, and it is paid only on failure. Distinct from the
    /// one-second assertion on Disconnect's own elapsed time, which is a semantic
    /// bound on the behaviour under test and must stay tight.
    /// </summary>
    private static readonly TimeSpan SignalBackstop = TimeSpan.FromSeconds(30);

    [Fact]
    public void Disconnect_WhenClientLockIsAvailable_DisposesGracefullyOnce()
    {
        var client = new RecordingSftpClient();
        var browser = CreateBrowser(client, TimeSpan.FromSeconds(1));
        var disconnectedCount = 0;
        browser.Disconnected += _ => disconnectedCount++;

        browser.Disconnect();
        browser.Disconnect();
        browser.Dispose();

        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, disconnectedCount);
        AssertConnectionContextDropped(browser);
    }

    [Fact]
    public async Task Disconnect_WhenOperationHoldsClientLock_ForcesDisposeWithinBound()
    {
        var client = new RecordingSftpClient(throwOnDispose: true);
        var browser = CreateBrowser(client, TimeSpan.FromMilliseconds(40));
        SemaphoreSlim clientLock = GetClientLock(browser);
        await clientLock.WaitAsync();

        var operationUnwound = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disposing += () =>
        {
            clientLock.Release();
            operationUnwound.TrySetResult();
        };

        var disconnectedCount = 0;
        browser.Disconnected += _ => disconnectedCount++;

        var stopwatch = Stopwatch.StartNew();
        browser.Disconnect();
        stopwatch.Stop();

        await operationUnwound.Task.WaitAsync(SignalBackstop);

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Forced disconnect took {stopwatch.Elapsed}.");
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, disconnectedCount);
        AssertConnectionContextDropped(browser);

        Assert.True(await clientLock.WaitAsync(SignalBackstop));
        clientLock.Release();

        browser.Disconnect();
        browser.Dispose();
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, disconnectedCount);
    }

    [Fact]
    public async Task Dispose_WhenOperationReleasesAfterForcedAbort_DoesNotDisposeClientLock()
    {
        var client = new RecordingSftpClient();
        var browser = CreateBrowser(client, TimeSpan.FromMilliseconds(20));
        SemaphoreSlim clientLock = GetClientLock(browser);
        await clientLock.WaitAsync();

        browser.Dispose();

        clientLock.Release();
        Assert.True(await clientLock.WaitAsync(SignalBackstop));
        clientLock.Release();

        browser.Dispose();
        Assert.Equal(1, client.DisposeCount);
    }

    private static SftpBrowser CreateBrowser(
        RecordingSftpClient client,
        TimeSpan disconnectLockTimeout)
    {
        var browser = new SftpBrowser(disconnectLockTimeout);
        SetField(browser, "_client", client);
        SetField(
            browser,
            "_connectionParams",
            new SshConnectionParams
            {
                Host = "example.test",
                Username = "tester",
                Password = "ephemeral"
            });
        SetField(
            browser,
            "_pinnedHostKeyVerifier",
            new PinnedFingerprintVerifier("example.test", 22, "SHA256:test"));
        return browser;
    }

    private static void AssertConnectionContextDropped(SftpBrowser browser)
    {
        Assert.Null(GetFieldValue(browser, "_connectionParams"));
        Assert.Null(GetFieldValue(browser, "_pinnedHostKeyVerifier"));
    }

    private static SemaphoreSlim GetClientLock(SftpBrowser browser)
    {
        return Assert.IsType<SemaphoreSlim>(GetFieldValue(browser, "_clientLock"));
    }

    private static object? GetFieldValue(SftpBrowser browser, string fieldName)
    {
        FieldInfo? field = typeof(SftpBrowser).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(browser);
    }

    private static void SetField(SftpBrowser browser, string fieldName, object value)
    {
        FieldInfo? field = typeof(SftpBrowser).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(browser, value);
    }

    private sealed class RecordingSftpClient(bool throwOnDispose = false)
        : SftpClient(new ConnectionInfo(
            "example.test",
            "tester",
            new NoneAuthenticationMethod("tester")))
    {
        public event Action? Disposing;

        public int DisposeCount { get; private set; }

        public override bool IsConnected => false;

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            Disposing?.Invoke();

            if (throwOnDispose)
            {
                throw new InvalidOperationException("Simulated forced-dispose failure.");
            }
        }
    }
}
