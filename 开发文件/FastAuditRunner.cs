using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using LinkDispositionChecker;

internal static class FastAuditRunner
{
    private static int _savedLoginCaptureAttempted;

    private sealed class DeterminateResultCache : IDisposable
    {
        private readonly object _sync = new object();
        private readonly string _path;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        private readonly Dictionary<string, CheckResult> _items = new Dictionary<string, CheckResult>(StringComparer.OrdinalIgnoreCase);
        private StreamWriter _writer;
        internal DeterminateResultCache(string path)
        {
            _path = String.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
            if (_path.Length == 0 || !File.Exists(_path)) return;
            foreach (string line in File.ReadLines(_path, Encoding.UTF8))
            {
                try
                {
                    var envelope = _serializer.Deserialize<Dictionary<string, object>>(line);
                    string key = envelope != null && envelope.ContainsKey("Key") ? envelope["Key"] as string : null;
                    CheckResult result = envelope != null && envelope.ContainsKey("Result") ? _serializer.ConvertToType<CheckResult>(envelope["Result"]) : null;
                    if (!String.IsNullOrWhiteSpace(key) && IsDeterminate(result)) _items[key] = result;
                }
                catch { }
            }
        }
        internal bool TryGet(string key, out CheckResult result) { lock (_sync) return _items.TryGetValue(key ?? "", out result); }
        internal void Put(string key, CheckResult result)
        {
            if (_path.Length == 0 || String.IsNullOrWhiteSpace(key) || !IsDeterminate(result)) return;
            lock (_sync)
            {
                if (_items.ContainsKey(key)) return;
                _items[key] = result;
                if (_writer == null)
                {
                    string directory = Path.GetDirectoryName(_path);
                    if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                    _writer = new StreamWriter(_path, true, new UTF8Encoding(false)); _writer.AutoFlush = true;
                }
                _writer.WriteLine(_serializer.Serialize(new { Key = key, Result = result }));
            }
        }
        private static bool IsDeterminate(CheckResult result) { return result != null && (result.Verdict == "仍可访问" || result.Verdict == "已失效") && !String.IsNullOrWhiteSpace(result.Evidence); }
        public void Dispose() { lock (_sync) { if (_writer != null) { _writer.Dispose(); _writer = null; } } }
    }

    private static string RequestKey(CheckJob job)
    {
        if (job == null) return "";
        Uri target;
        string url = Uri.TryCreate(job.Url, UriKind.Absolute, out target) ? target.GetComponents(UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped).TrimEnd('/') : (job.Url ?? "").Trim();
        // Keep row-level metadata in the cache identity. The same URL can occur
        // with different historical titles, excerpts, or authors; reusing a
        // final verdict across those rows can turn a content mismatch into a
        // false positive. The publisher may edit metadata later, so this is
        // intentionally a conservative request-level cache key.
        return url + "\n" + (job.Platform ?? "") + "\n" + (job.ContentType ?? "") +
            "\n" + (job.ExpectedTitle ?? "") + "\n" + (job.ExpectedExcerpt ?? "") +
            "\n" + (job.ExpectedAuthor ?? "");
    }
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

    internal static List<CheckJob> PrioritizeFreshJobs(IEnumerable<CheckJob> jobs, ISet<int> historicalUnresolved)
    {
        var historical = historicalUnresolved ?? new HashSet<int>();
        return (jobs ?? Enumerable.Empty<CheckJob>())
            .Where(job => job != null)
            .OrderBy(job => historical.Contains(job.Number) ? 1 : 0)
            .ThenBy(job => job.Number)
            .ToList();
    }

    internal static bool ShouldRunQuickIndependentEvidenceForDeferred(CheckJob job, CheckResult result)
    {
        return String.Equals(Environment.GetEnvironmentVariable("FAST_AUDIT_QUICK_INDEPENDENT_EVIDENCE"), "1",
            StringComparison.OrdinalIgnoreCase) && Checker.ShouldTryQuickIndependentEvidence(job, result);
    }

    internal static bool ShouldRunQuickIndependentEvidenceForShell(CheckJob job, CheckResult result)
    {
        return String.Equals(Environment.GetEnvironmentVariable("FAST_AUDIT_QUICK_INDEPENDENT_EVIDENCE"), "1",
            StringComparison.OrdinalIgnoreCase) && Checker.ShouldTryQuickIndependentContentEvidence(job, result);
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
        // Two real failures are enough to pause a generic shared infrastructure
        // within this shard. Remaining rows stay retryable and are never
        // converted into a content verdict.
        if (infrastructureRestrictions.IsPaused(job))
        {
            result = MainForm.CreateInfrastructureDeferredResult(job, job.InfrastructureKey);
            // A shared-infrastructure circuit prevents another request through
            // the failing local route, but it must not prevent the bounded
            // independent-evidence path from running.  Otherwise every row
            // behind a noisy IP is guaranteed to remain unfinished even when a
            // public reader can still obtain target-specific evidence.
            if (ShouldRunQuickIndependentEvidenceForDeferred(job, result) ||
                ShouldRunQuickIndependentEvidenceForShell(job, result))
                result = await checker.TryQuickIndependentEvidenceAsync(job, result, CancellationToken.None);
        }
        else
        {
            // The fast stage is HTTP/API only. Browser rendering is an explicit
            // user action and must never be mixed into the fast-stage metric.
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
            if (String.Equals(Environment.GetEnvironmentVariable("FAST_AUDIT_QUICK_INDEPENDENT_EVIDENCE"), "1",
                StringComparison.OrdinalIgnoreCase))
                result = await checker.TryQuickIndependentEvidenceAsync(job, result, CancellationToken.None);
        }
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
        int numberOffset;
        if (Int32.TryParse(Environment.GetEnvironmentVariable("FAST_AUDIT_NUMBER_OFFSET"), out numberOffset) && numberOffset > 0)
        {
            foreach (CheckJob job in jobs) job.Number += numberOffset;
            Console.WriteLine("NUMBER_OFFSET=" + numberOffset);
        }
        if (jobs.Count == 0)
        {
            Console.Error.WriteLine("没有从输入文件中读取到有效链接。");
            return 3;
        }

        string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!String.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);

        Console.WriteLine("INPUT_JOBS=" + jobs.Count);
        bool interactiveLogin = String.Equals(Environment.GetEnvironmentVariable("FAST_AUDIT_LOGIN_INTERACTIVE"), "1",
            StringComparison.OrdinalIgnoreCase);
        bool savedLogin = String.Equals(Environment.GetEnvironmentVariable("FAST_AUDIT_USE_SAVED_LOGIN"), "1",
            StringComparison.OrdinalIgnoreCase);
        string cookieHandoffPath = Environment.GetEnvironmentVariable("FAST_AUDIT_COOKIE_HANDOFF");
        if (AuthenticatedCookieBridge.Count == 0 && !String.IsNullOrWhiteSpace(cookieHandoffPath))
        {
            bool imported = AuthenticatedCookieBridge.ImportEncrypted(cookieHandoffPath);
            Console.WriteLine("COOKIE_HANDOFF_IMPORTED=" + (imported ? "1" : "0"));
        }
        if (AuthenticatedCookieBridge.Count == 0 && (interactiveLogin || savedLogin) &&
            Interlocked.Exchange(ref _savedLoginCaptureAttempted, 1) == 0)
        {
            CaptureBrowserLogin(jobs, !interactiveLogin);
            Console.WriteLine((interactiveLogin ? "INTERACTIVE_LOGIN_COOKIES=" : "SAVED_LOGIN_COOKIES=") +
                AuthenticatedCookieBridge.Count);
            if (!String.IsNullOrWhiteSpace(cookieHandoffPath) && AuthenticatedCookieBridge.Count > 0)
                Console.WriteLine("COOKIE_HANDOFF_EXPORTED=" +
                    (AuthenticatedCookieBridge.ExportEncrypted(cookieHandoffPath) ? "1" : "0"));
        }
        bool resumeEnabled = String.Equals(Environment.GetEnvironmentVariable("FAST_AUDIT_RESUME"), "1", StringComparison.OrdinalIgnoreCase);
        string inputSha256 = AuditCheckpointStore.ComputeInputSha256(input);
        var results = new ConcurrentBag<CheckResult>();
        var restrictions = new PlatformRestrictionController(3);
        var infrastructureRestrictions = new InfrastructureRestrictionController(2);
        var checker = new Checker(900000);
        string cachePath = Environment.GetEnvironmentVariable("FAST_AUDIT_RESULT_CACHE");
        using (var checkpointStore = new AuditCheckpointStore(output, inputSha256, resumeEnabled))
        using (var determinateCache = new DeterminateResultCache(cachePath))
        {
            string numberFilter = Environment.GetEnvironmentVariable("FAST_AUDIT_NUMBERS");
            HashSet<int> selectedNumbers = String.IsNullOrWhiteSpace(numberFilter) ? null :
                new HashSet<int>(numberFilter.Split(',').Select(value =>
                {
                    int number;
                    return Int32.TryParse(value.Trim(), out number) ? number : -1;
                }).Where(number => number > 0));
            Dictionary<int, CheckResult> recovered = checkpointStore.Load(jobs,
                message => Console.WriteLine("CHECKPOINT_WARNING=" + message));
            bool retryUnresolved = String.Equals(Environment.GetEnvironmentVariable("FAST_AUDIT_RETRY_UNRESOLVED"), "1", StringComparison.OrdinalIgnoreCase);
            int checkpointRecords = recovered.Count;
            var unresolvedCheckpointNumbers = new HashSet<int>(recovered
                .Where(item => item.Value != null && item.Value.Verdict != "已失效" && item.Value.Verdict != "仍可访问")
                .Select(item => item.Key));
            if (retryUnresolved)
            {
                recovered = recovered.Where(item => item.Value != null &&
                    (item.Value.Verdict == "已失效" || item.Value.Verdict == "仍可访问"))
                    .ToDictionary(item => item.Key, item => item.Value);
            }
            foreach (CheckResult result in recovered.Values) results.Add(result);
            int complete = recovered.Count;
            List<CheckJob> pending = jobs.Where(job => !recovered.ContainsKey(job.Number) &&
                (selectedNumbers == null || selectedNumbers.Contains(job.Number))).ToList();
            if (selectedNumbers != null)
                Console.WriteLine("SELECTED_NUMBERS=" + selectedNumbers.Count);
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
            if (retryUnresolved && unresolvedCheckpointNumbers.Count > 0)
            {
                // Finish never-seen rows first. Historical unresolved rows can
                // contain long 502/timeout waits; letting them monopolize all
                // workers delays coverage of the untouched portion of a full
                // workbook. They remain queued immediately after fresh rows.
                List<CheckJob> fresh = pending.Where(job => !unresolvedCheckpointNumbers.Contains(job.Number)).ToList();
                List<CheckJob> historical = pending.Where(job => unresolvedCheckpointNumbers.Contains(job.Number)).ToList();
                pending = InterleavePendingJobs(fresh)
                    .Concat(InterleavePendingJobs(historical))
                    .ToList();
                Console.WriteLine("PENDING_FRESH_FIRST=1,HISTORICAL_UNRESOLVED=" + unresolvedCheckpointNumbers.Count);
            }
            Console.WriteLine("PENDING_INTERLEAVED=1");

            int configuredWorkers;
            if (!Int32.TryParse(Environment.GetEnvironmentVariable("FAST_AUDIT_WORKERS"), out configuredWorkers))
                configuredWorkers = 6;

            // Run the same formal pass as the desktop application. A separate
            // preflight sample duplicates requests and makes the validation runner
            // report a different execution path from the product.
            int workerCount = Math.Min(Math.Max(1, configuredWorkers), Math.Max(1, pending.Count));
            Console.WriteLine("PHASE=check,WORKERS=" + workerCount + ",PENDING=" + pending.Count);

            // Deduplicate identical public targets within the same batch. Large
            // ledgers frequently contain the same URL under multiple source
            // rows; one in-flight request is enough, while each row still gets
            // its own result number and source metadata. Restriction/timeout
            // results are deliberately not cached across different identities.
            var inFlight = new ConcurrentDictionary<string, Task<CheckResult>>(StringComparer.OrdinalIgnoreCase);
            Func<CheckJob, string> requestKeyOf = RequestKey;
            int deduplicated = pending.GroupBy(requestKeyOf, StringComparer.OrdinalIgnoreCase).Sum(group => Math.Max(0, group.Count() - 1));
            Console.WriteLine("DEDUPLICATED_JOBS=" + deduplicated);
            int cacheHits = 0;

            int next = -1;
            var tasks = Enumerable.Range(0, workerCount).Select(async workerIndex =>
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref next);
                    if (index >= pending.Count) break;
                    CheckJob job = pending[index];
                    string requestKey = requestKeyOf(job);
                    CheckResult cached;
                    Task<CheckResult> shared = determinateCache.TryGet(requestKey, out cached)
                        ? Task.FromResult(cached)
                        : inFlight.GetOrAdd(requestKey,
                            delegate(string unused) { return CheckOne(checker, job, restrictions, infrastructureRestrictions); });
                    CheckResult sourceResult = await shared;
                    if (cached != null) Interlocked.Increment(ref cacheHits);
                    determinateCache.Put(requestKey, sourceResult);
                    CheckResult result = CloneForJob(sourceResult, job);
                    checkpointStore.Append(result);
                    results.Add(result);
                    int done = Interlocked.Increment(ref complete);
                    if (done % 25 == 0 || done == jobs.Count)
                        Console.WriteLine("PROGRESS=" + done + "/" + jobs.Count);
                }
            }).ToArray();
            await Task.WhenAll(tasks);
            Console.WriteLine("DETERMINATE_CACHE_HITS=" + cacheHits);
        }

        List<CheckResult> ordered = results.OrderBy(item => item.Number).ToList();
        string aiMode = AiFastStage.Mode();
        if (aiMode != "off")
        {
            AiFastStageReport aiReport = AiFastStage.RunAsync(ordered, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("AI_FAST_MODE=" + aiReport.Mode);
            Console.WriteLine("AI_FAST_CANDIDATES=" + aiReport.Candidates);
            Console.WriteLine("AI_FAST_ATTEMPTED=" + aiReport.Attempted);
            Console.WriteLine("AI_FAST_SUCCEEDED=" + aiReport.Succeeded);
            Console.WriteLine("AI_FAST_APPLIED=" + aiReport.Applied);
            Console.WriteLine("AI_FAST_FAILED=" + aiReport.Failed);
            if (!String.IsNullOrWhiteSpace(aiReport.Error))
                Console.WriteLine("AI_FAST_ERROR=" + aiReport.Error.Replace("\r", " ").Replace("\n", " "));
        }
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
        Console.WriteLine("INFRASTRUCTURE_DEFERRED=" + ordered.Count(item =>
            item != null && item.StatusCode == "基础设施异常"));
        Console.WriteLine("PAUSED_GROUPS=" + restrictions.PausedPlatforms.Count);
        Console.WriteLine("OUTPUT=" + output);
        return ordered.Count == jobs.Count ? 0 : 1;
    }

    private static void CaptureBrowserLogin(IEnumerable<CheckJob> jobs, bool automatic)
    {
        Exception browserError = null;
        var items = (jobs ?? Enumerable.Empty<CheckJob>()).Where(job => job != null)
            .Select(job => new CheckResult
            {
                Number = job.Number,
                OriginalUrl = job.Url,
                ExpectedTitle = job.ExpectedTitle,
                ExpectedExcerpt = job.ExpectedExcerpt,
                ExpectedAuthor = job.ExpectedAuthor,
                Platform = job.Platform,
                ContentType = job.ContentType,
                Verdict = "人工复核"
            }).ToList();
        var thread = new Thread(delegate()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var form = new DeepReviewForm(items, null, false, automatic, true))
                    form.ShowDialog();
            }
            catch (Exception ex) { browserError = ex; }
        });
        thread.IsBackground = false;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (browserError != null)
            Console.WriteLine("SAVED_LOGIN_ERROR=" + browserError.Message.Replace("\r", " ").Replace("\n", " "));
    }

    private static CheckResult CloneForJob(CheckResult source, CheckJob job)
    {
        if (source == null || job == null) return source;
        // Keep the network evidence but restore row-specific identity fields.
        var copy = new CheckResult
        {
            Number = job.Number, Verdict = source.Verdict, StatusCode = source.StatusCode,
            Title = source.Title, OriginalUrl = job.Url, FinalUrl = source.FinalUrl,
            Evidence = source.Evidence, CheckedAt = source.CheckedAt, Duration = source.Duration,
            ExpectedTitle = job.ExpectedTitle ?? "", ExpectedExcerpt = job.ExpectedExcerpt ?? "",
            ExpectedAuthor = job.ExpectedAuthor ?? "", Platform = job.Platform ?? "",
            ContentType = job.ContentType ?? source.ContentType, SkipDeepReview = source.SkipDeepReview,
            SourceSheet = job.SourceSheet, SourceRow = job.SourceRow, DeepReviewed = source.DeepReviewed,
            EdgeFastReviewed = source.EdgeFastReviewed, EvidenceTrail = source.EvidenceTrail,
            AnalysisContext = source.AnalysisContext, AiReviewed = source.AiReviewed,
            AiDecision = source.AiDecision, AiConfidence = source.AiConfidence, AiModel = source.AiModel,
            AiAttemptCount = source.AiAttemptCount, AiLastError = source.AiLastError,
            EvidenceStage = source.EvidenceStage, AcquisitionAttempts = source.AcquisitionAttempts,
            SiteHealth = source.SiteHealth, InfrastructureKey = job.InfrastructureKey,
            ContentStatus = source.ContentStatus, PublicReachability = source.PublicReachability,
            AcceptanceRecommendation = source.AcceptanceRecommendation, EvidenceGrade = source.EvidenceGrade,
            SupplierAction = source.SupplierAction
        };
        return copy;
    }
}
