using System.Collections.Concurrent;

namespace IsleBar.App.Search;

/// <summary>
/// Calls the Everything SDK only from <b>one dedicated thread</b> (SDK constraint).
/// Of queued requests only the last is processed, and results for stale input are filtered out by number —
/// this stops late results from overwriting newer ones when typing fast.
///
/// <b>This file has never been built. Needs phase-0 verification on PC.</b>
/// </summary>
internal sealed class FileSearchWorker : IDisposable
{
    private readonly BlockingCollection<(long Seq, string Query)> _requests = new(new ConcurrentQueue<(long, string)>());
    private readonly Action<long, IReadOnlyList<FileHit>, SearchProblem> _onResult;
    private readonly Thread _thread;
    private long _seq;

    /// <param name="onResult">Delivers results. <b>Marshalling to the UI thread is the caller's responsibility</b>.</param>
    public FileSearchWorker(Action<long, IReadOnlyList<FileHit>, SearchProblem> onResult)
    {
        _onResult = onResult;
        _thread = new Thread(Loop) { IsBackground = true, Name = "IsleBar Everything" };
        _thread.Start();
    }

    /// <summary>Requests a search. Returns a request number (used to match results).</summary>
    public long Request(string query)
    {
        var seq = Interlocked.Increment(ref _seq);
        _requests.Add((seq, query));
        return seq;
    }

    /// <summary>Most recently requested number.</summary>
    public long LatestSeq => Interlocked.Read(ref _seq);

    private void Loop()
    {
        using var sdk = EverythingSdk.TryLoad();
        foreach (var item in _requests.GetConsumingEnumerable())
        {
            var current = item;

            // Drop queued requests and handle only the last one
            while (_requests.TryTake(out var newer))
            {
                current = newer;
            }

            if (sdk is null)
            {
                _onResult(current.Seq, [], SearchProblem.SdkMissing);
                continue;
            }

            try
            {
                var hits = sdk.Search(current.Query, out var problem);
                _onResult(current.Seq, hits, problem);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _onResult(current.Seq, [], SearchProblem.SdkMissing);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.SEHException or InvalidOperationException)
            {
                _onResult(current.Seq, [], SearchProblem.Failed);
            }
        }
    }

    public void Dispose()
    {
        _requests.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _requests.Dispose();
    }
}
