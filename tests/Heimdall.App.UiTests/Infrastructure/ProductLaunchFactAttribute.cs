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

namespace Heimdall.App.UiTests.Infrastructure;

/// <summary>
/// A test that launches the real Heimdall executable, skipped unless the run opts in.
/// </summary>
/// <remarks>
/// The executable resolves its data root from the user's local application data, which a test
/// cannot redirect. On a developer machine that is the developer's own profile: the launch
/// writes its log there and starts the scheduled task engine, whose first tick runs any task
/// that is overdue. CI opts in, its profile being thrown away with the runner.
/// </remarks>
public sealed class ProductLaunchFactAttribute : StaFactAttribute
{
    /// <summary>Set to <see cref="OptInValue"/> to run the tests that launch the product.</summary>
    public const string OptInEnvironmentVariable = "HEIMDALL_UITESTS_LAUNCH_PRODUCT";

    /// <summary>The only value that opts in.</summary>
    public const string OptInValue = "1";

    public ProductLaunchFactAttribute()
    {
        if (!IsOptedIn(Environment.GetEnvironmentVariable(OptInEnvironmentVariable)))
        {
            Skip = $"Launches the real Heimdall against this user's profile. Set {OptInEnvironmentVariable}={OptInValue} to run it.";
        }
    }

    internal static bool IsOptedIn(string? value) => string.Equals(value, OptInValue, StringComparison.Ordinal);
}
