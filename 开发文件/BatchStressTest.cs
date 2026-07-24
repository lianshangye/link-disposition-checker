using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinkDispositionChecker
{
    internal static class BatchStressTest
    {
        public static void Main()
        {
            RunAsync().GetAwaiter().GetResult();
        }

        private static async Task RunAsync()
        {
            const int total = 50000;
            const int workers = 10;
            const string prefix = "http://127.0.0.1:18765/";
            string backupSession = SessionStore.SessionPath + ".stress-backup";
            string backupJournal = SessionStore.JournalPath + ".stress-backup";
            Backup(SessionStore.SessionPath, backupSession);
            Backup(SessionStore.JournalPath, backupJournal);

            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            var serverCancellation = new CancellationTokenSource();
            Task server = ServeAsync(listener, serverCancellation.Token);
            try
            {
                string input = String.Join(Environment.NewLine, Enumerable.Range(1, total).Select(index => prefix + "item/" + index));
                SessionStore.Save(input, "", new CheckJob[0], new CheckResult[0]);

                var checker = new Checker();
                var results = new CheckResult[total];
                int next = -1;
                var watch = Stopwatch.StartNew();
                Task[] tasks = Enumerable.Range(0, workers).Select(async worker =>
                {
                    var batch = new List<CheckResult>(500);
                    while (true)
                    {
                        int index = Interlocked.Increment(ref next);
                        if (index >= total) break;
                        CheckResult result = await checker.CheckAsync(prefix + "item/" + (index + 1), index + 1,
                            "Stress Item", "", "", "本地压力测试", "文章", false, CancellationToken.None);
                        results[index] = result;
                        batch.Add(result);
                        if (batch.Count >= 500)
                        {
                            SessionStore.AppendBatch(batch);
                            batch.Clear();
                        }
                    }
                    SessionStore.AppendBatch(batch);
                }).ToArray();
                await Task.WhenAll(tasks);
                watch.Stop();

                var loadWatch = Stopwatch.StartNew();
                CheckSession loaded = SessionStore.Load();
                loadWatch.Stop();
                var compactWatch = Stopwatch.StartNew();
                SessionStore.Save(input, "", new CheckJob[0], loaded.Results);
                compactWatch.Stop();

                int valid = results.Count(item => item != null && item.Verdict == "仍可访问");
                int missing = results.Count(item => item == null);
                Console.WriteLine("TOTAL=" + total);
                Console.WriteLine("VALID=" + valid);
                Console.WriteLine("MISSING_RESULTS=" + missing);
                Console.WriteLine("REQUEST_SECONDS=" + watch.Elapsed.TotalSeconds.ToString("0.00"));
                Console.WriteLine("REQUESTS_PER_SECOND=" + (total / watch.Elapsed.TotalSeconds).ToString("0.0"));
                Console.WriteLine("LOAD_RESULTS=" + loaded.Results.Count);
                Console.WriteLine("LOAD_SECONDS=" + loadWatch.Elapsed.TotalSeconds.ToString("0.00"));
                Console.WriteLine("COMPACT_SECONDS=" + compactWatch.Elapsed.TotalSeconds.ToString("0.00"));
                Console.WriteLine("SESSION_BYTES=" + new FileInfo(SessionStore.SessionPath).Length);
                Console.WriteLine("PRIVATE_MEMORY_MB=" + (Process.GetCurrentProcess().PrivateMemorySize64 / 1024d / 1024d).ToString("0.0"));
                if (missing != 0 || loaded.Results.Count != total) Environment.ExitCode = 2;
            }
            finally
            {
                serverCancellation.Cancel();
                listener.Stop();
                try { server.Wait(2000); } catch { }
                Delete(SessionStore.SessionPath);
                Delete(SessionStore.JournalPath);
                Restore(backupSession, SessionStore.SessionPath);
                Restore(backupJournal, SessionStore.JournalPath);
            }
        }

        private static async Task ServeAsync(HttpListener listener, CancellationToken token)
        {
            byte[] body = Encoding.UTF8.GetBytes("<html><head><title>Stress Item</title></head><body>Stress Item batch verification content</body></html>");
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { break; }
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, 0, body.Length);
                context.Response.Close();
            }
        }

        private static void Backup(string source, string backup)
        {
            Delete(backup);
            if (File.Exists(source)) File.Move(source, backup);
        }

        private static void Restore(string backup, string target)
        {
            if (File.Exists(backup)) File.Move(backup, target);
        }

        private static void Delete(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
