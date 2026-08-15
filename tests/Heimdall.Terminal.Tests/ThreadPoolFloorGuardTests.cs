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

namespace Heimdall.Terminal.Tests;

/// <summary>
/// Fails when the thread-pool floor declared for the test assemblies does not reach this process.
/// </summary>
/// <remarks>
/// <c>tests/Directory.Build.props</c> declares <c>ThreadPoolMinThreads</c> directly. The SDK
/// carries that property into the generated runtime configuration; this test verifies the
/// effective value in the running process rather than merely rereading its source declaration. A
/// future project-level <c>Directory.Build.props</c> could shadow the tests-level file unless it
/// imports its parent, but no deeper file exists today.
/// <para>
/// The expected number is duplicated below on purpose. A test that read the figure from the same
/// place that supplies it could never notice it failing to arrive.
/// </para>
/// <para>
/// This asserts one fact and nothing else. It measures no queue, attributes no latency and draws
/// no conclusion about how any test is scheduled.
/// </para>
/// </remarks>
public sealed class ThreadPoolFloorGuardTests
{
    /// <summary>
    /// The floor declared by <c>tests/Directory.Build.props</c>, restated here deliberately.
    /// </summary>
    private const int DeclaredMinimumWorkerThreads = 64;

    [Fact]
    public void TheDeclaredThreadPoolFloorReachesThisProcess()
    {
        ThreadPool.GetMinThreads(out int workerThreads, out _);

        Assert.True(
            workerThreads >= DeclaredMinimumWorkerThreads,
            $"tests/Directory.Build.props declares a ThreadPoolMinThreads floor of "
            + $"{DeclaredMinimumWorkerThreads}, but this process runs with {workerThreads} minimum "
            + "worker threads. The property is not reaching the generated runtime configuration; "
            + "inspect the evaluated MSBuild properties and any nearer Directory.Build.props.");
    }
}
