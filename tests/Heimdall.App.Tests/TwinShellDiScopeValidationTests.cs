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

using Heimdall.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes the TwinShell DI registration convention: no singleton may capture a
/// scoped service (captive dependency). This guards the Command Library / Git
/// sync graph — notably that <c>GitSyncService</c> (singleton) resolves the
/// scoped <c>ISyncService</c> per operation rather than holding it in a field,
/// which would pin a single <c>DbContext</c> for the whole application lifetime.
/// </summary>
public sealed class TwinShellDiScopeValidationTests
{
    [Fact]
    public void RegisterServices_BuildsWithScopeValidation_WithoutCaptiveDependencies()
    {
        var services = new ServiceCollection();
        TwinShellBootstrapper.RegisterServices(services);

        // ValidateOnBuild fails fast if any singleton consumes a scoped service.
        var exception = Record.Exception(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
        });

        Assert.Null(exception);
    }
}
