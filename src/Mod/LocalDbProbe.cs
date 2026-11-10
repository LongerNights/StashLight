using System.Diagnostics;
using FrooxEngine;

namespace StashLight;

/// <summary>
/// Diagnostic: the engine's local database (LiteDB) serves its cached-records, asset-lookup and variable queries through ONE
/// queue. If anything floods that queue, asset loads wait behind it while the frame rate stays fine. This times a trivial
/// call every 2 s for the first 4 minutes and logs any spell where it was slow, so a clogged queue is visible in the log
/// (with a timeline next to our own phases) instead of guessed at. It reads one count, so it adds almost no load itself.
/// </summary>
internal static class LocalDbProbe
{
    private const int SlowMs = 300;
    private static int _active;

    /// <summary>Watches the database queue for <paramref name="minutes"/>. Only one watch runs at a time.</summary>
    public static void Start(int minutes = 4, string label = "start-up")
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) return;
        _ = Task.Run(() => RunAsync(minutes, label));
    }

    private static async Task RunAsync(int minutes, string label)
    {
        try
        {
            var db = Engine.Current.LocalDB;
            var clock = Stopwatch.StartNew();
            int samples = 0, slow = 0; long max = 0, slowTotal = 0;
            while (clock.Elapsed < TimeSpan.FromMinutes(minutes))
            {
                var sw = Stopwatch.StartNew();
                try { await db.GetRecordCount().ConfigureAwait(false); } catch { /* a failed probe is just skipped */ }
                long ms = sw.ElapsedMilliseconds;
                samples++;
                if (ms > max) max = ms;
                if (ms >= SlowMs)
                {
                    slow++; slowTotal += ms;
                    IndexService.Note($"LocalDB probe ({label}): a trivial query waited {ms} ms at +{clock.Elapsed.TotalSeconds:F0}s (phase: {IndexService.Phase})");
                }
                await Task.Delay(2000).ConfigureAwait(false);
            }
            IndexService.Note($"LocalDB probe summary ({label}, {minutes} min): {samples} samples, max {max} ms, {slow} slow (>= {SlowMs} ms), {slowTotal} ms total waited");
        }
        catch (Exception ex) { IndexService.Note("LocalDB probe stopped: " + ex.Message); }
        finally { Interlocked.Exchange(ref _active, 0); }
    }
}
