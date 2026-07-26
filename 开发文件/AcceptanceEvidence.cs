using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LinkDispositionChecker
{
    internal sealed class ContractAcceptanceView
    {
        public string ContentStatus { get; set; }
        public string PublicReachability { get; set; }
        public string AcceptanceRecommendation { get; set; }
        public string EvidenceGrade { get; set; }
        public string SupplierAction { get; set; }
        public bool ContentResolved { get; set; }
        public bool RequiresIndependentNetworkReview { get; set; }
        public bool RequiresSupplement { get; set; }
    }

    internal static class ContractAcceptanceClassifier
    {
        internal static ContractAcceptanceView Evaluate(CheckResult item)
        {
            if (item == null) return Unknown("未知", "无结果", "D级—无核验结果", "重新运行核验", false);
            string verdict = (item.Verdict ?? "").Trim();
            if (verdict == "已失效")
            {
                return new ContractAcceptanceView
                {
                    ContentStatus = "明确失效",
                    PublicReachability = "不可访问（明确失效）",
                    AcceptanceRecommendation = "不通过—明确失效",
                    EvidenceGrade = "A级—目标失效证据",
                    SupplierAction = "如有异议，提供可由普通公众网络复现的原链接和当前正文证据",
                    ContentResolved = true,
                    RequiresIndependentNetworkReview = false,
                    RequiresSupplement = false
                };
            }
            if (verdict == "仍可访问")
            {
                return new ContractAcceptanceView
                {
                    ContentStatus = "正文仍在",
                    PublicReachability = "可访问",
                    AcceptanceRecommendation = "通过",
                    EvidenceGrade = "A级—目标正文证据",
                    SupplierAction = "",
                    ContentResolved = true,
                    RequiresIndependentNetworkReview = false,
                    RequiresSupplement = false
                };
            }
            if (verdict == "公网不可访问")
            {
                return new ContractAcceptanceView
                {
                    ContentStatus = "未知",
                    PublicReachability = "自动多线路不可访问",
                    AcceptanceRecommendation = "待独立普通网络复核—尚不能归责供应商",
                    EvidenceGrade = "C级—自动线路不可达证据",
                    SupplierAction = "先由独立家庭宽带或移动网络复核；复核仍失败且控制链接正常后，再要求供应商补证",
                    ContentResolved = false,
                    RequiresIndependentNetworkReview = true,
                    RequiresSupplement = true
                };
            }
            if (verdict == "暂时异常")
                return Unknown("访问异常待重试", "暂缓—先重试自动线路",
                    "D级—临时线路证据", "线路恢复后重新核验；本次不能用于归责", false);

            string combined = (item.StatusCode ?? "") + " " + (item.Evidence ?? "");
            bool restricted = Regex.IsMatch(combined,
                "登录|验证码|扫码|App|客户端|风控|403|429|captcha|verify you are human",
                RegexOptions.IgnoreCase);
            return Unknown(restricted ? "登录或风控受限" : "已响应但内容证据不足",
                "待供应商补证/人工复核",
                "C级—页面证据不足",
                "提供可由普通公众网络复现的原链接、目标内容编号、正文及发布账号状态",
                true);
        }

        private static ContractAcceptanceView Unknown(string reachability, string recommendation,
            string grade, string action, bool supplement)
        {
            return new ContractAcceptanceView
            {
                ContentStatus = "未知",
                PublicReachability = reachability,
                AcceptanceRecommendation = recommendation,
                EvidenceGrade = grade,
                SupplierAction = action,
                ContentResolved = false,
                RequiresIndependentNetworkReview = false,
                RequiresSupplement = supplement
            };
        }

        internal static void Apply(CheckResult item)
        {
            if (item == null) return;
            ContractAcceptanceView view = Evaluate(item);
            item.ContentStatus = view.ContentStatus;
            item.PublicReachability = view.PublicReachability;
            item.AcceptanceRecommendation = view.AcceptanceRecommendation;
            item.EvidenceGrade = view.EvidenceGrade;
            item.SupplierAction = view.SupplierAction;
        }

        internal static bool IsContentResolved(CheckResult item)
        {
            return Evaluate(item).ContentResolved;
        }
    }

    internal sealed class AcceptanceEvidencePackage
    {
        public string DirectoryPath { get; set; }
        public string ZipPath { get; set; }
        public string BatchId { get; set; }
        public int Total { get; set; }
        public int ContentResolved { get; set; }
        public int IndependentReviewRequired { get; set; }
        public int SupplementRequired { get; set; }
        public string EnvironmentAssessment { get; set; }
    }

    internal static class AcceptanceEvidencePackageWriter
    {
        private static readonly object SyncRoot = new object();
        internal static readonly string LatestPointerPath =
            Path.Combine(StoragePaths.ResolveResultsDirectory(), "最近一次验收证据包.txt");

        internal static AcceptanceEvidencePackage Write(IEnumerable<CheckResult> source, string runId)
        {
            return WriteToBaseDirectory(source, runId, StoragePaths.ResolveResultsDirectory(),
                ApplicationPath(), PlatformRules.RulesPath);
        }

        internal static AcceptanceEvidencePackage WriteToBaseDirectory(IEnumerable<CheckResult> source,
            string runId, string baseDirectory, string executablePath, string rulesPath)
        {
            lock (SyncRoot)
            {
                if (String.IsNullOrWhiteSpace(baseDirectory))
                    throw new ArgumentException("验收证据包目录不能为空。", "baseDirectory");
                List<CheckResult> rows = (source ?? Enumerable.Empty<CheckResult>())
                    .Where(item => item != null).OrderBy(item => item.Number).ToList();
                foreach (CheckResult row in rows) ContractAcceptanceClassifier.Apply(row);

                Directory.CreateDirectory(baseDirectory);
                string safeRunId = SafeFilePart(String.IsNullOrWhiteSpace(runId)
                    ? "RUN-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") : runId);
                string folderName = "验收证据包_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + safeRunId;
                string directory = Path.Combine(baseDirectory, folderName);
                int suffix = 1;
                while (Directory.Exists(directory) || File.Exists(directory + ".zip"))
                    directory = Path.Combine(baseDirectory, folderName + "_" + (++suffix));
                Directory.CreateDirectory(directory);

                string summaryPath = Path.Combine(directory, "01_验收汇总.csv");
                string supplementPath = Path.Combine(directory, "02_待补证清单.csv");
                string independentPath = Path.Combine(directory, "03_独立普通网络复核清单.csv");
                string methodPath = Path.Combine(directory, "04_环境与方法说明.txt");
                WriteSummaryCsv(summaryPath, rows);
                WriteSupplementCsv(supplementPath, rows);
                WriteIndependentReviewCsv(independentPath, rows, safeRunId);

                int resolved = rows.Count(ContractAcceptanceClassifier.IsContentResolved);
                int independent = rows.Count(item =>
                    ContractAcceptanceClassifier.Evaluate(item).RequiresIndependentNetworkReview);
                int supplement = rows.Count(item =>
                {
                    ContractAcceptanceView view = ContractAcceptanceClassifier.Evaluate(item);
                    return view.RequiresSupplement && !view.RequiresIndependentNetworkReview;
                });
                string environment = EnvironmentAssessment(rows);
                WriteMethod(methodPath, rows, safeRunId, environment, executablePath, rulesPath);

                string manifestPath = Path.Combine(directory, "SHA256SUMS.txt");
                WriteManifest(manifestPath, new[] { summaryPath, supplementPath, independentPath, methodPath });
                string zipPath = directory + ".zip";
                ZipFile.CreateFromDirectory(directory, zipPath, CompressionLevel.Optimal, true);
                string latestPointerPath = Path.Combine(baseDirectory, "最近一次验收证据包.txt");
                File.WriteAllText(latestPointerPath,
                    "批次：" + safeRunId + Environment.NewLine +
                    "目录：" + directory + Environment.NewLine +
                    "压缩包：" + zipPath + Environment.NewLine +
                    "生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                    new UTF8Encoding(true));
                return new AcceptanceEvidencePackage
                {
                    DirectoryPath = directory,
                    ZipPath = zipPath,
                    BatchId = safeRunId,
                    Total = rows.Count,
                    ContentResolved = resolved,
                    IndependentReviewRequired = independent,
                    SupplementRequired = supplement,
                    EnvironmentAssessment = environment
                };
            }
        }

        internal static List<CheckResult> SelectIndependentSamples(IEnumerable<CheckResult> source, int maximum)
        {
            int limit = Math.Max(0, maximum);
            var groups = (source ?? Enumerable.Empty<CheckResult>())
                .Where(item => item != null &&
                    ContractAcceptanceClassifier.Evaluate(item).RequiresIndependentNetworkReview)
                .GroupBy(item => String.IsNullOrWhiteSpace(item.InfrastructureKey)
                    ? "无基础设施分组" : item.InfrastructureKey, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Select(group => new Queue<CheckResult>(
                    RoundRobinByHost(group.OrderBy(item => item.Number)).ToList()))
                .ToList();
            var selected = new List<CheckResult>();
            while (selected.Count < limit && groups.Any(queue => queue.Count > 0))
            {
                foreach (Queue<CheckResult> queue in groups)
                {
                    if (selected.Count >= limit) break;
                    if (queue.Count > 0) selected.Add(queue.Dequeue());
                }
            }
            return selected;
        }

        private static IEnumerable<CheckResult> RoundRobinByHost(IEnumerable<CheckResult> source)
        {
            var queues = source.GroupBy(item => Host(item.OriginalUrl), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
                .Select(group => new Queue<CheckResult>(group.OrderBy(item => item.Number))).ToList();
            while (queues.Any(queue => queue.Count > 0))
            {
                foreach (Queue<CheckResult> queue in queues)
                    if (queue.Count > 0) yield return queue.Dequeue();
            }
        }

        private static void WriteSummaryCsv(string path, List<CheckResult> rows)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("序号,内容状态,公开可访问性,合同验收建议,证据等级,核验结果,HTTP状态,平台,内容类型,发文作者,页面标题,原链接,最终地址,判定依据,取证线路,站点对照,基础设施,核验时间,耗时,供应商行动");
                foreach (CheckResult row in rows)
                {
                    ContractAcceptanceView view = ContractAcceptanceClassifier.Evaluate(row);
                    writer.WriteLine(String.Join(",", new[]
                    {
                        row.Number.ToString(), Csv(view.ContentStatus), Csv(view.PublicReachability),
                        Csv(view.AcceptanceRecommendation), Csv(view.EvidenceGrade), Csv(row.Verdict),
                        Csv(row.StatusCode), Csv(row.Platform), Csv(row.ContentType), Csv(row.ExpectedAuthor),
                        Csv(row.Title), Csv(row.OriginalUrl), Csv(row.FinalUrl), Csv(row.Evidence),
                        Csv(row.AcquisitionAttempts), Csv(row.SiteHealth), Csv(row.InfrastructureKey),
                        Csv(row.CheckedAt), Csv(row.Duration), Csv(view.SupplierAction)
                    }));
                }
            }
        }

        private static void WriteSupplementCsv(string path, List<CheckResult> rows)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("序号,当前阶段,内容状态,公开可访问性,合同验收建议,证据等级,原链接,平台,发文作者,HTTP状态,站点对照,基础设施,当前证据,下一步,核验时间");
                foreach (CheckResult row in rows.Where(item =>
                    !ContractAcceptanceClassifier.IsContentResolved(item)))
                {
                    ContractAcceptanceView view = ContractAcceptanceClassifier.Evaluate(row);
                    string stage = view.RequiresIndependentNetworkReview
                        ? "先做独立普通网络复核" :
                        view.RequiresSupplement ? "供应商补证/人工复核" : "先重试自动线路";
                    writer.WriteLine(String.Join(",", new[]
                    {
                        row.Number.ToString(), Csv(stage), Csv(view.ContentStatus), Csv(view.PublicReachability),
                        Csv(view.AcceptanceRecommendation), Csv(view.EvidenceGrade), Csv(row.OriginalUrl),
                        Csv(row.Platform), Csv(row.ExpectedAuthor), Csv(row.StatusCode), Csv(row.SiteHealth),
                        Csv(row.InfrastructureKey), Csv(row.Evidence), Csv(view.SupplierAction), Csv(row.CheckedAt)
                    }));
                }
            }
        }

        private static void WriteIndependentReviewCsv(string path, List<CheckResult> rows, string batchId)
        {
            List<CheckResult> targets = SelectIndependentSamples(rows, 30);
            List<CheckResult> controls = rows.Where(item => item.Verdict == "仍可访问")
                .GroupBy(item => Host(item.OriginalUrl), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).Take(5).ToList();
            var homepages = targets.Select(item => Homepage(item.OriginalUrl))
                .Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(15).ToList();
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("批次,类型,样本序号,基础设施,域名,链接,当前工具结果,当前工具证据,普通网络A结果,普通网络B结果,控制链接是否正常,截图文件,复核人,复核时间,备注");
                int index = 1;
                foreach (CheckResult item in targets)
                    WriteIndependentRow(writer, batchId, "目标样本", index++, item.InfrastructureKey,
                        Host(item.OriginalUrl), item.OriginalUrl, item.Verdict, item.Evidence);
                foreach (string homepage in homepages)
                    WriteIndependentRow(writer, batchId, "同站首页控制", index++, "", Host(homepage),
                        homepage, "", "用于判断整个站点是否公开可访问");
                foreach (CheckResult item in controls)
                    WriteIndependentRow(writer, batchId, "批次已确认正常控制", index++, item.InfrastructureKey,
                        Host(item.OriginalUrl), item.OriginalUrl, item.Verdict,
                        "当前批次已取得目标正文；独立复核时应能正常打开");
            }
        }

        private static void WriteIndependentRow(StreamWriter writer, string batchId, string type,
            int index, string infrastructure, string host, string url, string verdict, string evidence)
        {
            writer.WriteLine(String.Join(",", new[]
            {
                Csv(batchId), Csv(type), index.ToString(), Csv(infrastructure), Csv(host), Csv(url),
                Csv(verdict), Csv(evidence), Csv(""), Csv(""), Csv(""), Csv(""), Csv(""), Csv(""), Csv("")
            }));
        }

        private static void WriteMethod(string path, List<CheckResult> rows, string batchId,
            string environment, string executablePath, string rulesPath)
        {
            int removed = rows.Count(item => item.Verdict == "已失效");
            int alive = rows.Count(item => item.Verdict == "仍可访问");
            int unavailable = rows.Count(item => item.Verdict == "公网不可访问");
            int temporary = rows.Count(item => item.Verdict == "暂时异常");
            int other = rows.Count - removed - alive - unavailable - temporary;
            var lines = new List<string>
            {
                "链接核验工具 " + SessionStore.CurrentEngineVersion + " - 合同验收环境与方法说明",
                "==========================================",
                "",
                "批次编号：" + batchId,
                "生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                "工具版本：" + SessionStore.CurrentEngineVersion,
                "主程序 SHA-256：" + HashIfExists(executablePath),
                "平台规则 SHA-256：" + HashIfExists(rulesPath),
                "",
                "一、批次结果",
                "------------",
                "总数：" + rows.Count,
                "明确失效：" + removed,
                "正文仍在：" + alive,
                "自动多线路不可访问：" + unavailable,
                "访问异常待重试：" + temporary,
                "其他证据不足：" + other,
                "内容状态已确认：" + (removed + alive),
                "内容状态确认率：" + (rows.Count == 0 ? "0.00%" :
                    ((removed + alive) * 100.0 / rows.Count).ToString("0.00") + "%"),
                "",
                "二、工具环境判断",
                "----------------",
                environment,
                "",
                "三、不能越过的证据边界",
                "----------------------",
                "1. “自动多线路不可访问”不等于内容删除，也不自动归责供应商。",
                "2. 当前内置公开云线路可能属于数据中心网络，不能代替家庭宽带或移动网络。",
                "3. 一次普通网络成功访问可以证明内容当时仍在；一次失败不能单独证明删除。",
                "4. 只有控制链接正常、目标链接在独立普通网络仍失败，才能支持“公开访问异常”。",
                "5. 合同是否据此判不通过，应以合同关于“公开可访问”的约定和验收程序为准。",
                "",
                "四、独立普通网络复核方法",
                "----------------------",
                "1. 将 03_独立普通网络复核清单.csv 交给不使用当前办公网络的复核人。",
                "2. 至少使用一条家庭宽带或移动网络；争议较大时使用两条不同运营商线路。",
                "3. 使用普通 Chrome、Firefox 或 Edge，无供应商 VPN、白名单或专属登录态。",
                "4. 先打开“批次已确认正常控制”和“同站首页控制”，再打开目标样本。",
                "5. 保存浏览器截图，记录复核人、时间、网络类型和结果。",
                "6. 如果控制链接也失败，本次复核无效；如果目标正文可见，应立即改判为正文仍在。",
                "",
                "五、供应商补证要求",
                "----------------",
                "供应商应提供普通公众网络可复现的原链接、目标内容编号、正文或媒体资源、发布账号和时间。",
                "只有供应商账号、VPN、白名单或内部网络可以访问时，不能单独证明公众可访问。",
                "无法复现的单张截图只能作为线索，不代替当前公开访问证据。",
                "",
                "六、文件完整性",
                "------------",
                "SHA256SUMS.txt 记录本证据包核心文件的哈希。保存或发送后可重新计算并比对，发现文件是否被修改。"
            };
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
        }

        private static string EnvironmentAssessment(List<CheckResult> rows)
        {
            List<CheckResult> resolved = rows.Where(ContractAcceptanceClassifier.IsContentResolved).ToList();
            int hosts = resolved.Select(item => Host(item.OriginalUrl))
                .Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int infrastructures = resolved.Select(item => String.IsNullOrWhiteSpace(item.InfrastructureKey)
                    ? Host(item.OriginalUrl) : item.InfrastructureKey)
                .Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (resolved.Count >= 5 && hosts >= 3 && infrastructures >= 2)
                return "批次内有 " + resolved.Count + " 条在 " + hosts + " 个域名、" + infrastructures +
                    " 个基础设施取得正文或明确失效证据，可排除“工具完全无法联网或完全失效”。" +
                    "这仍不能单独排除某一共享基础设施对数据中心网络的限制，相关记录必须完成独立普通网络复核。";
            return "本批次仅有 " + resolved.Count + " 条内容状态确认，涉及 " + hosts + " 个域名、" +
                infrastructures + " 个基础设施；控制证据不足，不能据此排除工具或当前网络环境问题。";
        }

        private static void WriteManifest(string path, IEnumerable<string> files)
        {
            var lines = new List<string>();
            foreach (string file in files.Where(File.Exists))
                lines.Add(HashFile(file) + "  " + Path.GetFileName(file));
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        private static string HashIfExists(string path)
        {
            return !String.IsNullOrWhiteSpace(path) && File.Exists(path) ? HashFile(path) : "未找到";
        }

        private static string HashFile(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (Stream stream = File.OpenRead(path))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "");
        }

        private static string ApplicationPath()
        {
            try { return System.Windows.Forms.Application.ExecutablePath; }
            catch { return ""; }
        }

        private static string Host(string url)
        {
            Uri uri;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out uri)
                ? (uri.Host ?? "").Trim().ToLowerInvariant() : "";
        }

        private static string Homepage(string url)
        {
            Uri uri;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out uri)
                ? uri.Scheme + "://" + uri.Authority + "/" : "";
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\"\"")
                .Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        private static string SafeFilePart(string value)
        {
            string text = Regex.Replace(value ?? "", @"[^A-Za-z0-9_\-]+", "_").Trim('_');
            return text.Length == 0 ? "RUN" : text;
        }
    }
}
