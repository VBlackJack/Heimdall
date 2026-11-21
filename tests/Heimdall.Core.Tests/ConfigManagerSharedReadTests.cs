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

using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

public sealed class ConfigManagerSharedReadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "heimdall-sharedread-" + Guid.NewGuid().ToString("N"));

    public ConfigManagerSharedReadTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void OpenSharedRead_AllowsRenameAndWriteWhileHandleIsOpen()
    {
        string path = Path.Combine(_dir, "servers.json");
        string moved = path + ".bak";
        File.WriteAllText(path, "{\"old\":true}");

        using (FileStream reader = ConfigManager.OpenSharedRead(path))
        {
            // A concurrent writer may hold the file open for writing...
            using (FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.True(writer.CanWrite);
            }

            // ...and a save may rename the file away; neither may be blocked by the reader.
            File.Move(path, moved);
        }

        Assert.True(File.Exists(moved));
    }
}
