using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using LinkDispositionChecker;

internal static class DeepAuditRunner
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 2) return 2;
        try
        {
            List<CheckJob> jobs = MainForm.LoadCsvJobs(args[0]);
            string numberFilter = Environment.GetEnvironmentVariable("DEEP_AUDIT_NUMBERS");
            if (!String.IsNullOrWhiteSpace(numberFilter))
            {
                var selected = new HashSet<int>(numberFilter.Split(',').Select(value =>
                {
                    int number;
                    return Int32.TryParse(value.Trim(), out number) ? number : -1;
                }).Where(number => number > 0));
                jobs = jobs.Where(job => selected.Contains(job.Number)).ToList();
            }

            var results = jobs.Select(job => new CheckResult
            {
                Number = job.Number,
                Verdict = "人工复核",
                StatusCode = "浏览器待核验",
                OriginalUrl = job.Url,
                FinalUrl = job.Url,
                ExpectedTitle = job.ExpectedTitle ?? "",
                ExpectedExcerpt = job.ExpectedExcerpt ?? "",
                ExpectedAuthor = job.ExpectedAuthor ?? "",
                Platform = job.Platform ?? "",
                ContentType = String.IsNullOrWhiteSpace(job.ContentType)
                    ? Checker.InferContentType(job.Platform, job.Url, job.ExpectedTitle) : job.ContentType,
                SourceSheet = job.SourceSheet,
                SourceRow = job.SourceRow
            }).ToList();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new DeepReviewForm(results, item =>
            {
                Console.WriteLine("DEEP_PROGRESS=" + item.Number + ",VERDICT=" + item.Verdict +
                    ",STATUS=" + item.StatusCode + ",PLATFORM=" + item.Platform);
            }, false, true))
            {
                form.ShowDialog();
            }

            using (var writer = new StreamWriter(args[1], false, new UTF8Encoding(true)))
            {
                writer.WriteLine("序号,核验结果,HTTP状态,平台,内容类型,发文作者,页面标题,原链接,最终地址,判定依据,核验时间");
                foreach (CheckResult result in results.OrderBy(item => item.Number))
                    writer.WriteLine(String.Join(",", new[]
                    {
                        result.Number.ToString(), Csv(result.Verdict), Csv(result.StatusCode), Csv(result.Platform),
                        Csv(result.ContentType), Csv(result.ExpectedAuthor), Csv(result.Title), Csv(result.OriginalUrl),
                        Csv(result.FinalUrl), Csv(result.Evidence), Csv(result.CheckedAt)
                    }));
            }
            int removed = results.Count(item => item.Verdict == "已失效");
            int alive = results.Count(item => item.Verdict == "仍可访问");
            Console.WriteLine("DEEP_TOTAL=" + results.Count);
            Console.WriteLine("DEEP_REMOVED=" + removed);
            Console.WriteLine("DEEP_ALIVE=" + alive);
            Console.WriteLine("DEEP_UNRESOLVED=" + (results.Count - removed - alive));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static string Csv(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }
}
