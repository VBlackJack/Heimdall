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
using FluentFTP;
using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

public sealed class FtpBrowserProgressTests
{
    [Fact]
    public void CreateProgress_ReportRaisesTransferProgressBeforeReturning()
    {
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        QueuedSynchronizationContext queuedContext = new();
        SynchronizationContext.SetSynchronizationContext(queuedContext);

        try
        {
            using FtpBrowser browser = new();
            using CancellationTokenSource cancellation = new();
            bool progressObserved = false;
            browser.TransferProgress += progress =>
            {
                progressObserved = true;
                cancellation.Cancel();
            };
            IProgress<FtpProgress> reporter = CreateProgress(browser);
            FtpProgress progress = new(1, 1)
            {
                TransferredBytes = 17
            };

            reporter.Report(progress);

            Assert.True(progressObserved);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(0, queuedContext.PostCount);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static IProgress<FtpProgress> CreateProgress(FtpBrowser browser)
    {
        MethodInfo? method = typeof(FtpBrowser).GetMethod(
            "CreateProgress",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IProgress<FtpProgress>>(
            method.Invoke(browser, ["settings.conf", 17L, false]));
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _ = callback;
            _ = state;
            PostCount++;
        }
    }
}
