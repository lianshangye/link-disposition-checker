using System;
using System.IO;
using System.Linq;
using LinkDispositionChecker;

internal static class CsvImportTests
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) return 2;
        try
        {
            Console.WriteLine("CSV test input=" + args[0] + ", exists=" + File.Exists(args[0]));
            var jobs = MainForm.LoadCsvJobs(args[0]);
            var restoredJobs = MainForm.LoadCsvJobsFromContent(File.ReadAllText(args[0]), "CSV");
            bool valid = jobs.Count > 0 && restoredJobs.Count == jobs.Count &&
                jobs.All(job => !String.IsNullOrWhiteSpace(job.Url) &&
                    (job.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || job.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                    !String.IsNullOrWhiteSpace(job.SourceSheet) && job.SourceRow >= 2) &&
                jobs.Zip(restoredJobs, (left, right) => left.Url == right.Url && left.SourceRow == right.SourceRow &&
                    left.ExpectedTitle == right.ExpectedTitle && left.ExpectedAuthor == right.ExpectedAuthor &&
                    left.Platform == right.Platform).All(match => match);
            Console.WriteLine("CSV jobs=" + jobs.Count + ", legitimate comma URLs=" + jobs.Count(job => job.Url.Contains(",")) +
                ", first=" + (jobs.Count == 0 ? "" : jobs[0].Url) + ", title=" + (jobs.Count == 0 ? "" : jobs[0].ExpectedTitle) +
                ", author=" + (jobs.Count == 0 ? "" : jobs[0].ExpectedAuthor));
            string noBomPath = Path.Combine(Path.GetTempPath(), "LinkCheckerUtf8NoBom_" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                File.WriteAllText(noBomPath, "链接,标题,平台\r\nhttps://example.com/a,中文标题,微博\r\n", new System.Text.UTF8Encoding(false));
                var noBomJobs = MainForm.LoadCsvJobs(noBomPath);
                valid = valid && noBomJobs.Count == 1 && noBomJobs[0].ExpectedTitle == "中文标题" && noBomJobs[0].Platform == "微博" &&
                    noBomJobs[0].SourceSheet == "CSV" && noBomJobs[0].SourceRow == 2;
                Console.WriteLine((noBomJobs.Count == 1 ? "PASS" : "FAIL") + " UTF-8 no-BOM CSV");
                File.WriteAllText(noBomPath,
                    "链接,标题,平台,来源工作表,来源行号\r\nhttps://example.com/b,第二条,网媒,原始数据,45934\r\n",
                    new System.Text.UTF8Encoding(false));
                var sourceJobs = MainForm.LoadCsvJobs(noBomPath);
                bool sourceIdentityPassed = sourceJobs.Count == 1 && sourceJobs[0].SourceSheet == "原始数据" && sourceJobs[0].SourceRow == 45934;
                valid = valid && sourceIdentityPassed;
                Console.WriteLine((sourceIdentityPassed ? "PASS" : "FAIL") + " CSV来源工作表和行号");
            }
            finally { try { File.Delete(noBomPath); } catch { } }
            return valid ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.GetType().FullName + ": " + ex.Message);
            return 3;
        }
    }
}
