using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LinkDispositionChecker
{
    internal sealed class ExecutionLogContext
    {
        private readonly ConcurrentQueue<CheckResult> _items = new ConcurrentQueue<CheckResult>();
        private readonly ConcurrentQueue<string> _events = new ConcurrentQueue<string>();
        private int _aiRequests;
        private int _aiSucceeded;
        private int _aiFailed;
        private int _aiRetries;

        internal string RunId { get; private set; }
        internal string Operation { get; private set; }
        internal string Trigger { get; private set; }
        internal string PerformanceMode { get; private set; }
        internal string NetworkMode { get; private set; }
        internal DateTime StartedAt { get; private set; }
        internal DateTime EndedAt { get; set; }
        internal string Outcome { get; set; }
        internal string StopReason { get; set; }
        internal int TotalJobs { get; private set; }
        internal int CompletedBefore { get; private set; }
        internal int PlannedItems { get; private set; }

        internal static ExecutionLogContext Start(string operation, string trigger, string performanceMode,
            string networkMode, int totalJobs, int completedBefore, int plannedItems)
        {
            DateTime now = DateTime.Now;
            var context = new ExecutionLogContext
            {
                RunId = "RUN-" + now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant(),
                Operation = operation ?? "未知操作",
                Trigger = trigger ?? "",
                PerformanceMode = performanceMode ?? "",
                NetworkMode = networkMode ?? "",
                StartedAt = now,
                EndedAt = now,
                Outcome = "执行中",
                StopReason = "",
                TotalJobs = Math.Max(0, totalJobs),
                CompletedBefore = Math.Max(0, completedBefore),
                PlannedItems = Math.Max(0, plannedItems)
            };
            context.RecordEvent("任务开始，计划处理 " + context.PlannedItems + " 条");
            return context;
        }

        internal void Observe(CheckResult item)
        {
            if (item != null) _items.Enqueue(item);
        }

        internal void RecordEvent(string message)
        {
            if (!String.IsNullOrWhiteSpace(message))
                _events.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + message.Trim());
        }

        internal void RecordAiSuccess(int requests)
        {
            Interlocked.Add(ref _aiRequests, Math.Max(1, requests));
            Interlocked.Increment(ref _aiSucceeded);
            Interlocked.Add(ref _aiRetries, Math.Max(0, requests - 1));
        }

        internal void RecordAiFailure(int requests, string message)
        {
            Interlocked.Add(ref _aiRequests, Math.Max(1, requests));
            Interlocked.Increment(ref _aiFailed);
            Interlocked.Add(ref _aiRetries, Math.Max(0, requests - 1));
            RecordEvent(message);
        }

        internal List<CheckResult> ObservedItems
        {
            get { return _items.Where(item => item != null).ToList(); }
        }

        internal List<string> Events { get { return _events.ToList(); } }
        internal int AiRequests { get { return _aiRequests; } }
        internal int AiSucceeded { get { return _aiSucceeded; } }
        internal int AiFailed { get { return _aiFailed; } }
        internal int AiRetries { get { return _aiRetries; } }
    }

    internal static class ExecutionLogWriter
    {
        private static readonly object SyncRoot = new object();
        internal static readonly string LogDirectory = Path.Combine(StoragePaths.UserDataDirectory, "RunLogs");
        internal static readonly string LatestLogPath = Path.Combine(LogDirectory, "最近一次执行日志.txt");

        internal static string Write(ExecutionLogContext context, IEnumerable<CheckResult> sessionResults)
        {
            return WriteToDirectory(context, sessionResults, LogDirectory);
        }

        internal static string WriteToDirectory(ExecutionLogContext context, IEnumerable<CheckResult> sessionResults,
            string logDirectory)
        {
            if (context == null) return "";
            lock (SyncRoot)
            {
                if (String.IsNullOrWhiteSpace(logDirectory)) throw new ArgumentException("日志目录不能为空。", "logDirectory");
                Directory.CreateDirectory(logDirectory);
                context.EndedAt = context.EndedAt == default(DateTime) ? DateTime.Now : context.EndedAt;
                string safeOperation = Regex.Replace(context.Operation ?? "执行", @"[^\p{L}\p{N}_-]+", "_").Trim('_');
                if (safeOperation.Length == 0) safeOperation = "执行";
                string path = Path.Combine(logDirectory,
                    "执行日志_" + context.StartedAt.ToString("yyyyMMdd_HHmmss") + "_" + safeOperation + "_" + context.RunId + ".txt");
                List<string> lines = BuildLines(context, sessionResults);
                string temporary = path + ".tmp";
                File.WriteAllLines(temporary, lines, new UTF8Encoding(true));
                File.Move(temporary, path);
                File.Copy(path, Path.Combine(logDirectory, "最近一次执行日志.txt"), true);
                PruneHistoricalLogs(logDirectory, 100);
                return path;
            }
        }

        internal static void PruneHistoricalLogs(string logDirectory, int maximum)
        {
            if (String.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory)) return;
            FileInfo[] historical = new DirectoryInfo(logDirectory).GetFiles("执行日志_*.txt")
                .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            foreach (FileInfo file in historical.Skip(Math.Max(1, maximum)))
            {
                try { file.Delete(); } catch { }
            }
        }

        internal static List<string> BuildLines(ExecutionLogContext context, IEnumerable<CheckResult> sessionResults)
        {
            List<CheckResult> runItems = context == null ? new List<CheckResult>() : context.ObservedItems;
            List<CheckResult> allItems = (sessionResults ?? Enumerable.Empty<CheckResult>())
                .Where(item => item != null).ToList();
            var lines = new List<string>();
            lines.Add("链接失效检测工具 - 执行诊断日志");
            lines.Add("====================================");
            lines.Add("");
            lines.Add("运行编号：" + Safe(context == null ? "" : context.RunId, 100));
            lines.Add("工具版本：" + SessionStore.CurrentEngineVersion);
            lines.Add("日志格式：4");
            lines.Add("执行类型：" + Safe(context == null ? "" : context.Operation, 80));
            lines.Add("启动方式：" + Safe(context == null ? "" : context.Trigger, 80));
            lines.Add("开始时间：" + (context == null ? "" : context.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")));
            lines.Add("结束时间：" + (context == null ? "" : context.EndedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")));
            lines.Add("执行时长：" + (context == null ? "0.0" :
                Math.Max(0, (context.EndedAt - context.StartedAt).TotalSeconds).ToString("0.0")) + " 秒");
            lines.Add("执行结果：" + Safe(context == null ? "" : context.Outcome, 120));
            lines.Add("结束原因：" + Safe(context == null ? "" : context.StopReason, 300));
            lines.Add("性能模式：" + Safe(context == null ? "" : context.PerformanceMode, 80));
            lines.Add("网络模式：" + Safe(context == null ? "" : context.NetworkMode, 80));
            lines.Add("");
            lines.Add("一、本次执行统计");
            lines.Add("----------------");
            lines.Add("任务总数：" + (context == null ? 0 : context.TotalJobs));
            lines.Add("执行前已完成：" + (context == null ? 0 : context.CompletedBefore));
            lines.Add("计划处理：" + (context == null ? 0 : context.PlannedItems));
            lines.Add("本次实际产生结果：" + runItems.Count);
            lines.Add("本次尚未处理：" + Math.Max(0, (context == null ? 0 : context.PlannedItems) - runItems.Count));
            lines.Add("当前累计结果：" + allItems.Count);
            lines.Add("基础核验剩余：" + Math.Max(0, (context == null ? 0 : context.TotalJobs) - allItems.Count));
            AppendVerdictCounts(lines, "本次结果", runItems);
            AppendVerdictCounts(lines, "累计结果", allItems);
            lines.Add("本次内容状态已确认：" + runItems.Count(ContractAcceptanceClassifier.IsContentResolved) +
                " / " + runItems.Count);
            lines.Add("累计内容状态已确认：" + allItems.Count(ContractAcceptanceClassifier.IsContentResolved) +
                " / " + allItems.Count);
            lines.Add("");
            lines.Add("二、本次网络与状态分布");
            lines.Add("----------------------");
            AppendCounts(lines, runItems.GroupBy(item => String.IsNullOrWhiteSpace(item.StatusCode) ? "未记录" : item.StatusCode)
                .Select(group => new KeyValuePair<string, int>(group.Key, group.Count()))
                .OrderByDescending(item => item.Value).ThenBy(item => item.Key), 20);
            lines.Add("");
            lines.Add("三、本次失败类型");
            lines.Add("----------------");
            AppendCounts(lines, runItems.GroupBy(FailureCategory)
                .Select(group => new KeyValuePair<string, int>(group.Key, group.Count()))
                .OrderByDescending(item => item.Value).ThenBy(item => item.Key), 20);
            lines.Add("");
            lines.Add("四、平台汇总");
            lines.Add("------------");
            AppendCounts(lines, runItems.GroupBy(item => String.IsNullOrWhiteSpace(item.Platform) ? "未识别平台" : item.Platform.Trim())
                .Select(group => new KeyValuePair<string, int>(group.Key, group.Count()))
                .OrderByDescending(item => item.Value).ThenBy(item => item.Key), 20);
            lines.Add("");
            lines.Add("五、域名汇总（最多 30 个）");
            lines.Add("------------------------");
            foreach (var group in runItems.GroupBy(item => Host(item.OriginalUrl))
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key).Take(30))
            {
                int alive = group.Count(item => item.Verdict == "仍可访问");
                int removed = group.Count(item => item.Verdict == "已失效");
                int unfinished = group.Count() - alive - removed;
                lines.Add("- " + Safe(group.Key, 120) + "：共 " + group.Count() +
                    "，有效 " + alive + "，失效 " + removed + "，未完成 " + unfinished);
            }
            if (runItems.Count == 0) lines.Add("- 无");
            lines.Add("");
            lines.Add("六、自动追证与基础设施");
            lines.Add("----------------------");
            lines.Add("配置的远程取证节点：" + RemoteEvidenceStore.LoadEndpoints().Count);
            AppendCounts(lines, runItems.GroupBy(item => String.IsNullOrWhiteSpace(item.EvidenceStage) ? "未进入自动追证" : item.EvidenceStage)
                .Select(group => new KeyValuePair<string, int>(group.Key, group.Count()))
                .OrderByDescending(item => item.Value).ThenBy(item => item.Key), 20);
            foreach (var group in runItems.Where(item => !String.IsNullOrWhiteSpace(item.InfrastructureKey))
                .GroupBy(item => item.InfrastructureKey, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key).Take(20))
                lines.Add("- " + Safe(group.Key, 100) + "：共 " + group.Count() +
                    "，有效 " + group.Count(item => item.Verdict == "仍可访问") +
                    "，失效 " + group.Count(item => item.Verdict == "已失效") +
                    "，未完成 " + group.Count(item =>
                        item.Verdict != "仍可访问" && item.Verdict != "已失效"));
            lines.Add("");
            lines.Add("七、AI 使用");
            lines.Add("----------");
            lines.Add("本次 AI 请求次数：" + (context == null ? 0 : context.AiRequests));
            lines.Add("本次 AI 成功条数：" + (context == null ? 0 : context.AiSucceeded));
            lines.Add("本次 AI 失败条数：" + (context == null ? 0 : context.AiFailed));
            lines.Add("本次 AI 重试次数：" + (context == null ? 0 : context.AiRetries));
            lines.Add("本次 AI 已复核：" + runItems.Count(item => item.AiReviewed));
            lines.Add("本次 AI 自动确认：" + runItems.Count(item => item.AiReviewed &&
                (item.Verdict == "已失效" || item.Verdict == "仍可访问")));
            lines.Add("累计 AI 已复核：" + allItems.Count(item => item.AiReviewed));
            foreach (var group in runItems.Where(item => item.AiReviewed)
                .GroupBy(item => String.IsNullOrWhiteSpace(item.AiModel) ? "未记录模型" : item.AiModel)
                .OrderByDescending(group => group.Count()))
                lines.Add("- 模型 " + Safe(group.Key, 100) + "：" + group.Count() + " 条");
            lines.Add("");
            lines.Add("八、关键执行事件（最多 50 条）");
            lines.Add("--------------------------");
            List<string> events = context == null ? new List<string>() : context.Events.Take(50).ToList();
            foreach (string item in events) lines.Add("- " + Safe(item, 300));
            if (events.Count == 0) lines.Add("- 无异常事件");
            lines.Add("");
            lines.Add("九、匿名问题样本（最多 30 条）");
            lines.Add("----------------------------");
            List<CheckResult> issues = runItems.Where(item =>
                !ContractAcceptanceClassifier.IsContentResolved(item)).Take(30).ToList();
            foreach (CheckResult item in issues)
            {
                lines.Add("- ID=" + AnonymousId(item.OriginalUrl) +
                    "；域名=" + Safe(Host(item.OriginalUrl), 100) +
                    "；平台=" + Safe(item.Platform, 80) +
                    "；HTTP=" + Safe(item.StatusCode, 30) +
                    "；结果=" + Safe(item.Verdict, 40) +
                    "；类型=" + FailureCategory(item) +
                    "；耗时=" + Safe(item.Duration, 30) +
                    "；追证=" + Safe(item.EvidenceStage, 80) +
                    "；站点=" + Safe(item.SiteHealth, 80) +
                    "；基础设施=" + Safe(item.InfrastructureKey, 80) +
                    "；原因=" + Safe(item.Evidence, 220));
            }
            if (issues.Count == 0) lines.Add("- 无");
            lines.Add("");
            lines.Add("十、自动诊断提示");
            lines.Add("----------------");
            foreach (string suggestion in Suggestions(runItems)) lines.Add("- " + suggestion);
            lines.Add("");
            lines.Add("隐私说明");
            lines.Add("--------");
            lines.Add("本日志不包含 API Token、Cookie、登录账号、完整网页正文或完整链接。");
            lines.Add("问题样本只保留域名、匿名链接 ID、状态和经过脱敏的简短原因。发送前仍建议按单位要求检查内容。");
            lines.Add("");
            lines.Add("反馈方式：将“最近一次执行日志.txt”发送给维护者，并说明你认为最影响使用的现象。");
            return lines;
        }

        private static void AppendVerdictCounts(List<string> lines, string label, List<CheckResult> items)
        {
            int alive = items.Count(item => item.Verdict == "仍可访问");
            int removed = items.Count(item => item.Verdict == "已失效");
            lines.Add(label + "：有效 " + alive + "，失效 " + removed +
                "，未完成 " + Math.Max(0, items.Count - alive - removed));
        }

        private static void AppendCounts(List<string> lines, IEnumerable<KeyValuePair<string, int>> values, int maximum)
        {
            List<KeyValuePair<string, int>> list = values.Take(maximum).ToList();
            if (list.Count == 0) { lines.Add("- 无"); return; }
            foreach (KeyValuePair<string, int> item in list)
                lines.Add("- " + Safe(item.Key, 120) + "：" + item.Value);
        }

        internal static string FailureCategory(CheckResult item)
        {
            if (item == null) return "未知";
            if (!String.IsNullOrWhiteSpace(item.AiLastError)) return "AI 调用失败";
            if (item.Verdict == "公网不可访问") return "未完成";
            int code;
            if (Int32.TryParse(item.StatusCode ?? "", out code))
            {
                if (code == 429) return "平台限流";
                if (code == 444) return "网络出口限制";
                if (code == 401 || code == 403 || code == 407) return "访问或代理受限";
                if (code == 408) return "请求超时";
                if (code >= 500) return "HTTP 5xx";
                if (code == 404 || code == 410) return "明确不存在";
            }
            string evidence = item.Evidence ?? "";
            if (Regex.IsMatch(evidence, "验证码|安全验证|captcha|verify you are human", RegexOptions.IgnoreCase)) return "安全验证";
            if (Regex.IsMatch(evidence, "登录|扫码|App内|客户端", RegexOptions.IgnoreCase)) return "登录或客户端限制";
            if (Regex.IsMatch((item.StatusCode ?? "") + " " + evidence, "超时|timeout", RegexOptions.IgnoreCase)) return "请求超时";
            if (Regex.IsMatch((item.StatusCode ?? "") + " " + evidence, "连接失败|无法建立连接|connection", RegexOptions.IgnoreCase)) return "连接失败";
            if (item.Verdict == "人工复核" || item.Verdict == "疑似已处置") return "证据不足或冲突";
            return item.Verdict == "已失效" || item.Verdict == "仍可访问"
                ? "内容状态已确认" : "其他";
        }

        private static IEnumerable<string> Suggestions(List<CheckResult> items)
        {
            if (items.Count == 0) return new[] { "本次没有产生可分析结果。" };
            int fives = items.Count(item =>
            {
                int code;
                return Int32.TryParse(item.StatusCode ?? "", out code) && code >= 500;
            });
            int restricted = items.Count(item => FailureCategory(item) == "平台限流" ||
                FailureCategory(item) == "网络出口限制" || FailureCategory(item) == "安全验证");
            int insufficient = items.Count(item => FailureCategory(item) == "证据不足或冲突");
            int unfinished = items.Count(item =>
                item.Verdict != "已失效" && item.Verdict != "仍可访问");
            var result = new List<string>();
            if (fives * 100 >= items.Count * 30)
                result.Add("HTTP 5xx 占比达到 " + (fives * 100.0 / items.Count).ToString("0.0") + "%，优先检查单位代理、出口线路或目标站点服务状态。");
            if (restricted * 100 >= items.Count * 15)
                result.Add("限流/出口限制/安全验证占比较高，应降低同平台频率并比较浏览器通道与普通 HTTP 通道。");
            if (insufficient > 0)
                result.Add("有 " + insufficient + " 条已取得响应但证据不足，适合进一步浏览器补证或 AI 证据复核。");
            if (unfinished > 0)
                result.Add("有 " + unfinished + " 条尚未取得有效或失效证据，已保留在“继续未完成”队列。");
            if (result.Count == 0) result.Add("未发现单一占比突出的系统性异常，请结合匿名问题样本继续分析。");
            return result;
        }

        internal static string Safe(string value, int maximum)
        {
            string text = value ?? "";
            text = Regex.Replace(text, @"https?://[^\s""'<>]+", "[链接]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"sk-[A-Za-z0-9_\-]{8,}", "[凭据已隐藏]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", "[邮箱已隐藏]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"C:\\Users\\[^\\\s]+", @"C:\Users\[用户]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length <= maximum ? text : text.Substring(0, maximum);
        }

        private static string Host(string url)
        {
            Uri uri;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out uri) && !String.IsNullOrWhiteSpace(uri.Host)
                ? uri.Host.ToLowerInvariant() : "无法解析域名";
        }

        private static string AnonymousId(string value)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                return BitConverter.ToString(bytes, 0, 6).Replace("-", "");
            }
        }
    }
}
