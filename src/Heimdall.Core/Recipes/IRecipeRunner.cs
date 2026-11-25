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

namespace Heimdall.Core.Recipes;

/// <summary>
/// Runs one resolved program with an already expanded argument list. The seam exists so that a
/// view model can be tested with a fake: no test starts a real process.
/// </summary>
public interface IRecipeRunner
{
    /// <summary>Runs a program; see <see cref="RecipeRunner.RunAsync"/>.</summary>
    /// <param name="recipe">The recipe: output encoding, standard error mode, success exit codes and timeout.</param>
    /// <param name="programPath">The absolute path of the program.</param>
    /// <param name="arguments">The output of <see cref="RecipeExpander.Expand"/>, one entry per argument.</param>
    /// <param name="workingDirectory">An existing empty folder owned by the caller.</param>
    /// <param name="cancellationToken">Cancels the run; the process tree is killed.</param>
    Task<RecipeRunResult> RunAsync(
        Recipe recipe,
        string programPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
