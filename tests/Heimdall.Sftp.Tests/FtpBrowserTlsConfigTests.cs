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

using FluentFTP;
using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The TLS options the FTPS connection is actually configured with.
/// </summary>
/// <remarks>
/// These assert the <see cref="FtpConfig"/> that <c>CreateConfig</c> really returns, not a
/// hand-built chain status. The distinction is the whole point: the pinned-certificate guard
/// already refuses <c>X509ChainStatusFlags.Revoked</c>, so an oracle feeding it a fabricated
/// status would pass while production never produced that flag - FluentFTP leaves the chain on
/// <c>X509RevocationMode.NoCheck</c> unless revocation validation is switched on here.
/// </remarks>
public sealed class FtpBrowserTlsConfigTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateConfig_WithEncryption_EnablesRevocationChecking(bool passiveMode)
    {
        FtpConfig config = FtpBrowser.CreateConfig(passiveMode, useSsl: true);

        Assert.True(config.ValidateCertificateRevocation);

        // Non-vacuity: the connection really is the encrypted one whose chain we care about.
        Assert.Equal(FtpEncryptionMode.Explicit, config.EncryptionMode);
        Assert.True(config.DataConnectionEncryption);
    }

    /// <summary>
    /// A plaintext connection builds no TLS chain, so requesting revocation checks there would
    /// configure a check that never runs. This is the control that keeps the assertion above from
    /// being satisfied by an unconditional assignment.
    /// </summary>
    [Fact]
    public void CreateConfig_WithoutEncryption_LeavesRevocationCheckingOff()
    {
        FtpConfig config = FtpBrowser.CreateConfig(passiveMode: true, useSsl: false);

        Assert.False(config.ValidateCertificateRevocation);
        Assert.Equal(FtpEncryptionMode.None, config.EncryptionMode);
        Assert.False(config.DataConnectionEncryption);
    }
}
