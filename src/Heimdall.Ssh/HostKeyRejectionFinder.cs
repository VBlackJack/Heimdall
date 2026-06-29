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

using System.Diagnostics.CodeAnalysis;

namespace Heimdall.Ssh;

internal static class HostKeyRejectionFinder
{
    internal static bool TryFind(
        Exception exception,
        [NotNullWhen(true)] out HostKeyRejectedException? hostKeyRejected)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Exception? current = exception;
        while (current is not null)
        {
            if (current is HostKeyRejectedException found)
            {
                hostKeyRejected = found;
                return true;
            }

            current = current.InnerException;
        }

        hostKeyRejected = null;
        return false;
    }
}
