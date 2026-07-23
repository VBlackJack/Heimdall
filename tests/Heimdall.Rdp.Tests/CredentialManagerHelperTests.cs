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

using Heimdall.Rdp;

namespace Heimdall.Rdp.Tests;

public sealed class CredentialManagerHelperTests
{
    [Fact]
    public void DeleteCredential_DomainSucceedsGenericFails_ReturnsFalseAndAttemptsBoth()
    {
        var attemptedTypes = new List<uint>();

        bool result = CredentialManagerHelper.DeleteCredential(
            "TERMSRV/server",
            (_, type) =>
            {
                attemptedTypes.Add(type);
                return type == CredentialManagerHelper.CredTypeDomainPassword
                    ? new CredentialManagerHelper.CredentialDeleteResult(true, 0)
                    : new CredentialManagerHelper.CredentialDeleteResult(false, 5);
            },
            out string? error);

        Assert.False(result);
        Assert.Equal(
            [CredentialManagerHelper.CredTypeDomainPassword, CredentialManagerHelper.CredTypeGeneric],
            attemptedTypes);
        Assert.Equal("GENERIC: WIN32_ERROR_5", error);
    }

    [Fact]
    public void DeleteCredential_DomainFailsGenericIsStillAttempted()
    {
        var attemptedTypes = new List<uint>();

        bool result = CredentialManagerHelper.DeleteCredential(
            "TERMSRV/server",
            (_, type) =>
            {
                attemptedTypes.Add(type);
                return type == CredentialManagerHelper.CredTypeDomainPassword
                    ? new CredentialManagerHelper.CredentialDeleteResult(false, 5)
                    : new CredentialManagerHelper.CredentialDeleteResult(true, 0);
            },
            out string? error);

        Assert.False(result);
        Assert.Equal(
            [CredentialManagerHelper.CredTypeDomainPassword, CredentialManagerHelper.CredTypeGeneric],
            attemptedTypes);
        Assert.Equal("DOMAIN_PASSWORD: WIN32_ERROR_5", error);
    }

    [Fact]
    public void DeleteCredential_BothTypesNotFound_ReturnsTrue()
    {
        var attemptedTypes = new List<uint>();

        bool result = CredentialManagerHelper.DeleteCredential(
            "TERMSRV/server",
            (_, type) =>
            {
                attemptedTypes.Add(type);
                return new CredentialManagerHelper.CredentialDeleteResult(
                    false,
                    CredentialManagerHelper.ErrorNotFound);
            },
            out string? error);

        Assert.True(result);
        Assert.Null(error);
        Assert.Equal(
            [CredentialManagerHelper.CredTypeDomainPassword, CredentialManagerHelper.CredTypeGeneric],
            attemptedTypes);
    }
}
