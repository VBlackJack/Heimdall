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

using FluentFTP.Exceptions;
using Heimdall.Sftp;
using Renci.SshNet.Common;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Only a typed absence may be read as "no conflicts"; every other listing failure used to be
/// swallowed the same way and sent the batch as replacements on a network hiccup.
/// </summary>
public sealed class RemotePathAbsenceTests
{
    [Fact]
    public void SftpPathNotFound_IsAbsence()
    {
        Assert.True(RemotePathAbsence.IsPathNotFound(new SftpPathNotFoundException("No such file")));
    }

    [Fact]
    public void FtpFileUnavailableReply_IsAbsence()
    {
        Assert.True(RemotePathAbsence.IsPathNotFound(new FtpCommandException("550", "No such directory")));
    }

    [Fact]
    public void OtherFtpReply_IsNotAbsence()
    {
        Assert.False(RemotePathAbsence.IsPathNotFound(new FtpCommandException("421", "Service not available")));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(InvalidOperationException))]
    public void TransientFailure_IsNotAbsence(Type exceptionType)
    {
        Exception exception = (Exception)Activator.CreateInstance(exceptionType, "transient")!;

        Assert.False(RemotePathAbsence.IsPathNotFound(exception));
    }
}
