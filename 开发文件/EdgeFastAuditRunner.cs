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
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal sealed class EdgeFastAuditForm : Form
{
    private readonly string _input;
    private readonly string _output;
    private readonly string _profile;
    private readonly WebView2 _browser = new WebView2();

    public EdgeFastAuditForm(string input, string output)
    {
        _input = input;
        _output = output;
        string configuredProfile = Environment.GetEnvironmentVariable("EDGE_AUDIT_PROFILE");
        _profile = String.IsNullOrWhiteSpace(configuredProfile)
            ? Path.Combine(Path.GetTempPath(), "LinkCheckerEdgeAudit_" + Guid.NewGuid().ToString("N"))
            : configuredProfile;
        ShowInTaskbar = false;
        Width = 320;
        Height = 240;
        _browser.Dock = DockStyle.Fill;
        Controls.Add(_browser);
        Shown += async delegate { await RunAsync(); };
        FormClosed += delegate
        {
            if (!String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EDGE_AUDIT_PROFILE"))) return;
            try { if (Directory.Exists(_profile)) Directory.Delete(_profile, true); } catch { }
        };
    }

    private async Task RunAsync()
    {
        try
        {
            List<CheckJob> jobs = MainForm.LoadCsvJobs(_input);
            string platformFilter = Environment.GetEnvironmentVariable("EDGE_AUDIT_PLATFORM");
            if (!String.IsNullOrWhiteSpace(platformFilter))
                jobs = jobs.Where(job => (job.Url ?? "").IndexOf(platformFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            string numberFilter = Environment.GetEnvironmentVariable("EDGE_AUDIT_NUMBERS");
            if (!String.IsNullOrWhiteSpace(numberFilter))
            {
                var selectedNumbers = new HashSet<int>(numberFilter.Split(',').Select(value =>
                {
                    int number;
                    return Int32.TryParse(value.Trim(), out number) ? number : -1;
                }).Where(number => number > 0));
                jobs = jobs.Where(job => selectedNumbers.Contains(job.Number)).ToList();
            }
            int limit;
            if (Int32.TryParse(Environment.GetEnvironmentVariable("EDGE_AUDIT_LIMIT"), out limit) && limit > 0)
                jobs = jobs.Take(limit).ToList();
            var results = new CheckResult[jobs.Count];
            Directory.CreateDirectory(_profile);
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, _profile);
            await _browser.EnsureCoreWebView2Async(environment);
            int next = -1;
            int completed = 0;
            var probeQueue = new ConcurrentQueue<int>();
            var renderQueue = new Queue<int>();
            int configuredWorkers;
            if (!Int32.TryParse(Environment.GetEnvironmentVariable("EDGE_AUDIT_WORKERS"), out configuredWorkers))
                configuredWorkers = 1;
            int workerCount = Math.Min(Math.Max(1, configuredWorkers), Math.Max(1, jobs.Count));
            Task[] workers = Enumerable.Range(0, workerCount).Select(async workerNumber =>
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref next);
                    if (index >= jobs.Count) break;
                    CheckJob job = jobs[index];
                    var result = new CheckResult
                    {
                        Number = job.Number,
                        OriginalUrl = job.Url,
                        ExpectedTitle = job.ExpectedTitle ?? "",
                        ExpectedExcerpt = job.ExpectedExcerpt ?? "",
                        ExpectedAuthor = job.ExpectedAuthor ?? "",
                        Platform = job.Platform ?? "",
                        ContentType = String.IsNullOrWhiteSpace(job.ContentType) ? Checker.InferContentType(job.Platform, job.Url, job.ExpectedTitle) : job.ContentType,
                        SourceSheet = job.SourceSheet,
                        SourceRow = job.SourceRow
                    };
                    try
                    {
                        EdgeFetchedResponse response = await DeepReviewForm.LoadEdgeResourceAsync(
                            _browser.CoreWebView2, job.Url, 700000, CancellationToken.None);
                        bool resolved = DeepReviewForm.ClassifyFastResponse(result, response);
                        if (!resolved && result.Verdict != "已失效" && result.Verdict != "仍可访问") probeQueue.Enqueue(index);
                    }
                    catch (Exception ex)
                    {
                        result.Verdict = "人工复核";
                        result.StatusCode = "浏览器失败";
                        result.Evidence = ex.ToString();
                    }
                    results[index] = result;
                    int done = Interlocked.Increment(ref completed);
                    if (done % 50 == 0 || done == jobs.Count) Console.WriteLine(done + "/" + jobs.Count);
                }
            }).ToArray();
            await Task.WhenAll(workers);
            Console.WriteLine("raw-stage completed, unresolved=" + probeQueue.Count);
            int unresolvedIndex;
            int probed = 0;
            while (probeQueue.TryDequeue(out unresolvedIndex))
            {
                CheckResult result = results[unresolvedIndex];
                try
                {
                    bool probeResolved = await DeepReviewForm.TryApplyEdgePlatformProbeAsync(
                        _browser.CoreWebView2, result, CancellationToken.None);
                    if ((result.OriginalUrl ?? "").IndexOf("douyin.com", StringComparison.OrdinalIgnoreCase) >= 0)
                        Console.WriteLine("douyin-probe " + result.Number + " resolved=" + probeResolved + " " + result.Evidence);
                    if (!probeResolved && DeepReviewForm.ShouldFastRenderPlatform(result)) renderQueue.Enqueue(unresolvedIndex);
                }
                catch { renderQueue.Enqueue(unresolvedIndex); }
                probed++;
                if (probed % 25 == 0) Console.WriteLine("probe-stage " + probed + ", render-queued=" + renderQueue.Count);
            }
            Console.WriteLine("probe-stage completed, render-queued=" + renderQueue.Count);
            int renderedCount = 0;
            while (renderQueue.Count > 0)
            {
                unresolvedIndex = renderQueue.Dequeue();
                CheckResult result = results[unresolvedIndex];
                try
                {
                    RenderedPageData page = await DeepReviewForm.ReadFastRenderedPageAsync(
                        _browser, result.OriginalUrl, CancellationToken.None);
                    bool resolved = DeepReviewForm.ApplyFastRenderedPage(result, page);
                    if (!resolved && DeepReviewForm.IsFastSecurityPage(page))
                    {
                        result.Verdict = "人工复核";
                        result.EdgeFastReviewed = true;
                        result.DeepReviewed = false;
                        result.Evidence = "平台出现安全验证或访问频繁提示；仅保留当前链接待复核，继续检查同平台其他链接";
                    }
                }
                catch (Exception ex)
                {
                    result.Verdict = "人工复核";
                    result.StatusCode = "浏览器失败";
                    result.Evidence = ex.ToString();
                }
                renderedCount++;
                if (renderedCount % 20 == 0 || renderQueue.Count == 0)
                    Console.WriteLine("render-stage " + renderedCount + ", remaining=" + renderQueue.Count);
            }
            using (var writer = new StreamWriter(_output, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("序号,核验结果,内容状态,公开可访问性,合同验收建议,证据等级,供应商行动,AI判断,AI置信度,AI模型,HTTP状态,平台,内容类型,发文作者,页面标题,原链接,最终地址,判定依据,追证阶段,取证线路,站点对照,基础设施,来源工作表,来源行号,核验时间,单条耗时,任务开始时间,任务完成时间,任务总耗时");
                foreach (CheckResult result in results)
                {
                    result.Verdict = Checker.NormalizeVisibleVerdict(result.Verdict);
                    ContractAcceptanceView view = ContractAcceptanceClassifier.Evaluate(result);
                    writer.WriteLine(String.Join(",", new[]
                    {
                        result.Number.ToString(), Csv(result.Verdict), Csv(view.ContentStatus), Csv(view.PublicReachability),
                        Csv(view.AcceptanceRecommendation), Csv(view.EvidenceGrade), Csv(view.SupplierAction), Csv(result.AiDecision),
                        Csv(result.AiReviewed ? result.AiConfidence.ToString("P0") : ""), Csv(result.AiModel), Csv(result.StatusCode),
                        Csv(result.Platform), Csv(result.ContentType), Csv(result.ExpectedAuthor), Csv(result.Title), Csv(result.OriginalUrl),
                        Csv(result.FinalUrl), Csv(result.Evidence), Csv(result.EvidenceStage), Csv(result.AcquisitionAttempts),
                        Csv(result.SiteHealth), Csv(result.InfrastructureKey), Csv(result.SourceSheet), result.SourceRow.ToString(),
                        Csv(result.CheckedAt), Csv(result.Duration), Csv(result.TaskStartedAt), Csv(result.TaskCompletedAt), Csv(result.TaskElapsed)
                    }));
                }
            }
            Console.WriteLine("removed=" + results.Count(item => item.Verdict == "已失效") +
                ", alive=" + results.Count(item => item.Verdict == "仍可访问") +
                ", review=" + results.Count(item => item.Verdict != "已失效" && item.Verdict != "仍可访问"));
            Environment.ExitCode = results.All(item => item != null) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            Environment.ExitCode = 1;
        }
        finally { Close(); }
    }

    private static string Csv(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

}

internal static class EdgeFastAuditRunner
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 2) return 2;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new EdgeFastAuditForm(args[0], args[1]));
        return Environment.ExitCode;
    }
}
