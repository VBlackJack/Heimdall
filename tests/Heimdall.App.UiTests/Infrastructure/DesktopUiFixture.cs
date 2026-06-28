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
using System.IO;

namespace Heimdall.App.UiTests.Infrastructure;

public sealed class DesktopUiFixture
{
    public DesktopUiFixture()
    {
        TryPurge(Path.Combine(AppContext.BaseDirectory, "config", "session-snapshot.json"));

        var debugDir = WpfTestHost.ResolveAppBuildDir("Debug");
        if (debugDir is not null)
        {
            TryPurge(Path.Combine(debugDir, "config", "session-snapshot.json"));
        }

        // TODO: Add Release-path coverage if the UIA suite starts running against Release outputs.
    }

    private static void TryPurge(string snapshotPath)
    {
        try
        {
            if (File.Exists(snapshotPath))
            {
                File.Delete(snapshotPath);
                Debug.WriteLine($"[DesktopUiFixture] Purged snapshot: {snapshotPath}");
            }
            else
            {
                Debug.WriteLine($"[DesktopUiFixture] Snapshot absent: {snapshotPath}");
            }
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[DesktopUiFixture] Snapshot purge skipped due to IO error: {snapshotPath} ({ex.Message})");
        }
    }
}
