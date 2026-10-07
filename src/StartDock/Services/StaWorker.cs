using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace StartDock.Services
{
    /// <summary>
    /// Runs work on background threads set up the way the Windows shell expects
    /// (single-threaded apartment, "STA") — for icon extraction.
    ///
    /// Why: asking the shell for an icon (SHGetFileInfo, especially for installed
    /// apps' "shell:AppsFolder\…" entries) from an ordinary thread-pool thread
    /// sometimes gets Windows' blank "generic file" icon instead of the real one,
    /// because the shell's icon handlers need an STA thread. The UI thread is one,
    /// which is why a pinned tile's Refresh icon worked while the same app in
    /// search results kept its blank page. Every background extraction goes
    /// through here now, so search, Recently added and the Add window get the
    /// real icons too.
    /// </summary>
    public static class StaWorker
    {
        private const int ThreadCount = 2;

        private static readonly BlockingCollection<Action> Queue = new();
        private static int _started;

        /// <summary>Runs <paramref name="work"/> on one of the STA threads.</summary>
        public static Task<T> Run<T>(Func<T> work)
        {
            EnsureStarted();
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Add(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        private static void EnsureStarted()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
                return;
            for (int i = 0; i < ThreadCount; i++)
            {
                var thread = new Thread(() =>
                {
                    foreach (var job in Queue.GetConsumingEnumerable())
                        job();
                })
                {
                    IsBackground = true,
                    Name = "StartDock shell worker",
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
        }
    }
}
