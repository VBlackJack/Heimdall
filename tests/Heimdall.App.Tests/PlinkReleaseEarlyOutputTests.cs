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
using System.Text;
using Heimdall.App.Services.Handlers;
using Heimdall.Terminal;

namespace Heimdall.App.Tests;

/// <summary>
/// The attested release handles learn that the launcher produced output so they can delete the
/// password file and the size session early. Doing so must not consume the session's bootstrap
/// replay: the terminal view subscribes later and still has to receive the banner and first prompt.
/// </summary>
public sealed class PlinkReleaseEarlyOutputTests
{
    private const string Marker = "HEIMDALL-MARKER";
    private const string PasswordFilePath = "heimdall-pw-file-placeholder";
    private const string SizeSessionName = "HeimdallPtySize-early-output";
    private const string ChildArguments = "/d /c \"echo " + Marker + "& set /p _=\"";
    private const string ChildTerminator = "\r\n";
    private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task AttestedHandles_LeaveTheBootstrapForTheView()
    {
        PipeModeSession session = new();
        TaskCompletionSource<bool> passwordDeleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> sizeDeleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> markerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int passwordDeletes = 0;
        int sizeDeletes = 0;
        bool runningAtPasswordDelete = false;
        bool runningAtSizeDelete = false;
        StringBuilder viewOutput = new();
        object viewLock = new();

        try
        {
            PlinkPasswordFileRelease.Arm(
                session,
                PasswordFilePath,
                _ =>
                {
                    Interlocked.Increment(ref passwordDeletes);
                    runningAtPasswordDelete = session.IsRunning;
                    passwordDeleted.TrySetResult(true);
                },
                firstByteProvesConsumption: true);
            PlinkSizeSession.ArmRelease(
                session,
                SizeSessionName,
                _ =>
                {
                    Interlocked.Increment(ref sizeDeletes);
                    runningAtSizeDelete = session.IsRunning;
                    sizeDeleted.TrySetResult(true);
                },
                firstByteProvesConsumption: true);

            await session.StartAsync(ResolveCommandProcessor(), ChildArguments);

            // The session raises the first-output signal only after the chunk has been buffered (or
            // delivered), so once both deletes have run the bootstrap output is already held for the
            // view. Subscribing after the deletes therefore leaves no window in which it could be lost.
            await Task.WhenAll(passwordDeleted.Task, sizeDeleted.Task).WaitAsync(WaitBound);

            Assert.Equal(1, Volatile.Read(ref passwordDeletes));
            Assert.Equal(1, Volatile.Read(ref sizeDeletes));
            Assert.True(runningAtPasswordDelete, "The password file went at exit, not on the first output.");
            Assert.True(runningAtSizeDelete, "The size session went at exit, not on the first output.");

            int replayedAtSubscription;
            session.DataReceived += data =>
            {
                string text = Encoding.UTF8.GetString(data.Span);
                bool seen;
                lock (viewLock)
                {
                    viewOutput.Append(text);
                    seen = viewOutput.ToString().Contains(Marker, StringComparison.Ordinal);
                }

                if (seen)
                {
                    markerSeen.TrySetResult(true);
                }
            };

            // The replay is delivered inside the add above, so whatever is present when it returns
            // was replayed from the bootstrap buffer and not received live.
            lock (viewLock)
            {
                replayedAtSubscription = viewOutput.Length;
            }

            Assert.True(
                replayedAtSubscription > 0,
                "Nothing was replayed to the view: the release handles consumed the bootstrap buffer.");

            await markerSeen.Task.WaitAsync(WaitBound);
        }
        finally
        {
            try
            {
                session.Write(ChildTerminator);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The child may already be gone; the point of this write is only to let it end.
            }

            session.Dispose();
        }
    }

    private static string ResolveCommandProcessor()
    {
        string commandProcessor = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        return File.Exists(commandProcessor) ? commandProcessor : "cmd.exe";
    }
}
