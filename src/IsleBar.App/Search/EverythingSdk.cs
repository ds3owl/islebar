using System.Runtime.InteropServices;
using IsleBar.App.Interop;

namespace IsleBar.App.Search;

/// <summary>One file search result row.</summary>
/// <param name="Name">File or folder name.</param>
/// <param name="Directory">Containing folder.</param>
/// <param name="IsFolder">Whether it is a folder.</param>
internal sealed record FileHit(string Name, string Directory, bool IsFolder)
{
    public string FullPath => Path.Combine(Directory, Name);
}

/// <summary>Why search is unavailable (the message text is picked from the language file).</summary>
internal enum SearchProblem
{
    None,

    /// <summary>Everything64.dll is missing → <c>NoSdk</c>.</summary>
    SdkMissing,

    /// <summary>Everything is not running → <c>EsOff</c>.</summary>
    ServiceOff,

    /// <summary>Any other failure → <c>EsFail</c>.</summary>
    Failed,
}

/// <summary>
/// Everything SDK wrapper.
///
/// <b>The SDK must be called from a single thread only.</b> So <see cref="FileSearchWorker"/>
/// owns this class exclusively on a dedicated thread. Do not call it directly from the UI thread.
///
/// <b>This file has never been built. Needs phase-0 verification on PC.</b>
/// </summary>
internal sealed class EverythingSdk : IDisposable
{
    /// <summary>Number of results shown at once (the list under the search box is small).</summary>
    public const int MaxResults = 8;
    private const uint SortDateModifiedDescending = 14;

    private const int EverythingErrorIpc = 2;   // Everything is not running

    private readonly IntPtr _library;

    private EverythingSdk(IntPtr library) => _library = library;

    /// <summary>Loads the DLL. Null if missing (file mode only shows a hint).</summary>
    public static EverythingSdk? TryLoad()
    {
        var path = AppPaths.EverythingDll;
        return NativeLibrary.TryLoad(path, out var handle) ? new EverythingSdk(handle) : null;
    }

    /// <summary>Searches. On failure <paramref name="problem"/> holds the reason and an empty list is returned.</summary>
    public IReadOnlyList<FileHit> Search(string query, out SearchProblem problem)
    {
        problem = SearchProblem.None;
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        Everything_SetSearchW(query);
        Everything_SetMax(MaxResults);
        Everything_SetSort(SortDateModifiedDescending);   // most recently modified files on top (same as the Python version)
        Everything_SetRequestFlags(RequestFileName | RequestPath);

        if (!Everything_QueryW(wait: true))
        {
            problem = Everything_GetLastError() == EverythingErrorIpc
                ? SearchProblem.ServiceOff
                : SearchProblem.Failed;
            return [];
        }

        var count = (int)Everything_GetNumResults();
        var hits = new List<FileHit>(count);
        for (var i = 0u; i < count; i++)
        {
            var name = Marshal.PtrToStringUni(Everything_GetResultFileNameW(i));
            var dir = Marshal.PtrToStringUni(Everything_GetResultPathW(i));
            if (name is not null && dir is not null)
            {
                hits.Add(new FileHit(name, dir, Everything_IsFolderResult(i)));
            }
        }

        return hits;
    }

    public void Dispose()
    {
        Everything_Reset();
        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
        }
    }

    private const uint RequestFileName = 0x0000_0001;
    private const uint RequestPath = 0x0000_0002;

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern void Everything_SetSearchW(string search);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetMax(uint max);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetSort(uint sort);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetRequestFlags(uint flags);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Everything_QueryW([MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("Everything64.dll")]
    private static extern uint Everything_GetNumResults();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr Everything_GetResultFileNameW(uint index);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr Everything_GetResultPathW(uint index);

    [DllImport("Everything64.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Everything_IsFolderResult(uint index);

    [DllImport("Everything64.dll")]
    private static extern uint Everything_GetLastError();

    [DllImport("Everything64.dll")]
    private static extern void Everything_Reset();
}
