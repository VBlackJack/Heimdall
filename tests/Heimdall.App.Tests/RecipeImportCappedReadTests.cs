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
using System.IO.Pipes;
using Heimdall.App.Services;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>
/// The file picked for import is read in one pass over one handle, stopped at the cap: a file whose size is not
/// known before it is read (here a pipe that keeps writing) is refused after one byte more than the cap.
/// </summary>
public sealed class RecipeImportCappedReadTests
{
    // A few MiB above the cap is enough: a reader that drained the pipe would make the producer write all of it.
    private const int ProducedBytes = 8 * 1024 * 1024;

    private const int ChunkBytes = 64 * 1024;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AFileWhoseSizeIsNotKnownInAdvance_IsRefusedAtTheCap_WithoutBeingReadWhole()
    {
        using RecipeImportHarness harness = await RecipeImportHarness.CreateAsync();
        string name = "heimdall-recipe-import-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource stop = new();
        NamedPipeServerStream server = new(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        long produced = 0;
        Task producer = Task.Run(async () =>
        {
            try
            {
                await server.WaitForConnectionAsync(stop.Token);
                byte[] chunk = new byte[ChunkBytes];
                while (Interlocked.Read(ref produced) < ProducedBytes)
                {
                    await server.WriteAsync(chunk, stop.Token);
                    Interlocked.Add(ref produced, chunk.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The reader stopped reading: that is what the test wants.
            }
        });

        try
        {
            // The blocking read has a thread of its own, so it never holds the worker the producer needs.
            RecipeImportOutcome outcome = await Task.Factory.StartNew(
                () => harness.Import.PrepareFile(@"\\.\pipe\" + name, []),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).WaitAsync(Bound);

            Assert.Equal(RecipeImportFailure.InvalidRecipe, outcome.Failure);
            RecipeError error = Assert.Single(outcome.Errors);
            Assert.Equal(RecipeErrorCode.FileTooLarge, error.Code);
            Assert.Equal(RecipeLimits.MaxFileBytes + 1, error.Actual);
        }
        finally
        {
            await stop.CancelAsync();
            server.Dispose();
            await producer.WaitAsync(Bound);
        }

        // The reader took one byte more than the cap and stopped, so the producer was left far below its maximum.
        long written = Interlocked.Read(ref produced);
        Assert.True(written < ProducedBytes / 4, $"the producer wrote {written} of {ProducedBytes} bytes");
    }
}
