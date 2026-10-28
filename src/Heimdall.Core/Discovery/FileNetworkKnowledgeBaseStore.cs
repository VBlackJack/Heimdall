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

namespace Heimdall.Core.Discovery;

/// <summary>
/// File-backed network knowledge base store using the production
/// <see cref="KnowledgeBaseManager"/> persistence path.
/// </summary>
public sealed class FileNetworkKnowledgeBaseStore : INetworkKnowledgeBaseStore
{
    private readonly string _path;

    /// <summary>
    /// Creates a store over <paramref name="path"/>; the production caller passes
    /// <see cref="KnowledgeBaseManager.ResolveDefaultPath"/>.
    /// </summary>
    public FileNetworkKnowledgeBaseStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public Task<NetworkKnowledgeBase> LoadAsync() => KnowledgeBaseManager.LoadAsync(_path);

    /// <inheritdoc />
    public Task SaveAsync(NetworkKnowledgeBase knowledgeBase) =>
        KnowledgeBaseManager.SaveAsync(knowledgeBase, _path);
}
