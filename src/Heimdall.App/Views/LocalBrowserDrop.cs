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

using DataFormats = System.Windows.DataFormats;
using IDataObject = System.Windows.IDataObject;

namespace Heimdall.App.Views;

/// <summary>What a drop on the local file list does.</summary>
internal enum LocalBrowserDropKind
{
    /// <summary>Nothing: the data holds nothing this list takes, or it came from this list.</summary>
    None,

    /// <summary>Entries dragged out of a remote list, downloaded into the target folder.</summary>
    RemoteDownload,

    /// <summary>Local files dragged from Explorer or another local list, copied into the target folder.</summary>
    LocalCopy,
}

/// <summary>
/// Decides what a drop on the local file list does. The list drags its own selection out as
/// real files, so without the origin check a drag released over the list itself (a short drag,
/// a change of mind) would copy the files onto themselves or into the folder under the pointer.
/// </summary>
internal static class LocalBrowserDrop
{
    /// <summary>The format a local list stamps on its own drags, holding that list's identity.</summary>
    internal const string OriginFormat = "Heimdall.LocalFileBrowser.DragOrigin";

    /// <summary>Classifies dropped data for the list identified by <paramref name="listId"/>.</summary>
    /// <param name="data">The dropped data, or <see langword="null"/>.</param>
    /// <param name="listId">The identity of the list receiving the drop.</param>
    internal static LocalBrowserDropKind Classify(IDataObject? data, string listId)
    {
        if (data is null)
        {
            return LocalBrowserDropKind.None;
        }

        if (SftpRemoteDragPayload.From(data) is not null)
        {
            return LocalBrowserDropKind.RemoteDownload;
        }

        if (data.GetDataPresent(OriginFormat) && data.GetData(OriginFormat) as string == listId)
        {
            return LocalBrowserDropKind.None;
        }

        return data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 }
            ? LocalBrowserDropKind.LocalCopy
            : LocalBrowserDropKind.None;
    }
}
