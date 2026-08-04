using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LinkDispositionChecker;

internal static class FastAuditRunner
{
    internal static List<CheckJob> InterleavePendingJobs(IEnumerable<CheckJob> jobs)
    {
        var queues = (jobs ?? Enumerable.Empty<CheckJob>())
            .Where(job => job != null)
            .GroupBy(job => String.IsNullOrWhiteSpace(job.InfrastructureKey)
                ? BatchPreflightPlanner.PlatformKey(job) : job.InfrastructureKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new Queue<CheckJob>(group.OrderBy(job => job.Number)))
            .OrderByDescending(queue => queue.Count)
            .ToList();
        var ordered = new List<CheckJob>();
        while (queues.Any(queue => queue.Count > 0))
        {
            foreach (Queue<CheckJob> queue in queues)
                if (queue.Count > 0) ordered.Add(queue.Dequeue());
        }
        return ordered;
    }

    private static string Csv(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    public static int Main(string[] args)
    {
        if (args.Length < 2) return 2;
        try
        {
            return Run(args[0], args[1]).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR_TYPE=" + ex.GetType().FullName);
            Console.Error.WriteLine("ERROR_MESSAGE=" + (ex.Message ?? "").Replace("\r", " ").Replace("\n", " "));
            Console.Error.WriteLine("ERROR_STACK=" + (ex.StackTrace ?? "").Replace("\r", " ").Replace("\n", " | "));
            return 99;
        }
    }

    private static async Task<CheckResult> CheckOne(
        Checker checker,
        CheckJob job,
        PlatformRestrictionController restrictions,
        InfrastructureRestrictionController infrastructureRestrictions)
    {
        CheckResult result;
        // Every row gets its own full evidence attempt.  A platform or shared-IP
        // circuit breaker may record a warning, but it must never turn the
        // remainder of a real batch into synthetic "unfinished" rows before
        // their target URLs have been checked.
        // The fast stage is HTTP/API only. Browser rendering is an explicit
        // user action and must never be started implicitly or mixed into the
        // fast-stage coverage metric.
        bool quickBrowser = false;
        result = await checker.CheckAsync(
            job.Url,
            job.Number,
            job.ExpectedTitle,
            job.ExpectedExcerpt,
            job.ExpectedAuthor,
            job.Platform,
            job.ContentType,
            quickBrowser,
            CancellationToken.None);
        // Do not escalate to public-cloud/remote/browser evidence here. Those
        // are the explicit deep-review action; including them would make the
        // fast-stage metric depend on external quotas and multi-second waits.
        string pausedPlatform;
        restrictions.Observe(job, result, out pausedPlatform);
        string pausedInfrastructure;
        infrastructureRestrictions.Observe(job, result, out pausedInfrastructure);

        result.Verdict = Checker.NormalizeVisibleVerdict(result.Verdict);
        result.SourceSheet = job.SourceSheet;
        result.SourceRow = job.SourceRow;
        result.InfrastructureKey = job.InfrastructureKey;
        ContractAcceptanceClassifier.Apply(result);
        return result;
    }

    private static async Task<int> Run(string input, string output)
    {
        string oldQuickPass = Environment.GetEnvironmentVariable("LINK_CHECKER_QUICK_PASS");
        Environment.SetEnvironmentVariable("LINK_CHECKER_QUICK_PASS", "1");
        try
        {
            return await RunCore(input, output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LINK_CHECKER_QUICK_PASS", oldQuickPass);
        }
    }

    private static void RunBrowserFastStage(List<CheckResult> ordered)
    {
        List<CheckResult> candidates = ordered.Where(item =>
            MainForm.IsFastEvidenceReviewCandidate(item)).ToList();
        Console.WriteLine("PHASE=browser-fast,PENDING=" + candidates.Count);
        if (candidates.Count == 0) return;

        Exception browserError = null;
        var thread = new Thread(delegate()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var form = new DeepReviewForm(candidates, item =>
                {
                    ContractAcceptanceClassifier.Apply(item);
                }, true, true))
                {
                    form.ShowDialog();
                }
            }
            catch (Exception ex) { browserError = ex; }
        });
        thread.IsBackground = false;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (browserError != null)
            Console.WriteLine("BROWSER_FAST_ERROR=" + browserError.Message.Replace("\r", " ").Replace("\n", " "));
        Console.WriteLine("BROWSER_FAST_RESOLVED=" + candidates.Count(item =>
            item.Verdict == "已失效" || item.Verdict == "仍可访问"));
    }

    private static async Task<int> RunCore(string input, string output)
    {
        DateTime taskStartedAt = DateTime.Now;
        var taskWatch = System.Diagnostics.Stopwatch.StartNew();
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
        bool resumeEnabled = String.Equals(Environment.GetEnvironmentVariable("FAST_AUDIT_RESUME"), "1", StringComparison.OrdinalIgnoreCase);
        string inputSha256 = AuditCheckpointStore.ComputeInputSha256(input);
        var results = new ConcurrentBag<CheckResult>();
        var restrictions = new PlatformRestrictionController(3);
        var infrastructureRestrictions = new InfrastructureRestrictionController(2);
        var checker = new Checker(900000);
        using (var checkpointStore = new AuditCheckpointStore(output, inputSha256, resumeEnabled))
        {
            Dictionary<int, CheckResult> recovered = checkpointStore.Load(jobs,
                message => Console.WriteLine("CHECKPOINT_WARNING=" + message));
            bool retryUnresolved = String.Equals(Environment.GetEnvironmentVariable("FAST_AUDIT_RETRY_UNRESOLVED"), "1", StringComparison.OrdinalIgnoreCase);
            int checkpointRecords = recovered.Count;
            if (retryUnresolved)
            {
                recovered = recovered.Where(item => item.Value != null &&
                    (item.Value.Verdict == "已失效" || item.Value.Verdict == "仍可访问"))
                    .ToDictionary(item => item.Key, item => item.Value);
            }
            foreach (CheckResult result in recovered.Values) results.Add(result);
            int complete = recovered.Count;
            List<CheckJob> pending = jobs.Where(job => !recovered.ContainsKey(job.Number)).ToList();
            Console.WriteLine("CHECKPOINT_ENABLED=" + (resumeEnabled ? "1" : "0"));
            Console.WriteLine("CHECKPOINT_RECORDS=" + checkpointRecords);
            Console.WriteLine("CHECKPOINT_RECOVERED=" + recovered.Count);
            Console.WriteLine("CHECKPOINT_RETRY_UNRESOLVED=" + (retryUnresolved ? "1" : "0"));

            Console.WriteLine("PHASE=register-infrastructure");
            Dictionary<string, int> infrastructures = pending.Count == 0
                ? new Dictionary<string, int>()
                : await Checker.RegisterInfrastructureAsync(pending, CancellationToken.None);
            Console.WriteLine("INFRASTRUCTURES=" + infrastructures.Count);
            Console.WriteLine("SHARED_INFRASTRUCTURES=" + infrastructures.Count(item => item.Value > 1));
            pending = InterleavePendingJobs(pending);
            Console.WriteLine("PENDING_INTERLEAVED=1");

            int configuredWorkers;
            if (!Int32.TryParse(Environment.GetEnvironmentVariable("FAST_AUDIT_WORKERS"), out configuredWorkers))
                configuredWorkers = 6;

            // Run the same formal pass as the desktop application. A separate
            // preflight sample duplicates requests and makes the validation runner
            // report a different execution path from the product.
            int workerCount = Math.Min(Math.Max(1, configuredWorkers), Math.Max(1, pending.Count));
            Console.WriteLine("PHASE=check,WORKERS=" + workerCount + ",PENDING=" + pending.Count);

            int next = -1;
            var tasks = Enumerable.Range(0, workerCount).Select(async ignored =>
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref next);
                    if (index >= pending.Count) break;
                    CheckResult result = await CheckOne(checker, pending[index], restrictions, infrastructureRestrictions);
                    checkpointStore.Append(result);
                    results.Add(result);
                    int done = Interlocked.Increment(ref complete);
                    if (done % 25 == 0 || done == jobs.Count)
                        Console.WriteLine("PROGRESS=" + done + "/" + jobs.Count);
                }
            }).ToArray();
            await Task.WhenAll(tasks);
        }

        List<CheckResult> ordered = results.OrderBy(item => item.Number).ToList();
        // The validation runner represents the product's quick stage. Do not
        // silently launch WebView2 here; browser evidence is an explicit user
        // action in the desktop application and must not be mixed into the
        // quick-stage coverage metric.
        Console.WriteLine("PHASE=browser-fast,SKIPPED=manual-only");
        taskWatch.Stop();
        DateTime taskCompletedAt = DateTime.Now;
        string taskElapsed = taskWatch.Elapsed.ToString(@"hh\:mm\:ss");
        foreach (CheckResult result in ordered)
        {
            result.TaskStartedAt = taskStartedAt.ToString("yyyy-MM-dd HH:mm:ss");
            result.TaskCompletedAt = taskCompletedAt.ToString("yyyy-MM-dd HH:mm:ss");
            result.TaskElapsed = taskElapsed;
        }
        using (var writer = new StreamWriter(output, false, new UTF8Encoding(true)))
        {
            writer.WriteLine("序号,核验结果,内容状态,公开可访问性,合同验收建议,证据等级,供应商行动,AI判断,AI置信度,AI模型,HTTP状态,平台,内容类型,发文作者,页面标题,原链接,最终地址,判定依据,追证阶段,取证线路,站点对照,基础设施,来源工作表,来源行号,核验时间,单条耗时,任务开始时间,任务完成时间,任务总耗时");
            foreach (CheckResult result in ordered)
            {
                ContractAcceptanceView view = ContractAcceptanceClassifier.Evaluate(result);
                writer.WriteLine(String.Join(",", new[]
                {
                    result.Number.ToString(),
                    Csv(result.Verdict),
                    Csv(view.ContentStatus),
                    Csv(view.PublicReachability),
                    Csv(view.AcceptanceRecommendation),
                    Csv(view.EvidenceGrade),
                    Csv(view.SupplierAction),
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
                    Csv(result.SourceSheet),
                    result.SourceRow.ToString(),
                    Csv(result.CheckedAt), Csv(result.Duration), Csv(result.TaskStartedAt),
                    Csv(result.TaskCompletedAt), Csv(result.TaskElapsed)
                }));
            }
        }

        int removed = ordered.Count(item => item.Verdict == "已失效");
        int alive = ordered.Count(item => item.Verdict == "仍可访问");
        int unavailable = ordered.Count(item => item.Verdict == "公网不可访问");
        int temporary = ordered.Count(item => item.Verdict == "暂时异常");
        int review = ordered.Count - removed - alive - unavailable - temporary;
        int contentResolved = removed + alive;
        double contentResolvedRate = ordered.Count == 0 ? 0 :
            100.0 * contentResolved / ordered.Count;
        Console.WriteLine("TOTAL=" + ordered.Count);
        Console.WriteLine("REMOVED=" + removed);
        Console.WriteLine("ALIVE=" + alive);
        Console.WriteLine("PUBLIC_UNAVAILABLE=" + unavailable);
        Console.WriteLine("TEMPORARY=" + temporary);
        Console.WriteLine("REVIEW=" + review);
        Console.WriteLine("CONTENT_RESOLVED=" + contentResolved);
        Console.WriteLine("CONTENT_RESOLVED_RATE=" + contentResolvedRate.ToString("0.00") + "%");
        Console.WriteLine("TASK_STARTED_AT=" + taskStartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        Console.WriteLine("TASK_COMPLETED_AT=" + taskCompletedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        Console.WriteLine("TASK_ELAPSED=" + taskElapsed);
        Console.WriteLine("CONTRACT_PENDING=" + (ordered.Count - contentResolved));
        Console.WriteLine("PAUSED_GROUPS=" + restrictions.PausedPlatforms.Count);
        Console.WriteLine("OUTPUT=" + output);
        return ordered.Count == jobs.Count ? 0 : 1;
    }
}
