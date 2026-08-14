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

using Heimdall.App.ViewModels;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class SudoUploadCommandsTests
{
    [Fact]
    public void Build_ProducesStreamedAtomicWriteWithoutUnprivilegedStaging()
    {
        string write = SudoUploadCommands.Build("/etc/hosts");

        Assert.Contains("cat > payload", write, StringComparison.Ordinal);
        Assert.Contains("mktemp -d --", write, StringComparison.Ordinal);
        Assert.Contains("mv -fT -- payload", write, StringComparison.Ordinal);
        Assert.Contains("[ -L", write, StringComparison.Ordinal);
        Assert.DoesNotContain(RemoteTempPaths.Prefix, write, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo tee", write, StringComparison.Ordinal);

        // This forbade "cp --" outright to keep the write atomic: copying content into place is
        // exactly what the rename exists to avoid. Narrowed rather than dropped, because metadata
        // preservation introduced a cp that copies NO content - every other form stays banned, so
        // a content copy still fails here.
        for (int index = write.IndexOf("cp --", StringComparison.Ordinal);
             index >= 0;
             index = write.IndexOf("cp --", index + 1, StringComparison.Ordinal))
        {
            Assert.StartsWith("cp --attributes-only ", write[index..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Build_EscapesTargetContainingSingleQuotes()
    {
        string write = SudoUploadCommands.Build("/var/log/oh's.log");

        Assert.EndsWith(@"sh '/var/log/oh'\''s.log'", write, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ThrowsForNullOrWhitespaceTargetPath()
    {
        Assert.ThrowsAny<ArgumentException>(() => SudoUploadCommands.Build(null!));
        Assert.ThrowsAny<ArgumentException>(() => SudoUploadCommands.Build(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => SudoUploadCommands.Build(" "));
    }
}
