using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LinkDispositionChecker;

internal static class FastAuditRunner
{
    private static string Csv(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    public static int Main(string[] args)
    {
        if (args.Length < 2) return 2;
        return Run(args[0], args[1]).GetAwaiter().GetResult();
    }

    private static async Task<int> Run(string input, string output)
    {
        List<CheckJob> jobs = MainForm.LoadCsvJobs(input);
        string numberFilter = Environment.GetEnvironmentVariable("FAST_AUDIT_NUMBERS");
        if (!String.IsNullOrWhiteSpace(numberFilter))
        {
            var selected = new HashSet<int>(numberFilter.Split(',').Select(value =>
            {
                int number;
                return Int32.TryParse(value.Trim(), out number) ? number : -1;
            }).Where(number => number > 0));
            jobs = jobs.Where(job => selected.Contains(job.Number)).ToList();
        }
        var results = new ConcurrentBag<CheckResult>();
        int next = -1;
        int complete = 0;
        int workers = Math.Min(12, Math.Max(1, jobs.Count));
        var checker = new Checker(900000);
        var tasks = Enumerable.Range(0, workers).Select(async ignored =>
        {
            while (true)
            {
                int index = Interlocked.Increment(ref next);
                if (index >= jobs.Count) break;
                CheckJob job = jobs[index];
                CheckResult result = await checker.CheckAsync(job.Url, job.Number, job.ExpectedTitle, job.ExpectedExcerpt, job.ExpectedAuthor, job.Platform, job.ContentType, false, CancellationToken.None);
                result.Verdict = Checker.NormalizeVisibleVerdict(result.Verdict);
                result.SourceSheet = job.SourceSheet;
                result.SourceRow = job.SourceRow;
                results.Add(result);
                int done = Interlocked.Increment(ref complete);
                if (done % 50 == 0 || done == jobs.Count) Console.WriteLine(done + "/" + jobs.Count);
            }
        }).ToArray();
        await Task.WhenAll(tasks);

        using (var writer = new StreamWriter(output, false, new UTF8Encoding(true)))
        {
            writer.WriteLine("序号,核验结果,HTTP状态,平台,内容类型,发文作者,页面标题,原链接,最终地址,判定依据,耗时");
            foreach (CheckResult result in results.OrderBy(item => item.Number))
                writer.WriteLine(String.Join(",", new[] { result.Number.ToString(), Csv(result.Verdict), Csv(result.StatusCode), Csv(result.Platform), Csv(result.ContentType), Csv(result.ExpectedAuthor), Csv(result.Title),
                    Csv(result.OriginalUrl), Csv(result.FinalUrl), Csv(result.Evidence), Csv(result.Duration) }));
        }
        Console.WriteLine("removed=" + results.Count(item => item.Verdict == "已失效") +
            ", alive=" + results.Count(item => item.Verdict == "仍可访问") +
            ", review=" + results.Count(item => item.Verdict != "已失效" && item.Verdict != "仍可访问"));
        return results.Count == jobs.Count ? 0 : 1;
    }
}
