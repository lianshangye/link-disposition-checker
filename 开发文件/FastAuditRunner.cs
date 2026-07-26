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

    private static async Task<CheckResult> CheckOne(
        Checker checker,
        CheckJob job,
        PlatformRestrictionController restrictions)
    {
        CheckResult result;
        if (restrictions.IsPaused(job))
        {
            result = MainForm.CreateInfrastructureDeferredResult(
                job,
                PlatformRestrictionController.DisplayLabel(job, BatchPreflightPlanner.PlatformKey(job)),
                restrictions.IsPubliclyUnavailable(job));
        }
        else
        {
            result = await checker.CheckAsync(
                job.Url,
                job.Number,
                job.ExpectedTitle,
                job.ExpectedExcerpt,
                job.ExpectedAuthor,
                job.Platform,
                job.ContentType,
                false,
                CancellationToken.None);
            if (NetworkRestrictionCircuitBreaker.IsTransientRestriction(result))
                result = await checker.EscalateEvidenceAsync(result, CancellationToken.None);
            string pausedPlatform;
            restrictions.Observe(job, result, out pausedPlatform);
        }

        result.Verdict = Checker.NormalizeVisibleVerdict(result.Verdict);
        result.SourceSheet = job.SourceSheet;
        result.SourceRow = job.SourceRow;
        result.InfrastructureKey = job.InfrastructureKey;
        return result;
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

        if (jobs.Count == 0)
        {
            Console.Error.WriteLine("没有从输入文件中读取到有效链接。");
            return 3;
        }

        string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!String.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);

        Console.WriteLine("INPUT_JOBS=" + jobs.Count);
        Console.WriteLine("PHASE=register-infrastructure");
        Dictionary<string, int> infrastructures =
            await Checker.RegisterInfrastructureAsync(jobs, CancellationToken.None);
        Console.WriteLine("INFRASTRUCTURES=" + infrastructures.Count);
        Console.WriteLine("SHARED_INFRASTRUCTURES=" + infrastructures.Count(item => item.Value > 1));

        var results = new ConcurrentBag<CheckResult>();
        var restrictions = new PlatformRestrictionController(3);
        var checker = new Checker(900000);
        int complete = 0;

        // 与桌面版一致，先对分散基础设施进行小规模预检，再进入并发核验。
        List<CheckJob> samples = jobs.Count >= 20
            ? BatchPreflightPlanner.SelectSamples(jobs, 8, 2)
            : new List<CheckJob>();
        var sampledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observations = new List<KeyValuePair<CheckJob, CheckResult>>();
        foreach (CheckJob job in samples)
        {
            CheckResult result = await CheckOne(checker, job, restrictions);
            results.Add(result);
            sampledKeys.Add(job.Key);
            observations.Add(new KeyValuePair<CheckJob, CheckResult>(job, result));
            int done = Interlocked.Increment(ref complete);
            Console.WriteLine("PREFLIGHT=" + done + "/" + jobs.Count + ",VERDICT=" + result.Verdict +
                ",STATUS=" + result.StatusCode + ",INFRA=" + result.InfrastructureKey);
            if (BatchPreflightPlanner.Analyze(observations).RequiresDecision) break;
        }

        List<CheckJob> pending = jobs.Where(job => !sampledKeys.Contains(job.Key)).ToList();
        int configuredWorkers;
        if (!Int32.TryParse(Environment.GetEnvironmentVariable("FAST_AUDIT_WORKERS"), out configuredWorkers))
            configuredWorkers = 6;
        int workerCount = Math.Min(Math.Max(1, configuredWorkers), Math.Max(1, pending.Count));
        Console.WriteLine("PHASE=check,WORKERS=" + workerCount + ",PENDING=" + pending.Count);

        int next = -1;
        var tasks = Enumerable.Range(0, workerCount).Select(async ignored =>
        {
            while (true)
            {
                int index = Interlocked.Increment(ref next);
                if (index >= pending.Count) break;
                CheckResult result = await CheckOne(checker, pending[index], restrictions);
                results.Add(result);
                int done = Interlocked.Increment(ref complete);
                if (done % 25 == 0 || done == jobs.Count)
                    Console.WriteLine("PROGRESS=" + done + "/" + jobs.Count);
            }
        }).ToArray();
        await Task.WhenAll(tasks);

        List<CheckResult> ordered = results.OrderBy(item => item.Number).ToList();
        using (var writer = new StreamWriter(output, false, new UTF8Encoding(true)))
        {
            writer.WriteLine("序号,核验结果,AI判断,AI置信度,AI模型,HTTP状态,平台,内容类型,发文作者,页面标题,原链接,最终地址,判定依据,追证阶段,取证线路,站点对照,基础设施,核验时间,耗时");
            foreach (CheckResult result in ordered)
                writer.WriteLine(String.Join(",", new[]
                {
                    result.Number.ToString(),
                    Csv(result.Verdict),
                    Csv(result.AiDecision),
                    Csv(result.AiReviewed ? result.AiConfidence.ToString("P0") : ""),
                    Csv(result.AiModel),
                    Csv(result.StatusCode),
                    Csv(result.Platform),
                    Csv(result.ContentType),
                    Csv(result.ExpectedAuthor),
                    Csv(result.Title),
                    Csv(result.OriginalUrl),
                    Csv(result.FinalUrl),
                    Csv(result.Evidence),
                    Csv(result.EvidenceStage),
                    Csv(result.AcquisitionAttempts),
                    Csv(result.SiteHealth),
                    Csv(result.InfrastructureKey),
                    Csv(result.CheckedAt),
                    Csv(result.Duration)
                }));
        }

        int removed = ordered.Count(item => item.Verdict == "已失效");
        int alive = ordered.Count(item => item.Verdict == "仍可访问");
        int unavailable = ordered.Count(item => item.Verdict == "公网不可访问");
        int temporary = ordered.Count(item => item.Verdict == "暂时异常");
        int review = ordered.Count - removed - alive - unavailable - temporary;
        double explicitRate = ordered.Count == 0 ? 0 :
            100.0 * (removed + alive + unavailable) / ordered.Count;
        Console.WriteLine("TOTAL=" + ordered.Count);
        Console.WriteLine("REMOVED=" + removed);
        Console.WriteLine("ALIVE=" + alive);
        Console.WriteLine("PUBLIC_UNAVAILABLE=" + unavailable);
        Console.WriteLine("TEMPORARY=" + temporary);
        Console.WriteLine("REVIEW=" + review);
        Console.WriteLine("EXPLICIT_RATE=" + explicitRate.ToString("0.00") + "%");
        Console.WriteLine("PAUSED_GROUPS=" + restrictions.PausedPlatforms.Count);
        Console.WriteLine("OUTPUT=" + output);
        return ordered.Count == jobs.Count ? 0 : 1;
    }
}
