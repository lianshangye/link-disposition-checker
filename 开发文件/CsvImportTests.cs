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
            bool valid = jobs.Count == 723 && jobs.Count(job => job.Url.Contains(",")) == 3 &&
                jobs[0].Url == "http://xueqiu.com/9632298307/400108447" && jobs[0].ExpectedTitle == "崩盘了" &&
                jobs[0].ExpectedAuthor == "投资之道在于懒" &&
                jobs[0].Platform == "雪球网" && jobs[0].ContentType == "帖子" &&
                jobs[0].SourceSheet == "CSV" && jobs[0].SourceRow == 2 && restoredJobs.Count == jobs.Count &&
                restoredJobs[47].Url == "http://guba.eastmoney.com/news,cfhpl,1743532656.html";
            Console.WriteLine("CSV jobs=" + jobs.Count + ", legitimate comma URLs=" + jobs.Count(job => job.Url.Contains(",")) +
                ", first=" + (jobs.Count == 0 ? "" : jobs[0].Url) + ", title=" + (jobs.Count == 0 ? "" : jobs[0].ExpectedTitle) +
                ", author=" + (jobs.Count == 0 ? "" : jobs[0].ExpectedAuthor));
            return valid ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.GetType().FullName + ": " + ex.Message);
            return 3;
        }
    }
}
