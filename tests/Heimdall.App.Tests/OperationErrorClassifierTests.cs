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
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.Ssh;
using Renci.SshNet.Common;

namespace Heimdall.App.Tests;

/// <summary>
/// Tests for <see cref="OperationErrorClassifier"/>: each exception type maps to its stable,
/// language-neutral error category.
/// </summary>
public sealed class OperationErrorClassifierTests
{
    [Fact]
    public void Classify_SftpPermissionDenied_ReturnsPermission()
    {
        OperationErrorClassifier.Classify(new SftpPermissionDeniedException("denied"))
            .Should().Be("permission");
    }

    [Fact]
    public void Classify_UnauthorizedAccess_ReturnsPermission()
    {
        OperationErrorClassifier.Classify(new UnauthorizedAccessException("denied"))
            .Should().Be("permission");
    }

    [Fact]
    public void Classify_HostKeyRejected_ReturnsSecurity()
    {
        var ex = new HostKeyRejectedException(
            "host.example", 22, "ssh-ed25519", "SHA256:presented", "SHA256:stored");

        OperationErrorClassifier.Classify(ex).Should().Be("security");
    }

    [Fact]
    public void Classify_IOException_ReturnsIo()
    {
        OperationErrorClassifier.Classify(new IOException("disk full")).Should().Be("io");
    }

    [Fact]
    public void Classify_UnknownException_ReturnsOther()
    {
        OperationErrorClassifier.Classify(new InvalidOperationException("boom")).Should().Be("other");
    }

    [Fact]
    public void Classify_NullException_Throws()
    {
        Action act = () => OperationErrorClassifier.Classify(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
