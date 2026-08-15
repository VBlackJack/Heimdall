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
/// <c>tests/Directory.Build.props</c> sets <c>ThreadPoolMinThreads</c>. It arrives here only
/// through a generated runtime configuration, which no source file shows, and only while that
/// props file keeps importing its parent: a nested <c>Directory.Build.props</c> REPLACES the one
/// above it instead of merging with it. Drop the import and the floor disappears with nothing in
/// the tree looking any different.
/// <para>
/// So the value is read from the running process rather than from the file that declares it, and
/// the expected number is duplicated below on purpose. A test that read the figure from the same
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
            + "check that the nested Directory.Build.props still imports its parent.");
    }
}
