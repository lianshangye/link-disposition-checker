using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("链接失效检测工具")]
[assembly: AssemblyProduct("链接失效检测工具")]
[assembly: AssemblyVersion("4.5.5.0")]
[assembly: AssemblyFileVersion("4.5.5.0")]

namespace LinkDispositionChecker
{
    internal sealed class CheckResult
    {
        public int Number { get; set; }
        public string Verdict { get; set; }
        public string StatusCode { get; set; }
        public string Title { get; set; }
        public string OriginalUrl { get; set; }
        public string FinalUrl { get; set; }
        public string Evidence { get; set; }
        public string CheckedAt { get; set; }
        public string Duration { get; set; }
        public string TaskStartedAt { get; set; }
        public string TaskCompletedAt { get; set; }
        public string TaskElapsed { get; set; }
        public string ExpectedTitle { get; set; }
        public string ExpectedExcerpt { get; set; }
        public string ExpectedAuthor { get; set; }
        public string Platform { get; set; }
        public string ContentType { get; set; }
        public bool SkipDeepReview { get; set; }
        public string SourceSheet { get; set; }
        public int SourceRow { get; set; }
        public bool DeepReviewed { get; set; }
        public bool EdgeFastReviewed { get; set; }
        public List<VerificationEvidence> EvidenceTrail { get; set; }
        public string AnalysisContext { get; set; }
        public bool AiReviewed { get; set; }
        public string AiDecision { get; set; }
        public double AiConfidence { get; set; }
        public string AiModel { get; set; }
        public int AiAttemptCount { get; set; }
        public string AiLastError { get; set; }
        public string EvidenceStage { get; set; }
        public string AcquisitionAttempts { get; set; }
        public string SiteHealth { get; set; }
        public string InfrastructureKey { get; set; }
        public string ContentStatus { get; set; }
        public string PublicReachability { get; set; }
        public string AcceptanceRecommendation { get; set; }
        public string EvidenceGrade { get; set; }
        public string SupplierAction { get; set; }

        [ScriptIgnore]
        public string DisplayVerdict
        {
            get
            {
                if (String.Equals(Verdict, "仍可访问", StringComparison.OrdinalIgnoreCase)) return "有效";
                if (String.Equals(Verdict, "已失效", StringComparison.OrdinalIgnoreCase)) return "失效";
                return "未完成";
            }
        }
    }

    internal sealed class RenderedPageData
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string Text { get; set; }
        public string Html { get; set; }
        public string MainText { get; set; }
        public string MainHtml { get; set; }
        public string ObservedUrls { get; set; }
    }

    internal sealed class DeepDecision
    {
        public bool Resolved { get; set; }
        public bool NeedsVerification { get; set; }
        public string Verdict { get; set; }
        public string Evidence { get; set; }
    }

    internal enum EvidenceKind
    {
        TargetContentPresent,
        TargetRemovalExplicit,
        TargetRedirectedAway,
        AccessRestricted,
        TemporaryFailure,
        GenericPage,
        IdentityOnly
    }

    internal enum EvidenceStrength
    {
        Weak,
        Supporting,
        Strong,
        Conclusive
    }

    internal sealed class VerificationEvidence
    {
        public EvidenceKind Kind { get; set; }
        public EvidenceStrength Strength { get; set; }
        public string Source { get; set; }
        public string Platform { get; set; }
        public string TargetId { get; set; }
        public string Message { get; set; }
        public string FinalUrl { get; set; }
        public bool IsCurrentResponse { get; set; }
    }

    internal static class EvidenceAdjudicator
    {
        public static DeepDecision Decide(IEnumerable<VerificationEvidence> source)
        {
            List<VerificationEvidence> evidences = (source ?? Enumerable.Empty<VerificationEvidence>())
                .Where(item => item != null).ToList();
            VerificationEvidence present = Best(evidences, EvidenceKind.TargetContentPresent, EvidenceStrength.Strong, true);
            VerificationEvidence removed = Best(evidences, EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Strong, true);
            VerificationEvidence redirected = Best(evidences, EvidenceKind.TargetRedirectedAway, EvidenceStrength.Conclusive, true);

            if (present != null && (removed != null || redirected != null))
                return Review("目标内容存在与失效证据互相冲突，已保留人工复核");
            if (present != null) return Resolve("仍可访问", present.Message);
            if (removed != null) return Resolve("已失效", removed.Message);
            if (redirected != null) return Resolve("已失效", redirected.Message);

            VerificationEvidence restriction = Best(evidences, EvidenceKind.AccessRestricted, EvidenceStrength.Weak, false);
            if (restriction != null)
            {
                DeepDecision decision = Review(restriction.Message);
                decision.NeedsVerification = true;
                return decision;
            }
            VerificationEvidence temporary = Best(evidences, EvidenceKind.TemporaryFailure, EvidenceStrength.Weak, false);
            if (temporary != null) return Review(temporary.Message);
            VerificationEvidence generic = Best(evidences, EvidenceKind.GenericPage, EvidenceStrength.Weak, false);
            if (generic != null) return Review(generic.Message);
            VerificationEvidence identity = Best(evidences, EvidenceKind.IdentityOnly, EvidenceStrength.Weak, false);
            if (identity != null) return Review(identity.Message);
            return Review("尚未取得足够的目标内容证据");
        }

        private static VerificationEvidence Best(List<VerificationEvidence> evidences, EvidenceKind kind,
            EvidenceStrength minimum, bool requireCurrent)
        {
            return evidences.Where(item => item.Kind == kind && item.Strength >= minimum &&
                    (!requireCurrent || item.IsCurrentResponse))
                .OrderByDescending(item => item.Strength).FirstOrDefault();
        }

        private static DeepDecision Resolve(string verdict, string evidence)
        {
            return new DeepDecision { Resolved = true, Verdict = verdict, Evidence = evidence ?? "" };
        }

        private static DeepDecision Review(string evidence)
        {
            return new DeepDecision { Resolved = false, Verdict = "人工复核", Evidence = evidence ?? "" };
        }
    }

    internal sealed class EdgeFetchedResponse
    {
        public int StatusCode { get; set; }
        public string Body { get; set; }
        public string ContentType { get; set; }
        public string Error { get; set; }
    }

    internal sealed class RemoteEvidenceSettings
    {
        public int Version { get; set; }
        public string[] Endpoints { get; set; }
    }

    internal sealed class RemoteEvidenceResponse
    {
        public int Status { get; set; }
        public string FinalUrl { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
        public string Html { get; set; }
        public string Source { get; set; }
        public string Error { get; set; }
        public bool TargetUnreachable { get; set; }
    }

    internal static class RemoteEvidenceStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 1000000 };
        internal static readonly string SettingsPath =
            Path.Combine(StoragePaths.UserDataDirectory, "remote-evidence.json");

        internal static List<string> LoadEndpoints()
        {
            var endpoints = new List<string>();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    RemoteEvidenceSettings settings = Serializer.Deserialize<RemoteEvidenceSettings>(
                        File.ReadAllText(SettingsPath, Encoding.UTF8));
                    if (settings != null && settings.Endpoints != null) endpoints.AddRange(settings.Endpoints);
                }
            }
            catch { }
            string environment = Environment.GetEnvironmentVariable("LINK_CHECKER_REMOTE_EVIDENCE_ENDPOINTS") ?? "";
            if (!String.IsNullOrWhiteSpace(environment))
                endpoints.AddRange(environment.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            return endpoints.Select(item => (item ?? "").Trim())
                .Where(item =>
                {
                    Uri uri;
                    return Uri.TryCreate(item.Replace("{url}", "https%3A%2F%2Fexample.com"), UriKind.Absolute, out uri) &&
                        uri.Scheme == Uri.UriSchemeHttps;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
        }

        internal static void SaveEndpoints(IEnumerable<string> endpoints)
        {
            List<string> validated = (endpoints ?? Enumerable.Empty<string>())
                .Select(item => (item ?? "").Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
            foreach (string endpoint in validated)
            {
                Uri uri;
                string check = endpoint.Replace("{url}", "https%3A%2F%2Fexample.com");
                if (!Uri.TryCreate(check, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("远程取证节点必须使用 HTTPS：" + endpoint);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, Serializer.Serialize(new RemoteEvidenceSettings
            {
                Version = 1,
                Endpoints = validated.ToArray()
            }), new UTF8Encoding(false));
            if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, null);
            else File.Move(temporary, SettingsPath);
        }
    }

    internal sealed class ExcelLinkSource
    {
        public string Url { get; set; }
        public int Row { get; set; }
        public string ExpectedTitle { get; set; }
        public string ExpectedExcerpt { get; set; }
        public string ExpectedAuthor { get; set; }
        public string Platform { get; set; }
        public string ContentType { get; set; }
        public bool ManualOnly { get; set; }
    }

    internal sealed class ExcelSheetPlan
    {
        public string SheetName { get; set; }
        public int HeaderRow { get; set; }
        public int LinkColumn { get; set; }
        public int ResultStartColumn { get; set; }
        public List<ExcelLinkSource> Sources { get; set; }
    }

    internal sealed class CheckSession
    {
        public int Version { get; set; }
        public string EngineVersion { get; set; }
        public string SavedAt { get; set; }
        public string InputText { get; set; }
        public string ExcelPath { get; set; }
        public List<CheckJob> Jobs { get; set; }
        public List<CheckResult> Results { get; set; }
    }

    internal sealed class CheckJob
    {
        public int Number { get; set; }
        public string Url { get; set; }
        public string ExpectedTitle { get; set; }
        public string ExpectedExcerpt { get; set; }
        public string ExpectedAuthor { get; set; }
        public string Platform { get; set; }
        public string ContentType { get; set; }
        public string SourceSheet { get; set; }
        public int SourceRow { get; set; }
        public string InfrastructureKey { get; set; }

        public string Key
        {
            get
            {
                return CheckIdentity.Create(SourceSheet, SourceRow, Url);
            }
        }
    }

    internal static class CheckIdentity
    {
        public static string Create(string sourceSheet, int sourceRow, string url)
        {
            string normalizedUrl = (url ?? "").Trim();
            return !String.IsNullOrEmpty(sourceSheet) && sourceRow > 0
                ? sourceSheet + "\n" + sourceRow + "\n" + normalizedUrl
                : normalizedUrl;
        }
    }

    internal sealed class PlatformRule
    {
        public string Name { get; set; }
        public string[] Domains { get; set; }
        public string LoginUrl { get; set; }
        public bool DynamicShell { get; set; }
        public string ReviewTier { get; set; }
        public int MinimumWaitMilliseconds { get; set; }
        public int MaximumWaitMilliseconds { get; set; }
        public int NavigationTimeoutMilliseconds { get; set; }
        public string Limitation { get; set; }
        public string[] RemovedSignals { get; set; }
        public string[] RestrictedSignals { get; set; }
    }

    internal sealed class PlatformRuleSet
    {
        public int Version { get; set; }
        public PlatformRule[] Platforms { get; set; }
        public string[] RemovedSignals { get; set; }
        public string[] RestrictedSignals { get; set; }
    }

    internal static class PlatformRules
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 4000000 };
        private static PlatformRuleSet _rules;

        public static string RulesPath
        {
            get
            {
                string assemblyDirectory = Path.GetDirectoryName(typeof(PlatformRules).Assembly.Location);
                return Path.Combine(String.IsNullOrEmpty(assemblyDirectory) ? AppDomain.CurrentDomain.BaseDirectory : assemblyDirectory, "platform-rules.json");
            }
        }

        public static PlatformRule Find(Uri uri)
        {
            if (uri == null) return null;
            EnsureLoaded();
            return FindByHost(uri.Host);
        }

        public static bool AreSamePlatform(string firstHost, string secondHost)
        {
            EnsureLoaded();
            PlatformRule first = FindByHost(firstHost);
            PlatformRule second = FindByHost(secondHost);
            return first != null && Object.ReferenceEquals(first, second);
        }

        private static PlatformRule FindByHost(string hostValue)
        {
            string host = (hostValue ?? "").Trim('.').ToLowerInvariant();
            return (_rules.Platforms ?? new PlatformRule[0]).FirstOrDefault(rule =>
                (rule.Domains ?? new string[0]).Any(domain => HostMatches(host, domain)));
        }

        public static string FindRemovedSignal(string text, Uri uri)
        {
            EnsureLoaded();
            PlatformRule rule = Find(uri);
            return FindSignal(text, (_rules.RemovedSignals ?? new string[0]).Concat(rule == null ? new string[0] : rule.RemovedSignals ?? new string[0]));
        }

        public static string FindRestrictedSignal(string text, Uri uri)
        {
            EnsureLoaded();
            PlatformRule rule = Find(uri);
            return FindSignal(text, (_rules.RestrictedSignals ?? new string[0]).Concat(rule == null ? new string[0] : rule.RestrictedSignals ?? new string[0]));
        }

        private static void EnsureLoaded()
        {
            if (_rules != null) return;
            try
            {
                _rules = File.Exists(RulesPath)
                    ? Serializer.Deserialize<PlatformRuleSet>(File.ReadAllText(RulesPath, Encoding.UTF8))
                    : null;
            }
            catch { _rules = null; }
            if (_rules == null) _rules = new PlatformRuleSet { Version = 1, Platforms = new PlatformRule[0] };
        }

        private static bool HostMatches(string host, string domain)
        {
            string value = (domain ?? "").Trim().Trim('.').ToLowerInvariant();
            return value.Length > 0 && (host == value || host.EndsWith("." + value, StringComparison.Ordinal));
        }

        private static string FindSignal(string text, IEnumerable<string> signals)
        {
            string lower = (text ?? "").ToLowerInvariant();
            foreach (string signal in signals.Where(item => !String.IsNullOrWhiteSpace(item)))
                if (lower.IndexOf(signal.ToLowerInvariant(), StringComparison.Ordinal) >= 0) return signal;
            return "";
        }
    }

    internal sealed class PerformanceProfile
    {
        public string Name { get; set; }
        public int Workers { get; set; }
        public int GridRows { get; set; }
        public int BodyBytes { get; set; }
        public int RefreshMilliseconds { get; set; }

        public static PerformanceProfile Resolve(string selection)
        {
            if (selection == "低配模式") return new PerformanceProfile { Name = "低配", Workers = 1, GridRows = 700, BodyBytes = 240000, RefreshMilliseconds = 500 };
            if (selection == "标准模式") return new PerformanceProfile { Name = "标准", Workers = 3, GridRows = 2500, BodyBytes = 550000, RefreshMilliseconds = 260 };
            if (selection == "高性能模式") return new PerformanceProfile { Name = "高性能", Workers = 6, GridRows = 5000, BodyBytes = 900000, RefreshMilliseconds = 180 };
            if (!Environment.Is64BitProcess) return Resolve("低配模式");
            long memory = GetPhysicalMemoryBytes();
            int processors = Math.Max(1, Environment.ProcessorCount);
            if (processors <= 2 || (memory > 0 && memory <= 3L * 1024 * 1024 * 1024))
                return new PerformanceProfile { Name = "低配", Workers = 1, GridRows = 500, BodyBytes = 180000, RefreshMilliseconds = 650 };
            if (processors <= 4 || (memory > 0 && memory <= 5L * 1024 * 1024 * 1024)) return Resolve("低配模式");
            if (processors <= 8 || (memory > 0 && memory <= 10L * 1024 * 1024 * 1024)) return Resolve("标准模式");
            return Resolve("高性能模式");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private sealed class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

        private static long GetPhysicalMemoryBytes()
        {
            try { var status = new MemoryStatus(); return GlobalMemoryStatusEx(status) ? (long)status.TotalPhysical : 0L; }
            catch { return 0L; }
        }
    }

    internal static class StoragePaths
    {
        public static readonly string UserDataDirectory = ResolveWritableDirectory(new[]
        {
            Environment.GetEnvironmentVariable("LINK_CHECKER_TEST_DATA_DIR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkDispositionChecker"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "链接失效核验工具数据"),
            Path.Combine(Path.GetTempPath(), "LinkDispositionChecker")
        });

        public static string ResolveResultsDirectory()
        {
            return ResolveWritableDirectory(new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "核验结果"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "链接失效核验工具结果"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "链接失效核验工具结果"),
                Path.Combine(UserDataDirectory, "Results")
            });
        }

        public static string ResolveReportDirectory()
        {
            return ResolveWritableDirectory(new[]
            {
                Path.Combine(UserDataDirectory, "Reports"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Path.GetTempPath()
            });
        }

        private static string ResolveWritableDirectory(IEnumerable<string> candidates)
        {
            string fallback = candidates.FirstOrDefault(item => !String.IsNullOrWhiteSpace(item)) ?? Path.GetTempPath();
            foreach (string candidate in candidates.Where(item => !String.IsNullOrWhiteSpace(item)))
                if (CanWrite(candidate)) return candidate;
            return fallback;
        }

        private static bool CanWrite(string directory)
        {
            string probe = "";
            try
            {
                Directory.CreateDirectory(directory);
                probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "ok", Encoding.ASCII);
                File.Delete(probe);
                return true;
            }
            catch
            {
                try { if (probe.Length > 0 && File.Exists(probe)) File.Delete(probe); } catch { }
                return false;
            }
        }
    }

    internal static class RuntimeReport
    {
        public static string Write(string stage, Exception exception)
        {
            try
            {
                string directory = StoragePaths.ResolveReportDirectory();
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "运行异常报告_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                var lines = new List<string>
                {
                    "链接失效核验工具 - 运行异常报告",
                    "生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                    "工具版本：" + SessionStore.CurrentEngineVersion,
                    "发生阶段：" + (stage ?? "未知"),
                    "系统：" + Environment.OSVersion.VersionString,
                    "进程位数：" + (Environment.Is64BitProcess ? "64 位" : "32 位"),
                    "进度目录：" + RedactPath(StoragePaths.UserDataDirectory),
                    "",
                    "异常信息：",
                    Sanitize(Flatten(exception)),
                    "",
                    "说明：已完成的核验结果通常仍保存在断点进度中。本报告不主动写入导入文件内容、Cookie、账号或密码。"
                };
                File.WriteAllLines(path, lines, new UTF8Encoding(true));
                return path;
            }
            catch { return "未能生成异常报告"; }
        }

        private static string Flatten(Exception exception)
        {
            var parts = new List<string>();
            for (Exception current = exception; current != null && parts.Count < 6; current = current.InnerException)
            {
                string message = String.IsNullOrWhiteSpace(current.Message) ? "" : ": " + current.Message;
                parts.Add(current.GetType().FullName + message);
                if (!String.IsNullOrWhiteSpace(current.StackTrace)) parts.Add(current.StackTrace);
            }
            return String.Join(Environment.NewLine, parts);
        }

        private static string Sanitize(string value)
        {
            string text = value ?? "";
            text = Regex.Replace(text, @"https?://\S+", "[链接已隐藏]", RegexOptions.IgnoreCase);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!String.IsNullOrWhiteSpace(profile)) text = text.Replace(profile, "%USERPROFILE%");
            return text;
        }

        private static string RedactPath(string value)
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return !String.IsNullOrWhiteSpace(profile) && (value ?? "").StartsWith(profile, StringComparison.OrdinalIgnoreCase)
                ? "%USERPROFILE%" + value.Substring(profile.Length)
                : value ?? "";
        }
    }

    internal static class SessionStore
    {
        public const string CurrentEngineVersion = "4.5.5";
        private static readonly object SyncRoot = new object();
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        public static readonly string SessionPath = Path.Combine(StoragePaths.UserDataDirectory, "last-session.json");
        public static readonly string JournalPath = SessionPath + ".journal";
        public static readonly string BackupPath = SessionPath + ".bak";

        public static bool Exists { get { return File.Exists(SessionPath) || File.Exists(JournalPath) || File.Exists(BackupPath); } }

        public static void Save(string inputText, string excelPath, IEnumerable<CheckJob> jobs, IEnumerable<CheckResult> results)
        {
            lock (SyncRoot)
            {
                string directory = Path.GetDirectoryName(SessionPath);
                Directory.CreateDirectory(directory);
                string temporary = SessionPath + ".tmp";
                var session = new CheckSession
                {
                    Version = 1,
                    EngineVersion = CurrentEngineVersion,
                    SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    InputText = inputText ?? "",
                    ExcelPath = excelPath ?? "",
                    Jobs = (jobs ?? Enumerable.Empty<CheckJob>()).OrderBy(item => item.Number).ToList(),
                    Results = (results ?? Enumerable.Empty<CheckResult>()).OrderBy(item => item.Number).ToList()
                };
                File.WriteAllText(temporary, Serializer.Serialize(session), new UTF8Encoding(false));
                File.Copy(temporary, BackupPath, true);
                if (File.Exists(SessionPath)) File.Replace(temporary, SessionPath, null);
                else File.Move(temporary, SessionPath);
                File.WriteAllText(JournalPath, "", new UTF8Encoding(false));
            }
        }

        public static void Append(CheckResult result)
        {
            AppendBatch(result == null ? Enumerable.Empty<CheckResult>() : new[] { result });
        }

        public static void AppendBatch(IEnumerable<CheckResult> results)
        {
            List<CheckResult> batch = (results ?? Enumerable.Empty<CheckResult>()).Where(item => item != null).ToList();
            if (batch.Count == 0) return;
            lock (SyncRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SessionPath));
                var builder = new StringBuilder();
                foreach (CheckResult result in batch) builder.AppendLine(Serializer.Serialize(result));
                File.AppendAllText(JournalPath, builder.ToString(), new UTF8Encoding(false));
            }
        }

        public static CheckSession Load()
        {
            if (!Exists) return null;
            CheckSession session;
            lock (SyncRoot)
            {
                Exception primaryFailure;
                session = TryLoadSessionFile(SessionPath, out primaryFailure);
                if (session == null)
                {
                    Exception backupFailure;
                    session = TryLoadSessionFile(BackupPath, out backupFailure);
                    if (session == null && primaryFailure != null)
                        throw new InvalidDataException("主进度和备份均无法读取。", primaryFailure);
                }
                if (session == null) session = new CheckSession { Version = 1, Results = new List<CheckResult>() };
                if (File.Exists(JournalPath))
                {
                    var latest = new Dictionary<string, CheckResult>(StringComparer.OrdinalIgnoreCase);
                    foreach (CheckResult saved in session.Results ?? new List<CheckResult>())
                        if (saved != null) latest[ResultKey(saved)] = saved;
                    foreach (string line in File.ReadLines(JournalPath, Encoding.UTF8))
                    {
                        if (String.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            CheckResult result = Serializer.Deserialize<CheckResult>(line);
                            if (result != null)
                            {
                                string key = ResultKey(result);
                                CheckResult existing;
                                if (!latest.TryGetValue(key, out existing) || IsAtLeastAsRecent(result, existing)) latest[key] = result;
                            }
                        }
                        catch
                        {
                            // A crash can truncate only the final append. Earlier complete lines remain usable.
                        }
                    }
                    session.Results = latest.Values.OrderBy(item => item.Number).ToList();
                }
            }
            if (session == null || session.Version != 1) throw new InvalidDataException("进度文件版本不受支持。");
            if (session.Results == null) session.Results = new List<CheckResult>();
            return session;
        }

        private static string ResultKey(CheckResult result)
        {
            return result == null ? "" : CheckIdentity.Create(result.SourceSheet, result.SourceRow, result.OriginalUrl);
        }

        private static CheckSession TryLoadSessionFile(string path, out Exception failure)
        {
            failure = null;
            if (!File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                return String.IsNullOrWhiteSpace(json) ? null : Serializer.Deserialize<CheckSession>(json);
            }
            catch (Exception ex)
            {
                failure = ex;
                return null;
            }
        }

        private static bool IsAtLeastAsRecent(CheckResult candidate, CheckResult existing)
        {
            DateTime candidateTime;
            DateTime existingTime;
            bool candidateParsed = DateTime.TryParse(candidate == null ? "" : candidate.CheckedAt, out candidateTime);
            bool existingParsed = DateTime.TryParse(existing == null ? "" : existing.CheckedAt, out existingTime);
            if (candidateParsed && existingParsed) return candidateTime >= existingTime;
            if (existingParsed && !candidateParsed) return false;
            return true;
        }

        public static void Clear()
        {
            lock (SyncRoot)
            {
                foreach (string path in new[] { SessionPath, JournalPath, BackupPath, SessionPath + ".tmp" })
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
            }
        }

        public static string Describe()
        {
            try
            {
                CheckSession session = Load();
                if (session == null) return "";
                int total = session.Jobs != null && session.Jobs.Count > 0
                    ? session.Jobs.Count
                    : Regex.Matches(session.InputText ?? "", @"https?://[^\s\""'<>\uff0c\uff1b,]+", RegexOptions.IgnoreCase).Count;
                return session.SavedAt + "，已保存 " + session.Results.Count + " / " + total + " 条";
            }
            catch { return "存在进度文件，但文件可能已损坏"; }
        }
    }

    internal static class ExcelBridge
    {
        private static readonly Regex UrlPattern = new Regex(@"https?://[^\s\""'<>，；]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] ResultHeaders = new[]
        {
            "链接状态", "HTTP状态", "页面标题"
        };

        public static List<ExcelSheetPlan> LoadPlans(string path)
        {
            object excelObject = null;
            object workbookObject = null;
            var plans = new List<ExcelSheetPlan>();
            try
            {
                Type excelType = Type.GetTypeFromProgID("Excel.Application");
                if (excelType == null) throw new InvalidOperationException("未检测到 Microsoft Excel。");
                excelObject = Activator.CreateInstance(excelType);
                dynamic excel = excelObject;
                excel.Visible = false;
                excel.DisplayAlerts = false;
                workbookObject = excel.Workbooks.Open(path, 0, true);
                dynamic workbook = workbookObject;

                for (int sheetIndex = 1; sheetIndex <= workbook.Worksheets.Count; sheetIndex++)
                {
                    dynamic sheet = null;
                    dynamic used = null;
                    try
                    {
                        sheet = workbook.Worksheets[sheetIndex];
                        used = sheet.UsedRange;
                        int firstRow = Convert.ToInt32(used.Row);
                        int firstColumn = Convert.ToInt32(used.Column);
                        int rowCount = Convert.ToInt32(used.Rows.Count);
                        int columnCount = Convert.ToInt32(used.Columns.Count);
                        if (rowCount <= 0 || columnCount <= 0) continue;

                        object values = used.Value2;
                        object formulas = used.Formula;
                        var byColumn = new Dictionary<int, List<ExcelLinkSource>>();
                        var headerRows = new Dictionary<int, int>();

                        for (int r = 0; r < rowCount; r++)
                        {
                            for (int c = 0; c < columnCount; c++)
                            {
                                int absoluteRow = firstRow + r;
                                int absoluteColumn = firstColumn + c;
                                string valueText = Convert.ToString(ValueAt(values, r, c, rowCount, columnCount)) ?? "";
                                string formulaText = Convert.ToString(ValueAt(formulas, r, c, rowCount, columnCount)) ?? "";
                                if (r < 25 && IsLinkHeader(valueText) && !headerRows.ContainsKey(absoluteColumn))
                                    headerRows[absoluteColumn] = absoluteRow;

                                string url = ExtractFirstUrl(valueText);
                                if (String.IsNullOrEmpty(url)) url = ExtractFirstUrl(formulaText);
                                if (String.IsNullOrEmpty(url)) continue;
                                List<ExcelLinkSource> list;
                                if (!byColumn.TryGetValue(absoluteColumn, out list))
                                {
                                    list = new List<ExcelLinkSource>();
                                    byColumn[absoluteColumn] = list;
                                }
                                list.Add(new ExcelLinkSource { Url = url, Row = absoluteRow });
                            }
                        }

                        if (byColumn.Count == 0) continue;
                        int linkColumn = byColumn
                            .OrderByDescending(pair => headerRows.ContainsKey(pair.Key))
                            .ThenByDescending(pair => pair.Value.Count)
                            .First().Key;
                        List<ExcelLinkSource> sources = byColumn[linkColumn];
                        int firstUrlRow = sources.Min(source => source.Row);
                        int headerRow = headerRows.ContainsKey(linkColumn) ? headerRows[linkColumn] : Math.Max(firstRow, firstUrlRow - 1);
                        if (headerRow == firstUrlRow && firstUrlRow > 1) headerRow = firstUrlRow - 1;

                        int titleColumn = 0;
                        int headerOffset = headerRow - firstRow;
                        if (headerOffset >= 0 && headerOffset < rowCount)
                        {
                            for (int c = 0; c < columnCount; c++)
                            {
                                string headerText = Convert.ToString(ValueAt(values, headerOffset, c, rowCount, columnCount)) ?? "";
                                if (IsTitleHeader(headerText)) { titleColumn = firstColumn + c; break; }
                            }
                        }
                        if (titleColumn > 0)
                        {
                            foreach (ExcelLinkSource source in sources)
                            {
                                int rowOffset = source.Row - firstRow;
                                int columnOffset = titleColumn - firstColumn;
                                if (rowOffset >= 0 && rowOffset < rowCount && columnOffset >= 0 && columnOffset < columnCount)
                                    source.ExpectedTitle = Convert.ToString(ValueAt(values, rowOffset, columnOffset, rowCount, columnCount)) ?? "";
                            }
                        }

                        int excerptColumn = 0;
                        if (headerOffset >= 0 && headerOffset < rowCount)
                        {
                            for (int c = 0; c < columnCount; c++)
                            {
                                string headerText = Convert.ToString(ValueAt(values, headerOffset, c, rowCount, columnCount)) ?? "";
                                if (IsExcerptHeader(headerText)) { excerptColumn = firstColumn + c; break; }
                            }
                        }
                        if (excerptColumn > 0)
                        {
                            foreach (ExcelLinkSource source in sources)
                            {
                                int rowOffset = source.Row - firstRow;
                                int columnOffset = excerptColumn - firstColumn;
                                if (rowOffset >= 0 && rowOffset < rowCount && columnOffset >= 0 && columnOffset < columnCount)
                                    source.ExpectedExcerpt = Convert.ToString(ValueAt(values, rowOffset, columnOffset, rowCount, columnCount)) ?? "";
                            }
                        }

                        int authorColumn = 0;
                        if (headerOffset >= 0 && headerOffset < rowCount)
                        {
                            for (int c = 0; c < columnCount; c++)
                            {
                                string headerText = Convert.ToString(ValueAt(values, headerOffset, c, rowCount, columnCount)) ?? "";
                                if (IsAuthorHeader(headerText)) { authorColumn = firstColumn + c; break; }
                            }
                        }
                        if (authorColumn > 0)
                        {
                            foreach (ExcelLinkSource source in sources)
                            {
                                int rowOffset = source.Row - firstRow;
                                int columnOffset = authorColumn - firstColumn;
                                if (rowOffset >= 0 && rowOffset < rowCount && columnOffset >= 0 && columnOffset < columnCount)
                                    source.ExpectedAuthor = Convert.ToString(ValueAt(values, rowOffset, columnOffset, rowCount, columnCount)) ?? "";
                            }
                        }

                        int platformColumn = 0;
                        int contentTypeColumn = 0;
                        if (headerOffset >= 0 && headerOffset < rowCount)
                        {
                            for (int c = 0; c < columnCount; c++)
                            {
                                string headerText = Convert.ToString(ValueAt(values, headerOffset, c, rowCount, columnCount)) ?? "";
                                if (platformColumn == 0 && IsPlatformHeader(headerText)) platformColumn = firstColumn + c;
                                if (contentTypeColumn == 0 && IsContentTypeHeader(headerText)) contentTypeColumn = firstColumn + c;
                            }
                        }
                        foreach (ExcelLinkSource source in sources)
                        {
                            int rowOffset = source.Row - firstRow;
                            if (rowOffset < 0 || rowOffset >= rowCount) continue;
                            if (platformColumn > 0)
                                source.Platform = Convert.ToString(ValueAt(values, rowOffset, platformColumn - firstColumn, rowCount, columnCount)) ?? "";
                            if (contentTypeColumn > 0)
                                source.ContentType = Convert.ToString(ValueAt(values, rowOffset, contentTypeColumn - firstColumn, rowCount, columnCount)) ?? "";
                        }

                        int lastColumn = firstColumn + columnCount - 1;
                        int resultStart = FindExistingResultStart(values, firstRow, firstColumn, rowCount, columnCount, headerRow);
                        if (resultStart <= 0) resultStart = lastColumn + 1;

                        plans.Add(new ExcelSheetPlan
                        {
                            SheetName = Convert.ToString(sheet.Name),
                            HeaderRow = headerRow,
                            LinkColumn = linkColumn,
                            ResultStartColumn = resultStart,
                            Sources = sources.Where(source => source.Row > headerRow).ToList()
                        });
                    }
                    finally
                    {
                        ReleaseCom(used);
                        ReleaseCom(sheet);
                    }
                }
                return plans.Where(plan => plan.Sources.Count > 0).ToList();
            }
            finally
            {
                if (workbookObject != null)
                {
                    try { ((dynamic)workbookObject).Close(false); } catch { }
                }
                if (excelObject != null)
                {
                    try { ((dynamic)excelObject).Quit(); } catch { }
                }
                ReleaseCom(workbookObject);
                ReleaseCom(excelObject);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        public static string WriteResults(string path, List<ExcelSheetPlan> plans, IEnumerable<CheckResult> results)
        {
            string directory = Path.GetDirectoryName(path);
            string extension = Path.GetExtension(path);
            string backup = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "_核验前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + extension);
            File.Copy(path, backup, false);

            var resultMap = results.GroupBy(r => r.OriginalUrl, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            object excelObject = null;
            object workbookObject = null;
            bool saved = false;
            try
            {
                Type excelType = Type.GetTypeFromProgID("Excel.Application");
                if (excelType == null) throw new InvalidOperationException("未检测到 Microsoft Excel。");
                excelObject = Activator.CreateInstance(excelType);
                dynamic excel = excelObject;
                excel.Visible = false;
                excel.DisplayAlerts = false;
                workbookObject = excel.Workbooks.Open(path, 0, false);
                dynamic workbook = workbookObject;
                if (Convert.ToBoolean(workbook.ReadOnly)) throw new IOException("文件处于只读状态，请先关闭正在打开该文件的 Excel。");

                foreach (ExcelSheetPlan plan in plans)
                {
                    dynamic sheet = null;
                    dynamic sourceHeader = null;
                    dynamic headerRange = null;
                    dynamic outputRange = null;
                    try
                    {
                        sheet = workbook.Worksheets[plan.SheetName];
                        sourceHeader = sheet.Cells[plan.HeaderRow, plan.LinkColumn];
                        headerRange = sheet.Range[sheet.Cells[plan.HeaderRow, plan.ResultStartColumn], sheet.Cells[plan.HeaderRow, plan.ResultStartColumn + ResultHeaders.Length - 1]];
                        try
                        {
                            sourceHeader.Copy();
                            headerRange.PasteSpecial(-4122);
                            excel.CutCopyMode = false;
                        }
                        catch { }
                        for (int i = 0; i < ResultHeaders.Length; i++)
                            sheet.Cells[plan.HeaderRow, plan.ResultStartColumn + i].Value2 = ResultHeaders[i];

                        int maxRow = plan.Sources.Max(source => source.Row);
                        int dataStartRow = plan.HeaderRow + 1;
                        int dataRowCount = maxRow - dataStartRow + 1;
                        Array matrix = Array.CreateInstance(typeof(object), new[] { dataRowCount, ResultHeaders.Length }, new[] { 1, 1 });
                        foreach (ExcelLinkSource source in plan.Sources)
                        {
                            CheckResult result;
                            if (!resultMap.TryGetValue(source.Url, out result)) continue;
                            int rowOffset = source.Row - dataStartRow + 1;
                            matrix.SetValue(OpenXmlExcelBridge.ToExcelVerdict(result.Verdict), rowOffset, 1);
                            matrix.SetValue(result.StatusCode ?? "", rowOffset, 2);
                            matrix.SetValue(result.Title ?? "", rowOffset, 3);
                        }
                        outputRange = sheet.Range[sheet.Cells[dataStartRow, plan.ResultStartColumn], sheet.Cells[maxRow, plan.ResultStartColumn + ResultHeaders.Length - 1]];
                        outputRange.Value2 = matrix;
                        sheet.Columns[plan.ResultStartColumn].ColumnWidth = 15;
                        sheet.Columns[plan.ResultStartColumn + 1].ColumnWidth = 12;
                        sheet.Columns[plan.ResultStartColumn + 2].ColumnWidth = 42;
                        sheet.Columns[plan.ResultStartColumn + 2].WrapText = true;
                    }
                    finally
                    {
                        ReleaseCom(outputRange);
                        ReleaseCom(headerRange);
                        ReleaseCom(sourceHeader);
                        ReleaseCom(sheet);
                    }
                }
                workbook.Save();
                saved = true;
                return backup;
            }
            finally
            {
                if (workbookObject != null)
                {
                    try { ((dynamic)workbookObject).Close(saved); } catch { }
                }
                if (excelObject != null)
                {
                    try { ((dynamic)excelObject).Quit(); } catch { }
                }
                ReleaseCom(workbookObject);
                ReleaseCom(excelObject);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private static object ValueAt(object matrix, int row, int column, int rowCount, int columnCount)
        {
            if (rowCount == 1 && columnCount == 1) return matrix;
            Array array = matrix as Array;
            if (array == null) return null;
            return array.GetValue(row + array.GetLowerBound(0), column + array.GetLowerBound(1));
        }

        private static int FindExistingResultStart(object values, int firstRow, int firstColumn, int rowCount, int columnCount, int headerRow)
        {
            int rowOffset = headerRow - firstRow;
            if (rowOffset < 0 || rowOffset >= rowCount) return 0;
            for (int c = 0; c < columnCount; c++)
            {
                string value = Convert.ToString(ValueAt(values, rowOffset, c, rowCount, columnCount)) ?? "";
                string header = value.Trim();
                if (header == "链接状态" || header == "链接是否失效" || header == "核验结果" || header == "自动核验结果") return firstColumn + c;
            }
            return 0;
        }

        private static bool IsLinkHeader(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            return lower.Contains("链接") || lower.Contains("网址") || lower == "url" || lower.Contains("url地址") || lower == "link";
        }

        private static bool IsTitleHeader(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            return lower == "标题" || lower.Contains("内容标题") || lower.Contains("文章标题") || lower.Contains("作品标题") || lower == "title";
        }

        private static bool IsExcerptHeader(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            return lower == "摘要" || lower.Contains("内容摘要") || lower.Contains("正文摘要") || lower == "excerpt" || lower == "summary";
        }

        private static bool IsAuthorHeader(string value)
        {
            string lower = (value ?? "").Replace(" ", "").Trim().ToLowerInvariant();
            return lower == "账号昵称" || lower == "作者" || lower == "发文作者" ||
                lower == "发布账号" || lower == "发布人" || lower == "发布者" ||
                lower == "账号名称" || lower == "昵称" || lower == "账号" || lower == "author";
        }

        private static bool IsContentTypeHeader(string value)
        {
            string lower = (value ?? "").Replace(" ", "").Trim().ToLowerInvariant();
            return lower == "内容类型" || lower == "信息类型" || lower == "媒体类型" ||
                lower == "类型" || lower == "contenttype" || lower == "type";
        }

        private static bool IsPlatformHeader(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            return lower == "平台" || lower.Contains("发布平台") || lower.Contains("来源平台") || lower == "platform";
        }

        private static bool IsWechatChannelPlatform(string value)
        {
            string text = (value ?? "").Replace(" ", "").Trim();
            return text.IndexOf("视频号", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("微信视频", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExtractFirstUrl(string text)
        {
            Match match = UrlPattern.Match(text ?? "");
            return match.Success ? match.Value.Trim().TrimEnd('.', ',', ';', ')', ']', '}', '。', '，', '；') : "";
        }

        private static void ReleaseCom(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }
    }

    internal static class OpenXmlExcelBridge
    {
        private static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly Regex UrlPattern = new Regex(@"https?://[^\s\""'<>，；]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] ResultHeaders = new[]
        {
            "链接状态", "HTTP状态", "页面标题"
        };

        public static List<ExcelSheetPlan> LoadPlans(string path)
        {
            EnsureSupported(path);
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                List<string> sharedStrings = ReadSharedStrings(archive);
                Dictionary<string, string> sheetPaths = ReadSheetPaths(archive);
                var plans = new List<ExcelSheetPlan>();
                foreach (var sheetInfo in sheetPaths)
                {
                    ZipArchiveEntry entry = archive.GetEntry(sheetInfo.Value);
                    if (entry == null) continue;
                    XDocument document = LoadDocument(entry);
                    var cells = ReadCells(document, sharedStrings);
                    Dictionary<string, string> hyperlinks = ReadHyperlinks(archive, sheetInfo.Value, document);
                    if (cells.Count == 0) continue;
                    int minimumCellRow = cells.Min(item => item.Row);

                    var byColumn = new Dictionary<int, List<ExcelLinkSource>>();
                    var headerRows = new Dictionary<int, int>();
                    foreach (CellValue cell in cells)
                    {
                        if (cell.Row <= minimumCellRow + 24 && IsLinkHeader(cell.Text) && !headerRows.ContainsKey(cell.Column))
                            headerRows[cell.Column] = cell.Row;
                        string hyperlink;
                        string cellReference = ColumnName(cell.Column) + cell.Row;
                        string url = hyperlinks.TryGetValue(cellReference, out hyperlink) ? hyperlink : ExtractFirstUrl(cell.Text);
                        if (String.IsNullOrEmpty(url)) url = ExtractFirstUrl(cell.Formula);
                        if (String.IsNullOrEmpty(url)) continue;
                        List<ExcelLinkSource> list;
                        if (!byColumn.TryGetValue(cell.Column, out list))
                        {
                            list = new List<ExcelLinkSource>();
                            byColumn[cell.Column] = list;
                        }
                        list.Add(new ExcelLinkSource { Url = url, Row = cell.Row });
                    }
                    if (byColumn.Count == 0) continue;

                    int linkColumn = byColumn.OrderByDescending(pair => headerRows.ContainsKey(pair.Key)).ThenByDescending(pair => pair.Value.Count).First().Key;
                    List<ExcelLinkSource> sources = byColumn[linkColumn];
                    int firstRow = cells.Min(cell => cell.Row);
                    int firstUrlRow = sources.Min(source => source.Row);
                    int headerRow = headerRows.ContainsKey(linkColumn) ? headerRows[linkColumn] : Math.Max(firstRow, firstUrlRow - 1);
                    if (headerRow == firstUrlRow && firstUrlRow > 1) headerRow--;
                    int titleColumn = cells.Where(cell => cell.Row == headerRow && IsTitleHeader(cell.Text)).Select(cell => cell.Column).DefaultIfEmpty(0).First();
                    var titlesByRow = new Dictionary<int, string>();
                    if (titleColumn > 0)
                    {
                        titlesByRow = cells.Where(cell => cell.Column == titleColumn).GroupBy(cell => cell.Row)
                            .ToDictionary(group => group.Key, group => group.First().Text ?? "");
                        foreach (ExcelLinkSource source in sources)
                        {
                            string expectedTitle;
                            if (titlesByRow.TryGetValue(source.Row, out expectedTitle)) source.ExpectedTitle = expectedTitle;
                        }
                    }
                    int excerptColumn = cells.Where(cell => cell.Row == headerRow && IsExcerptHeader(cell.Text)).Select(cell => cell.Column).DefaultIfEmpty(0).First();
                    if (excerptColumn > 0)
                    {
                        var excerptsByRow = cells.Where(cell => cell.Column == excerptColumn).GroupBy(cell => cell.Row)
                            .ToDictionary(group => group.Key, group => group.First().Text ?? "");
                        foreach (ExcelLinkSource source in sources)
                        {
                            string excerpt;
                            if (excerptsByRow.TryGetValue(source.Row, out excerpt)) source.ExpectedExcerpt = excerpt;
                        }
                    }
                    int authorColumn = cells.Where(cell => cell.Row == headerRow && IsAuthorHeader(cell.Text)).Select(cell => cell.Column).DefaultIfEmpty(0).First();
                    var authorsByRow = new Dictionary<int, string>();
                    if (authorColumn > 0)
                    {
                        authorsByRow = cells.Where(cell => cell.Column == authorColumn).GroupBy(cell => cell.Row)
                            .ToDictionary(group => group.Key, group => group.First().Text ?? "");
                        foreach (ExcelLinkSource source in sources)
                        {
                            string expectedAuthor;
                            if (authorsByRow.TryGetValue(source.Row, out expectedAuthor)) source.ExpectedAuthor = expectedAuthor;
                        }
                    }
                    int contentTypeColumn = cells.Where(cell => cell.Row == headerRow && IsContentTypeHeader(cell.Text)).Select(cell => cell.Column).DefaultIfEmpty(0).First();
                    if (contentTypeColumn > 0)
                    {
                        var contentTypesByRow = cells.Where(cell => cell.Column == contentTypeColumn).GroupBy(cell => cell.Row)
                            .ToDictionary(group => group.Key, group => group.First().Text ?? "");
                        foreach (ExcelLinkSource source in sources)
                        {
                            string contentType;
                            if (contentTypesByRow.TryGetValue(source.Row, out contentType)) source.ContentType = contentType;
                        }
                    }
                    int platformColumn = cells.Where(cell => cell.Row == headerRow && IsPlatformHeader(cell.Text)).Select(cell => cell.Column).DefaultIfEmpty(0).First();
                    if (platformColumn > 0)
                    {
                        var platformsByRow = cells.Where(cell => cell.Column == platformColumn).GroupBy(cell => cell.Row)
                            .ToDictionary(group => group.Key, group => group.First().Text ?? "");
                        foreach (ExcelLinkSource source in sources)
                        {
                            string platform;
                            if (platformsByRow.TryGetValue(source.Row, out platform)) source.Platform = platform;
                        }
                        foreach (var platformRow in platformsByRow.Where(pair => pair.Key > headerRow && IsWechatChannelPlatform(pair.Value)))
                        {
                            if (sources.Any(source => source.Row == platformRow.Key)) continue;
                            string expectedTitle = "";
                            titlesByRow.TryGetValue(platformRow.Key, out expectedTitle);
                            sources.Add(new ExcelLinkSource
                            {
                                Url = "",
                                Row = platformRow.Key,
                                ExpectedTitle = expectedTitle ?? "",
                                ExpectedAuthor = authorsByRow.ContainsKey(platformRow.Key) ? authorsByRow[platformRow.Key] : "",
                                ContentType = contentTypeColumn > 0 && cells.Any(cell => cell.Row == platformRow.Key && cell.Column == contentTypeColumn)
                                    ? cells.First(cell => cell.Row == platformRow.Key && cell.Column == contentTypeColumn).Text ?? "" : "",
                                Platform = platformRow.Value,
                                ManualOnly = true
                            });
                        }
                    }
                    int existingStart = cells.Where(cell => cell.Row == headerRow &&
                        (cell.Text.Trim() == "链接状态" || cell.Text.Trim() == "链接是否失效" ||
                         cell.Text.Trim() == "核验结果" || cell.Text.Trim() == "自动核验结果"))
                        .Select(cell => cell.Column).DefaultIfEmpty(0).First();
                    int resultStart = existingStart > 0 ? existingStart : cells.Max(cell => cell.Column) + 1;

                    var plan = new ExcelSheetPlan
                    {
                        SheetName = sheetInfo.Key,
                        HeaderRow = headerRow,
                        LinkColumn = linkColumn,
                        ResultStartColumn = resultStart,
                        Sources = sources.Where(source => source.Row > headerRow).ToList()
                    };
                    if (plan.Sources.Count > 0) plans.Add(plan);
                }
                return plans;
            }
        }

        public static string WriteResults(string path, List<ExcelSheetPlan> plans, IEnumerable<CheckResult> results)
        {
            EnsureSupported(path);
            string directory = Path.GetDirectoryName(path);
            string extension = Path.GetExtension(path);
            string backup = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "_核验前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + extension);
            string temporary = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".writing." + Guid.NewGuid().ToString("N") + extension);
            File.Copy(path, backup, false);

            var resultRows = results.ToList();
            var resultsBySource = resultRows.Where(item => !String.IsNullOrEmpty(item.SourceSheet) && item.SourceRow > 0)
                .GroupBy(item => CheckIdentity.Create(item.SourceSheet, item.SourceRow, item.OriginalUrl), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var resultsByUrl = resultRows.Where(item => !String.IsNullOrEmpty(item.OriginalUrl))
                .GroupBy(item => item.OriginalUrl, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var replacements = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);

            using (var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
            {
                Dictionary<string, string> sheetPaths = ReadSheetPaths(archive);
                foreach (ExcelSheetPlan plan in plans)
                {
                    string sheetPath;
                    if (!sheetPaths.TryGetValue(plan.SheetName, out sheetPath)) continue;
                    ZipArchiveEntry entry = archive.GetEntry(sheetPath);
                    if (entry == null) continue;
                    XDocument document = LoadDocument(entry);
                    XElement sheetData = document.Root.Element(MainNs + "sheetData");
                    if (sheetData == null) continue;
                    var rowsByNumber = sheetData.Elements(MainNs + "row")
                        .Where(item => (int?)item.Attribute("r") != null)
                        .ToDictionary(item => (int)item.Attribute("r"));
                    var cellsByReference = sheetData.Descendants(MainNs + "c")
                        .Where(item => !String.IsNullOrEmpty((string)item.Attribute("r")))
                        .ToDictionary(item => (string)item.Attribute("r"), item => item, StringComparer.OrdinalIgnoreCase);

                    XElement linkHeader = FindCell(cellsByReference, plan.HeaderRow, plan.LinkColumn);
                    string headerStyle = linkHeader == null ? null : (string)linkHeader.Attribute("s");
                    for (int i = 0; i < ResultHeaders.Length; i++)
                    {
                        XElement cell = GetOrCreateCell(sheetData, rowsByNumber, cellsByReference, plan.HeaderRow, plan.ResultStartColumn + i);
                        if (!String.IsNullOrEmpty(headerStyle)) cell.SetAttributeValue("s", headerStyle);
                        SetInlineText(cell, ResultHeaders[i]);
                    }

                    foreach (ExcelLinkSource source in plan.Sources)
                    {
                        CheckResult result = null;
                        if (!source.ManualOnly)
                        {
                            resultsBySource.TryGetValue(CheckIdentity.Create(plan.SheetName, source.Row, source.Url), out result);
                            if (result == null) resultsByUrl.TryGetValue(source.Url ?? "", out result);
                            if (result == null) continue;
                        }
                        XElement sourceCell = FindCell(cellsByReference, source.Row, plan.LinkColumn);
                        string sourceStyle = sourceCell == null ? null : (string)sourceCell.Attribute("s");
                        string[] values = source.ManualOnly
                            ? new[] { "未完成", "", "" }
                            : new[] { ToExcelVerdict(result.Verdict), result.StatusCode ?? "",
                                result.Title ?? "" };
                        for (int i = 0; i < values.Length; i++)
                        {
                            XElement cell = GetOrCreateCell(sheetData, rowsByNumber, cellsByReference, source.Row, plan.ResultStartColumn + i);
                            if (!String.IsNullOrEmpty(sourceStyle) && cell.Attribute("s") == null) cell.SetAttributeValue("s", sourceStyle);
                            SetInlineText(cell, values[i] ?? "");
                        }
                    }

                    EnsureColumnWidths(document, plan.ResultStartColumn);
                    UpdateDimension(document, sheetData);
                    replacements[sheetPath] = document;
                }

                using (var output = File.Create(temporary))
                using (var outputArchive = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    foreach (ZipArchiveEntry sourceEntry in archive.Entries)
                    {
                        ZipArchiveEntry targetEntry = outputArchive.CreateEntry(sourceEntry.FullName, CompressionLevel.Optimal);
                        targetEntry.LastWriteTime = sourceEntry.LastWriteTime;
                        using (Stream targetStream = targetEntry.Open())
                        {
                            XDocument replacement;
                            if (replacements.TryGetValue(sourceEntry.FullName, out replacement))
                            {
                                using (var writer = new StreamWriter(targetStream, new UTF8Encoding(false)))
                                    replacement.Save(writer, SaveOptions.DisableFormatting);
                            }
                            else
                            {
                                using (Stream sourceStream = sourceEntry.Open()) sourceStream.CopyTo(targetStream);
                            }
                        }
                    }
                }
            }

            ValidateWorkbook(temporary);
            File.Replace(temporary, path, null);
            return backup;
        }

        private static Dictionary<string, string> ReadHyperlinks(ZipArchive archive, string sheetPath, XDocument document)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string normalized = (sheetPath ?? "").Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            string relationshipsPath = (slash < 0 ? "" : normalized.Substring(0, slash + 1)) + "_rels/" +
                (slash < 0 ? normalized : normalized.Substring(slash + 1)) + ".rels";
            ZipArchiveEntry relationshipsEntry = archive.GetEntry(relationshipsPath);
            if (relationshipsEntry == null) return result;
            XDocument relationships = LoadDocument(relationshipsEntry);
            var targets = relationships.Descendants(PackageRelNs + "Relationship")
                .Where(item => String.Equals((string)item.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(item => (string)item.Attribute("Id") ?? "", item => (string)item.Attribute("Target") ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (XElement hyperlink in document.Descendants(MainNs + "hyperlink"))
            {
                string reference = (string)hyperlink.Attribute("ref") ?? "";
                string id = (string)hyperlink.Attribute(RelNs + "id") ?? "";
                string target;
                if (reference.IndexOf(':') < 0 && targets.TryGetValue(id, out target) && !String.IsNullOrWhiteSpace(target))
                    result[reference] = target;
            }
            return result;
        }

        private static void ValidateWorkbook(string path)
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                if (archive.GetEntry("[Content_Types].xml") == null || archive.GetEntry("xl/workbook.xml") == null)
                    throw new InvalidDataException("写回生成的临时 Excel 结构不完整，原文件未被替换。");
            }
        }

        private sealed class CellValue
        {
            public int Row;
            public int Column;
            public string Text;
            public string Formula;
        }

        private static List<CellValue> ReadCells(XDocument document, List<string> sharedStrings)
        {
            var result = new List<CellValue>();
            foreach (XElement cell in document.Descendants(MainNs + "c"))
            {
                string reference = (string)cell.Attribute("r") ?? "";
                int row; int column;
                if (!ParseCellReference(reference, out row, out column)) continue;
                string type = (string)cell.Attribute("t") ?? "";
                string raw = (string)cell.Element(MainNs + "v") ?? "";
                string text;
                if (type == "s")
                {
                    int index;
                    text = Int32.TryParse(raw, out index) && index >= 0 && index < sharedStrings.Count ? sharedStrings[index] : "";
                }
                else if (type == "inlineStr") text = String.Concat(cell.Descendants(MainNs + "t").Select(item => item.Value));
                else text = raw;
                result.Add(new CellValue { Row = row, Column = column, Text = text ?? "", Formula = (string)cell.Element(MainNs + "f") ?? "" });
            }
            return result;
        }

        private static Dictionary<string, string> ReadSheetPaths(ZipArchive archive)
        {
            XDocument workbook = LoadDocument(archive.GetEntry("xl/workbook.xml"));
            XDocument relationships = LoadDocument(archive.GetEntry("xl/_rels/workbook.xml.rels"));
            var relationshipMap = relationships.Descendants(PackageRelNs + "Relationship")
                .ToDictionary(element => (string)element.Attribute("Id"), element => NormalizeSheetPath((string)element.Attribute("Target")));
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement sheet in workbook.Descendants(MainNs + "sheet"))
            {
                string name = (string)sheet.Attribute("name");
                string id = (string)sheet.Attribute(RelNs + "id");
                string target;
                if (!String.IsNullOrEmpty(name) && relationshipMap.TryGetValue(id, out target) && target.IndexOf("worksheets/", StringComparison.OrdinalIgnoreCase) >= 0)
                    result[name] = target;
            }
            return result;
        }

        private static string NormalizeSheetPath(string target)
        {
            target = (target ?? "").Replace('\\', '/').TrimStart('/');
            if (target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) return target;
            while (target.StartsWith("../", StringComparison.Ordinal)) target = target.Substring(3);
            return "xl/" + target;
        }

        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            ZipArchiveEntry entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return new List<string>();
            XDocument document = LoadDocument(entry);
            return document.Descendants(MainNs + "si").Select(item => String.Concat(item.Descendants(MainNs + "t").Select(text => text.Value))).ToList();
        }

        private static XDocument LoadDocument(ZipArchiveEntry entry)
        {
            if (entry == null) throw new InvalidDataException("Excel 文件结构不完整。");
            using (Stream stream = entry.Open()) return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        private static XElement FindCell(XElement sheetData, int row, int column)
        {
            string reference = ColumnName(column) + row;
            return sheetData.Descendants(MainNs + "c").FirstOrDefault(cell => String.Equals((string)cell.Attribute("r"), reference, StringComparison.OrdinalIgnoreCase));
        }

        private static XElement FindCell(Dictionary<string, XElement> cellsByReference, int row, int column)
        {
            XElement cell;
            return cellsByReference.TryGetValue(ColumnName(column) + row, out cell) ? cell : null;
        }

        private static XElement GetOrCreateCell(XElement sheetData, int rowNumber, int column)
        {
            XElement row = sheetData.Elements(MainNs + "row").FirstOrDefault(item => (int?)item.Attribute("r") == rowNumber);
            if (row == null)
            {
                row = new XElement(MainNs + "row", new XAttribute("r", rowNumber));
                XElement nextRow = sheetData.Elements(MainNs + "row").FirstOrDefault(item => ((int?)item.Attribute("r") ?? Int32.MaxValue) > rowNumber);
                if (nextRow == null) sheetData.Add(row); else nextRow.AddBeforeSelf(row);
            }
            string reference = ColumnName(column) + rowNumber;
            XElement existing = row.Elements(MainNs + "c").FirstOrDefault(cell => String.Equals((string)cell.Attribute("r"), reference, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
            var newCell = new XElement(MainNs + "c", new XAttribute("r", reference));
            XElement nextCell = row.Elements(MainNs + "c").FirstOrDefault(item => CellColumn((string)item.Attribute("r")) > column);
            if (nextCell == null) row.Add(newCell); else nextCell.AddBeforeSelf(newCell);
            return newCell;
        }

        private static XElement GetOrCreateCell(XElement sheetData, Dictionary<int, XElement> rowsByNumber,
            Dictionary<string, XElement> cellsByReference, int rowNumber, int column)
        {
            XElement row;
            if (!rowsByNumber.TryGetValue(rowNumber, out row))
            {
                row = new XElement(MainNs + "row", new XAttribute("r", rowNumber));
                XElement nextRow = rowsByNumber.Where(item => item.Key > rowNumber).OrderBy(item => item.Key).Select(item => item.Value).FirstOrDefault();
                if (nextRow == null) sheetData.Add(row); else nextRow.AddBeforeSelf(row);
                rowsByNumber[rowNumber] = row;
            }
            string reference = ColumnName(column) + rowNumber;
            XElement existing;
            if (cellsByReference.TryGetValue(reference, out existing)) return existing;
            var newCell = new XElement(MainNs + "c", new XAttribute("r", reference));
            XElement nextCell = row.Elements(MainNs + "c").FirstOrDefault(item => CellColumn((string)item.Attribute("r")) > column);
            if (nextCell == null) row.Add(newCell); else nextCell.AddBeforeSelf(newCell);
            cellsByReference[reference] = newCell;
            return newCell;
        }

        private static void SetInlineText(XElement cell, string value)
        {
            cell.SetAttributeValue("t", "inlineStr");
            cell.Elements(MainNs + "v").Remove();
            cell.Elements(MainNs + "f").Remove();
            cell.Elements(MainNs + "is").Remove();
            var text = new XElement(MainNs + "t", value ?? "");
            if (!String.IsNullOrEmpty(value) && (Char.IsWhiteSpace(value[0]) || Char.IsWhiteSpace(value[value.Length - 1])))
                text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            cell.Add(new XElement(MainNs + "is", text));
        }

        private static void EnsureColumnWidths(XDocument document, int startColumn)
        {
            XElement worksheet = document.Root;
            XElement sheetData = worksheet.Element(MainNs + "sheetData");
            XElement columns = worksheet.Element(MainNs + "cols");
            if (columns == null)
            {
                columns = new XElement(MainNs + "cols");
                if (sheetData == null) worksheet.Add(columns); else sheetData.AddBeforeSelf(columns);
            }
            double[] widths = new[] { 16d, 12d, 42d };
            for (int i = 0; i < widths.Length; i++)
            {
                int column = startColumn + i;
                XElement existing = columns.Elements(MainNs + "col").FirstOrDefault(item => ((int?)item.Attribute("min") ?? 0) == column && ((int?)item.Attribute("max") ?? 0) == column);
                if (existing == null)
                {
                    existing = new XElement(MainNs + "col", new XAttribute("min", column), new XAttribute("max", column));
                    columns.Add(existing);
                }
                existing.SetAttributeValue("width", widths[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                existing.SetAttributeValue("customWidth", "1");
            }
        }

        private static void UpdateDimension(XDocument document, XElement sheetData)
        {
            var cells = sheetData.Descendants(MainNs + "c").Select(cell => (string)cell.Attribute("r")).Where(value => !String.IsNullOrEmpty(value)).ToList();
            if (cells.Count == 0) return;
            int minRow = Int32.MaxValue, minCol = Int32.MaxValue, maxRow = 0, maxCol = 0;
            foreach (string reference in cells)
            {
                int row; int col;
                if (!ParseCellReference(reference, out row, out col)) continue;
                minRow = Math.Min(minRow, row); minCol = Math.Min(minCol, col); maxRow = Math.Max(maxRow, row); maxCol = Math.Max(maxCol, col);
            }
            XElement dimension = document.Root.Element(MainNs + "dimension");
            if (dimension == null)
            {
                dimension = new XElement(MainNs + "dimension");
                document.Root.AddFirst(dimension);
            }
            dimension.SetAttributeValue("ref", ColumnName(minCol) + minRow + ":" + ColumnName(maxCol) + maxRow);
        }

        private static bool ParseCellReference(string reference, out int row, out int column)
        {
            row = 0; column = 0;
            Match match = Regex.Match(reference ?? "", "^([A-Za-z]+)([0-9]+)$");
            if (!match.Success || !Int32.TryParse(match.Groups[2].Value, out row)) return false;
            foreach (char character in match.Groups[1].Value.ToUpperInvariant()) column = column * 26 + (character - 'A' + 1);
            return column > 0 && row > 0;
        }

        private static int CellColumn(string reference)
        {
            int row, column;
            return ParseCellReference(reference, out row, out column) ? column : Int32.MaxValue;
        }

        private static string ColumnName(int column)
        {
            var builder = new StringBuilder();
            while (column > 0) { column--; builder.Insert(0, (char)('A' + column % 26)); column /= 26; }
            return builder.ToString();
        }

        private static bool IsLinkHeader(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            return lower.Contains("链接") || lower.Contains("网址") || lower == "url" || lower.Contains("url地址") || lower == "link";
        }

        private static bool IsTitleHeader(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            return lower == "标题" || lower.Contains("内容标题") || lower.Contains("文章标题") || lower.Contains("作品标题") || lower == "title";
        }

        private static bool IsExcerptHeader(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            return lower == "摘要" || lower.Contains("内容摘要") || lower.Contains("正文摘要") || lower == "excerpt" || lower == "summary";
        }

        private static bool IsAuthorHeader(string value)
        {
            string lower = (value ?? "").Replace(" ", "").Trim().ToLowerInvariant();
            return lower == "账号昵称" || lower == "作者" || lower == "发文作者" ||
                lower == "发布账号" || lower == "发布人" || lower == "发布者" ||
                lower == "账号名称" || lower == "昵称" || lower == "账号" || lower == "author";
        }

        private static bool IsContentTypeHeader(string value)
        {
            string lower = (value ?? "").Replace(" ", "").Trim().ToLowerInvariant();
            return lower == "内容类型" || lower == "信息类型" || lower == "媒体类型" ||
                lower == "类型" || lower == "contenttype" || lower == "type";
        }

        private static bool IsPlatformHeader(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            return lower == "平台" || lower.Contains("发布平台") || lower.Contains("来源平台") || lower == "platform";
        }

        private static bool IsWechatChannelPlatform(string value)
        {
            string text = (value ?? "").Replace(" ", "").Trim();
            return text.IndexOf("视频号", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("微信视频", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExtractFirstUrl(string text)
        {
            Match match = UrlPattern.Match(text ?? "");
            return match.Success ? match.Value.Trim().TrimEnd('.', ',', ';', ')', ']', '}', '。', '，', '；') : "";
        }

        internal static string ToExcelVerdict(string verdict)
        {
            if (verdict == "已失效") return "失效";
            if (verdict == "仍可访问") return "有效";
            return "未完成";
        }

        private static void EnsureSupported(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".xlsx" && extension != ".xlsm") throw new NotSupportedException("请先将旧版 .xls 文件另存为 .xlsx。");
        }
    }

    internal sealed partial class Checker
    {
        private static readonly string[] RemovedSignals = new[]
        {
            "该内容已删除", "内容已被删除", "内容已删除", "原文已删除", "作者已删除", "视频已删除", "该文章已被删除", "该文章已被作者删除", "抱歉，该文章已被删除",
            "该内容已经删除", "内容已经删除", "该文章已删除", "该文章已经删除", "文章已经删除",
            "该视频已经删除", "视频已经删除", "该帖子已删除", "帖子已删除", "该笔记已被删除", "笔记已删除",
            "该内容已下架", "内容已下架", "视频已下架", "商品已下架", "该内容不存在", "内容不存在",
            "文章不存在", "页面不存在", "页面已不存在", "页面被删除", "链接已失效", "该链接已失效",
            "此内容因违规无法查看", "因违规无法查看", "根据相关法律法规和政策", "已被屏蔽",
            "微博不存在或暂无查看权限", "抱歉，此微博已被删除", "当前内容不可访问",
            "原帖已被作者删除", "该帖已被作者删除", "帖子已被作者删除", "该内容已被作者删除",
            "您访问的页面找不到了", "抱歉，页面找不到了", "该作品不存在", "作品不存在",
            "视频不存在", "视频已下线", "该视频已下线", "内容不见了", "页面不见了", "那条视频不见了",
            "该文章已不存在", "文章没有找到哦", "出错了！文章没有找到哦", "出错了文章没有找到哦",
            "当前内容不适合展示，无法查看", "抱歉，你访问的内容不存在", "你访问的内容不存在",
            "没有知识存在的荒原",
            "this page is no longer available", "the page you requested cannot be found", "this content is no longer available",
            "content has been removed", "post has been removed", "video has been removed", "page not found"
        };

        private static readonly string[] RestrictedSignals = new[]
        {
            "请完成安全验证", "安全验证", "访问过于频繁", "操作频繁", "稍后再试", "请登录后查看",
            "登录后查看", "请先登录", "扫码登录", "扫码查看", "扫描二维码", "滑动验证", "验证码", "访问受限", "请使用手机客户端打开",
            "打开小红书App", "打开小红书 APP", "去App查看", "去 APP 查看", "请在小红书App内打开",
            "access denied", "verify you are human", "unusual traffic", "captcha", "sign in to continue", "log in to continue"
        };

        private readonly HttpClient _client;
        private readonly HttpClient _directClient;
        private readonly HttpClient _zhihuClient;
        private readonly HttpClient _remoteEvidenceClient;
        private readonly HttpClient _globalpingClient;
        private readonly int _bodyBytes;
        private static readonly SemaphoreSlim ZhihuProbeGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim BaiduPublicProbeGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim KuaishouProbeGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim BilibiliProbeGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim RenderedSocialProbeGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim PublicCloudProbeGate = new SemaphoreSlim(1, 1);
        private static readonly object PublicCloudProbeTimingSync = new object();
        private static DateTime _lastPublicCloudProbeStartedUtc = DateTime.MinValue;
        private static readonly object ZhihuProbeTimingSync = new object();
        private static DateTime _lastZhihuProbeStartedUtc = DateTime.MinValue;
        private static readonly ConcurrentDictionary<string, Task<PlatformProbeOutcome>> DouyinProbeCache =
            new ConcurrentDictionary<string, Task<PlatformProbeOutcome>>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim WeiboVisitorGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim WeiboProbeGate = new SemaphoreSlim(1, 1);
        private static string _weiboVisitorCookie = "";
        private static DateTime _weiboVisitorCookieCreatedUtc = DateTime.MinValue;
        private static readonly SemaphoreSlim BrowserSemaphore = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentDictionary<string, RequestPacingState> RequestPacing =
            new ConcurrentDictionary<string, RequestPacingState>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> InfrastructureByHost =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly string BrowserPath = FindBrowserPath();

        private sealed class RequestPacingState
        {
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            public DateTime NextAllowedUtc = DateTime.MinValue;
        }

        internal static string RequestPacingKey(Uri uri)
        {
            string host = uri == null ? "" : (uri.Host ?? "").Trim().Trim('.').ToLowerInvariant();
            foreach (string platform in new[]
            {
                "zhihu.com", "weibo.com", "weibo.cn", "douyin.com", "iesdouyin.com",
                "toutiao.com", "xiaohongshu.com", "xhslink.com", "kuaishou.com",
                "gifshow.com", "bilibili.com", "baidu.com", "dongchedi.com", "xueqiu.com"
            })
                if (host == platform || host.EndsWith("." + platform, StringComparison.Ordinal)) return platform;
            string infrastructure;
            if (host.Length > 0 && InfrastructureByHost.TryGetValue(host, out infrastructure) &&
                !String.IsNullOrWhiteSpace(infrastructure)) return infrastructure;
            return host;
        }

        internal static async Task<Dictionary<string, int>> RegisterInfrastructureAsync(
            IEnumerable<CheckJob> jobs, CancellationToken token)
        {
            List<string> hosts = (jobs ?? Enumerable.Empty<CheckJob>())
                .Where(item => item != null)
                .Select(item =>
                {
                    Uri uri;
                    return Uri.TryCreate(item.Url, UriKind.Absolute, out uri)
                        ? (uri.Host ?? "").Trim().Trim('.').ToLowerInvariant() : "";
                })
                .Where(host => host.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var gate = new SemaphoreSlim(12, 12);
            var tasks = hosts.Select(async host =>
            {
                await gate.WaitAsync(token);
                try
                {
                    Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
                    Task finished = await Task.WhenAny(lookup, Task.Delay(3500, token));
                    token.ThrowIfCancellationRequested();
                    if (finished != lookup) return;
                    IPAddress address = (await lookup)
                        .Where(item => item != null)
                        .OrderBy(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
                        .FirstOrDefault();
                    if (address != null)
                        InfrastructureByHost[host] = "IP " + address;
                }
                catch { }
                finally { gate.Release(); }
            }).ToArray();
            await Task.WhenAll(tasks);

            foreach (CheckJob job in jobs ?? Enumerable.Empty<CheckJob>())
            {
                Uri uri;
                string infrastructure;
                if (job != null && Uri.TryCreate(job.Url, UriKind.Absolute, out uri) &&
                    InfrastructureByHost.TryGetValue(uri.Host, out infrastructure))
                    job.InfrastructureKey = infrastructure;
            }
            return (jobs ?? Enumerable.Empty<CheckJob>())
                .Where(item => item != null)
                .GroupBy(item => String.IsNullOrWhiteSpace(item.InfrastructureKey)
                    ? BatchPreflightPlanner.PlatformKey(item) : item.InfrastructureKey,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        }

        internal static int RequestPacingMilliseconds(Uri uri)
        {
            string key = RequestPacingKey(uri);
            IPAddress address;
            if (IPAddress.TryParse(uri == null ? "" : uri.Host, out address) &&
                IPAddress.IsLoopback(address)) return 0;
            return new[]
            {
                "zhihu.com", "weibo.com", "weibo.cn", "douyin.com", "iesdouyin.com",
                "toutiao.com", "xiaohongshu.com", "xhslink.com", "kuaishou.com",
                "gifshow.com", "bilibili.com", "baidu.com", "dongchedi.com", "xueqiu.com"
            }.Contains(key, StringComparer.OrdinalIgnoreCase) ? 1600 : 350;
        }

        internal static async Task WaitForRequestSlotAsync(Uri uri, CancellationToken token)
        {
            string key = RequestPacingKey(uri);
            if (String.IsNullOrWhiteSpace(key)) return;
            RequestPacingState state = RequestPacing.GetOrAdd(key, ignored => new RequestPacingState());
            await state.Gate.WaitAsync(token);
            try
            {
                int delay = Math.Max(0, (int)(state.NextAllowedUtc - DateTime.UtcNow).TotalMilliseconds);
                if (delay > 0) await Task.Delay(delay, token);
                state.NextAllowedUtc = DateTime.UtcNow.AddMilliseconds(RequestPacingMilliseconds(uri));
            }
            finally { state.Gate.Release(); }
        }

        private sealed class BrowserSnapshot
        {
            public string Html;
            public string Error;
            public bool TimedOut;
        }

        private sealed class PlatformProbeOutcome
        {
            public bool Resolved;
            public string Verdict;
            public string Evidence;
            public string FinalUrl;
            public List<VerificationEvidence> Evidences;
        }

        private static PlatformProbeOutcome ProbeOutcome(EvidenceKind kind, EvidenceStrength strength,
            string source, string platform, string targetId, string message, string finalUrl, bool isCurrentResponse)
        {
            var evidence = new VerificationEvidence
            {
                Kind = kind,
                Strength = strength,
                Source = source ?? "",
                Platform = platform ?? "",
                TargetId = targetId ?? "",
                Message = message ?? "",
                FinalUrl = finalUrl ?? "",
                IsCurrentResponse = isCurrentResponse
            };
            DeepDecision decision = EvidenceAdjudicator.Decide(new[] { evidence });
            return new PlatformProbeOutcome
            {
                Resolved = decision.Resolved,
                Verdict = decision.Verdict,
                Evidence = decision.Evidence,
                FinalUrl = finalUrl,
                Evidences = new List<VerificationEvidence> { evidence }
            };
        }

        private static DeepDecision DecideEvidence(EvidenceKind kind, EvidenceStrength strength,
            string source, string platform, string targetId, string message, string finalUrl, bool isCurrentResponse)
        {
            return EvidenceAdjudicator.Decide(new[]
            {
                new VerificationEvidence
                {
                    Kind = kind,
                    Strength = strength,
                    Source = source ?? "",
                    Platform = platform ?? "",
                    TargetId = targetId ?? "",
                    Message = message ?? "",
                    FinalUrl = finalUrl ?? "",
                    IsCurrentResponse = isCurrentResponse
                }
            });
        }

        private sealed class ProbeResponse
        {
            public int Status;
            public string Body;
            public string FinalUrl;
        }

        private sealed class SendAttempt
        {
            public HttpResponseMessage Response;
            public Exception Error;
        }

        public Checker() : this(900000) { }

        public Checker(int bodyBytes)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | (SecurityProtocolType)768 | SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 64;
            _client = CreateClient(true);
            _directClient = CreateClient(false);
            _zhihuClient = CreateClient(true);
            _remoteEvidenceClient = CreateClient(true);
            _remoteEvidenceClient.Timeout = TimeSpan.FromSeconds(25);
            // Globalping's anonymous hourly allowance is counted by the API caller's
            // public IP. Keep the control API off the target-page/system proxy route
            // so a saturated shared proxy does not block otherwise available evidence.
            _globalpingClient = CreateClient(false);
            _globalpingClient.Timeout = TimeSpan.FromSeconds(30);
            _bodyBytes = Math.Max(180000, bodyBytes);
        }

        private static HttpClient CreateClient(bool useSystemProxy)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 8,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                UseProxy = useSystemProxy
            };
            if (useSystemProxy)
            {
                try
                {
                    IWebProxy proxy = WebRequest.GetSystemWebProxy();
                    if (proxy != null)
                    {
                        proxy.Credentials = CredentialCache.DefaultCredentials;
                        handler.Proxy = proxy;
                    }
                    handler.UseDefaultCredentials = true;
                }
                catch { }
            }
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.6");
            return client;
        }

        public Task<CheckResult> CheckAsync(string input, int number, CancellationToken token)
        {
            return CheckAsync(input, number, "", token);
        }

        public async Task<CheckResult> CheckAsync(string input, int number, string expectedTitle, CancellationToken token)
        {
            return await CheckAsync(input, number, expectedTitle, "", true, token);
        }

        public async Task<CheckResult> CheckAsync(string input, int number, string expectedTitle, bool allowBrowserFallback, CancellationToken token)
        {
            return await CheckAsync(input, number, expectedTitle, "", allowBrowserFallback, token);
        }

        public async Task<CheckResult> CheckAsync(string input, int number, string expectedTitle, string expectedExcerpt, bool allowBrowserFallback, CancellationToken token)
        {
            return await CheckAsync(input, number, expectedTitle, expectedExcerpt, "", "", "", allowBrowserFallback, token);
        }

        public async Task<CheckResult> CheckAsync(string input, int number, string expectedTitle, string expectedExcerpt, string expectedAuthor, bool allowBrowserFallback, CancellationToken token)
        {
            return await CheckAsync(input, number, expectedTitle, expectedExcerpt, expectedAuthor, "", "", allowBrowserFallback, token);
        }

        public async Task<CheckResult> CheckAsync(string input, int number, string expectedTitle, string expectedExcerpt, string expectedAuthor, string platform, string contentType, bool allowBrowserFallback, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            var result = new CheckResult
            {
                Number = number,
                OriginalUrl = input,
                ExpectedTitle = expectedTitle ?? "",
                ExpectedExcerpt = expectedExcerpt ?? "",
                ExpectedAuthor = expectedAuthor ?? "",
                Platform = platform ?? "",
                ContentType = String.IsNullOrWhiteSpace(contentType) ? InferContentType(platform, input, expectedTitle) : contentType,
                FinalUrl = "",
                Title = "",
                CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            Uri uri;
            if (!Uri.TryCreate(input, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                result.Verdict = "输入有误";
                result.StatusCode = "-";
                result.Evidence = "不是有效的 HTTP/HTTPS 链接";
                result.Duration = "0.0s";
                return result;
            }

            if (IsWechatChannel(uri))
            {
                // Temporary video-download URLs commonly return HTTP 400 after
                // the signed URL expires. That transport response is not proof
                // that the video was removed; let the normal evidence path keep
                // it in the retryable unfinished queue with a precise reason.
                result.Verdict = "暂时异常";
                result.StatusCode = "无公开页";
                result.Evidence = "微信视频号临时下载地址没有稳定的公开详情页，不能仅凭下载地址判断失效；保留稍后重试";
                result.SkipDeepReview = true;
                result.Duration = "0.0s";
                return result;
            }

            if (IsBaiduDtArticle(uri))
            {
                PlatformProbeOutcome baiduPreflight = await ProbeBaiduDtArticleAsync(uri, expectedTitle, expectedExcerpt, token);
                if (baiduPreflight != null && baiduPreflight.Resolved)
                {
                    result.Verdict = baiduPreflight.Verdict;
                    result.Evidence = baiduPreflight.Evidence;
                    result.EvidenceTrail = baiduPreflight.Evidences;
                    result.StatusCode = "200";
                    result.FinalUrl = baiduPreflight.FinalUrl;
                    watch.Stop();
                    result.Duration = watch.Elapsed.TotalSeconds.ToString("0.0") + "s";
                    return result;
                }
            }

            try
            {
                using (var response = await SendWithFallbackAsync(uri, token))
                {
                    int code = (int)response.StatusCode;
                    result.StatusCode = code.ToString();
                    result.FinalUrl = response.RequestMessage != null && response.RequestMessage.RequestUri != null
                        ? response.RequestMessage.RequestUri.AbsoluteUri : input;

                    string mediaType = response.Content.Headers.ContentType == null ? "" : (response.Content.Headers.ContentType.MediaType ?? "");
                    string body = await ReadLimitedBodyAsync(response.Content, _bodyBytes, token);
                    string title = ExtractTitle(body);
                    result.Title = title;
                    string visibleForAi = ExtractVisibleText(body);
                    result.AnalysisContext = AiReviewPolicy.BuildObservedContext(title,
                        ExtractProbableMainContentText(body), visibleForAi);

                    Uri platformProbeUri = SelectPlatformProbeUri(uri, result.FinalUrl);
                    PlatformProbeOutcome platformProbe = await ProbePlatformContentAsync(platformProbeUri, expectedTitle, expectedExcerpt, expectedAuthor, token);
                    if ((platformProbe == null || !platformProbe.Resolved) &&
                        platformProbeUri != null && uri != null &&
                        !String.Equals(platformProbeUri.AbsoluteUri, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                    {
                        PlatformProbeOutcome originalProbe = await ProbePlatformContentAsync(uri, expectedTitle, expectedExcerpt, expectedAuthor, token);
                        if (originalProbe != null && (platformProbe == null || originalProbe.Resolved))
                            platformProbe = originalProbe;
                    }
                    if (platformProbe != null && platformProbe.Evidences != null)
                        result.EvidenceTrail = platformProbe.Evidences;
                    if ((platformProbe == null || !platformProbe.Resolved) &&
                        IsAutohomeArticleErrorRedirect(uri, result.FinalUrl, body))
                    {
                        platformProbe = ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-page", "汽车之家", "",
                            "汽车之家最终地址带有 pc-error-no-hidden 目标错误标记，且已跳离原文章路径",
                            result.FinalUrl, true);
                    }
                    // A supplier URL may legitimately redirect to the publisher's
                    // own article page on another domain (for example 10jqka ->
                    // TechWeb). Resolve that target before generic login/footer
                    // signals such as "扫描二维码" can downgrade a real article.
                    if ((platformProbe == null || !platformProbe.Resolved) && code >= 200 && code < 300 &&
                        HasCrossDomainArticleIdentity(result, title, visibleForAi, body, result.FinalUrl))
                    {
                        platformProbe = ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                            "cross-domain-article", result.Platform, "",
                            "原链接跳转到其他站点，但最终页面标题、正文结构和目标内容仍然匹配",
                            result.FinalUrl, true);
                    }
                    // Some public pages return a normal HTTP 200 shell but hide
                    // the target body from ordinary clients. For platforms where
                    // the independent reader is known to preserve the target
                    // article/post, take one bounded cloud reading before the
                    // generic 200-page heuristics classify it as review.
                    if ((platformProbe == null || !platformProbe.Resolved) && code >= 200 && code < 300 &&
                        ShouldTryPublicCloudForUnresolved(uri, result))
                    {
                        RemoteEvidenceResponse publicCloud = await TryPublicCloudEvidenceAsync(uri, token);
                        if (ApplyRemoteEvidence(result, publicCloud, "public-cloud-reader", result.EvidenceTrail ?? new List<VerificationEvidence>()))
                        {
                            platformProbe = new PlatformProbeOutcome
                            {
                                Resolved = true,
                                Verdict = result.Verdict,
                                Evidence = result.Evidence,
                                FinalUrl = result.FinalUrl,
                                Evidences = result.EvidenceTrail
                            };
                        }
                    }
                    Uri shortEvidenceUri;
                    string shortEvidenceSource = result.FinalUrl;
                    Match encodedTarget = Regex.Match(body ?? "", @"https?%3A%2F%2Fweibo\.com%2Ftv%2Fshow%2F[0-9]+%3A[0-9]+",
                        RegexOptions.IgnoreCase);
                    if (encodedTarget.Success) shortEvidenceSource = WebUtility.UrlDecode(encodedTarget.Value);
                    if ((platformProbe == null || !platformProbe.Resolved) &&
                        (TryExtractWeiboVideoEvidenceUri(shortEvidenceSource, out shortEvidenceUri) ||
                         TryBuildKnownShortLinkEvidenceUri(uri, shortEvidenceSource, out shortEvidenceUri)))
                    {
                        RemoteEvidenceResponse shortCloud = await TryPublicCloudEvidenceAsync(shortEvidenceUri, token);
                        if (ApplyRemoteEvidence(result, shortCloud, "public-cloud-reader",
                            result.EvidenceTrail ?? new List<VerificationEvidence>(), shortEvidenceUri))
                        {
                            platformProbe = new PlatformProbeOutcome
                            {
                                Resolved = true,
                                Verdict = result.Verdict,
                                Evidence = result.Evidence,
                                FinalUrl = result.FinalUrl,
                                Evidences = result.EvidenceTrail
                            };
                        }
                        else
                        {
                            result.AcquisitionAttempts = "微博视频目标页公开云取证未确认：" +
                                (shortCloud == null ? "无响应" :
                                 !String.IsNullOrWhiteSpace(shortCloud.Error) ? shortCloud.Error :
                                 "已返回页面但标题/正文/作者未通过目标身份匹配");
                        }
                    }

                    if (platformProbe != null && platformProbe.Resolved)
                    {
                        result.Verdict = platformProbe.Verdict;
                        result.Evidence = platformProbe.Evidence;
                        if (!String.IsNullOrWhiteSpace(platformProbe.FinalUrl)) result.FinalUrl = platformProbe.FinalUrl;
                    }
                    else if (code == 404 || code == 410)
                    {
                        Uri finalUri;
                        Uri.TryCreate(result.FinalUrl, UriKind.Absolute, out finalUri);
                        bool targetResponse = IsAuthoritativeTargetHttpRemoval(uri, finalUri);
                        result.Verdict = targetResponse ? "已失效" : "人工复核";
                        result.Evidence = targetResponse
                            ? "目标地址所属站点返回 HTTP " + code
                            : "请求跳转到其他站点或登录页后返回 HTTP " + code + "，不能证明原目标内容已删除";
                    }
                    else if (code == 429 || code == 444)
                    {
                        result.Verdict = "暂时异常";
                        result.Evidence = "当前网络出口受到站点限制（HTTP " + code + "），已保留稍后重试";
                    }
                    else if (code == 401 || code == 403 || code == 407)
                    {
                        // A restricted web response is not deletion evidence. For
                        // platforms with a stable public reader, make one bounded
                        // independent read before leaving the row unfinished. This
                        // is especially important for Zhihu: the answer API often
                        // returns 403 even when the answer is still public.
                        if (platformProbe == null && ShouldTryPublicCloudForUnresolved(uri, result) &&
                            (code == 403 || code == 407))
                        {
                            RemoteEvidenceResponse restrictedCloud = await TryPublicCloudEvidenceAsync(uri, token);
                            if (ApplyRemoteEvidence(result, restrictedCloud, "public-cloud-reader",
                                result.EvidenceTrail ?? new List<VerificationEvidence>(), uri))
                            {
                                watch.Stop();
                                result.Duration = watch.Elapsed.TotalSeconds.ToString("0.0") + "s";
                                return result;
                            }
                        }
                        bool browserResolved = false;
                        if (allowBrowserFallback && !String.IsNullOrWhiteSpace(expectedTitle) &&
                            !String.Equals(Environment.GetEnvironmentVariable("LINK_CHECKER_QUICK_PASS"), "1",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            BrowserSnapshot restrictedSnapshot = await RenderWithBrowserAsync(result.FinalUrl, token);
                            ApplyBrowserResult(result, expectedTitle, restrictedSnapshot, code);
                            browserResolved = result.Verdict == "已失效" || result.Verdict == "仍可访问";
                        }
                        if (!browserResolved)
                        {
                            result.Verdict = "人工复核";
                            result.Evidence = "访问被限制（HTTP " + code + "），不能据此判定已处置";
                        }
                    }
                    else if (code >= 200 && code < 300 && platformProbe == null &&
                        ShouldTryPublicCloudForUnresolved(uri, result) &&
                        (LooksLikeLogin(result.FinalUrl) ||
                         !String.IsNullOrEmpty(FindSignal((title + " " + visibleForAi).ToLowerInvariant(), RestrictedSignals))))
                    {
                        RemoteEvidenceResponse restrictedShellCloud = await TryPublicCloudEvidenceAsync(uri, token);
                        if (ApplyRemoteEvidence(result, restrictedShellCloud, "public-cloud-reader",
                            result.EvidenceTrail ?? new List<VerificationEvidence>(), uri))
                        {
                            watch.Stop();
                            result.Duration = watch.Elapsed.TotalSeconds.ToString("0.0") + "s";
                            return result;
                        }
                        result.Verdict = NetworkRestrictionCircuitBreaker.IsSecurityOrRateLimitText(
                            FindSignal((title + " " + visibleForAi).ToLowerInvariant(), RestrictedSignals))
                            ? "暂时异常" : "人工复核";
                        result.Evidence = "普通网页进入登录/验证/风控页，独立公开读取也未取得足够目标证据";
                    }
                    else if (code >= 500)
                    {
                        bool browserResolved = false;
                        if (allowBrowserFallback && !String.IsNullOrWhiteSpace(expectedTitle) &&
                            !String.Equals(Environment.GetEnvironmentVariable("LINK_CHECKER_QUICK_PASS"), "1",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            BrowserSnapshot errorSnapshot = await RenderWithBrowserAsync(result.FinalUrl, token);
                            ApplyBrowserResult(result, expectedTitle, errorSnapshot, code);
                            browserResolved = result.Verdict == "已失效" || result.Verdict == "仍可访问";
                        }
                        if (!browserResolved)
                        {
                            result.Verdict = "暂时异常";
                            result.Evidence = "当前访问线路或目标站点返回 HTTP " + code + "，未取得正常内容页，建议稍后重试";
                        }
                    }
                    else if (code == 451)
                    {
                        result.Verdict = "疑似已处置";
                        result.Evidence = "服务器返回 HTTP 451（因法律原因不可用）";
                    }
                    else if (code == 408)
                    {
                        result.Verdict = "暂时异常";
                        result.Evidence = "请求超时（HTTP 408），建议稍后重试";
                    }
                    else if (code >= 400)
                    {
                        result.Verdict = "人工复核";
                        result.Evidence = "服务器拒绝请求（HTTP " + code + "），不能据此判定已处置";
                    }
                    else
                    {
                        string visible = visibleForAi;
                        string combined = (title + " " + visible).ToLowerInvariant();
                        string signal = FirstNonEmpty(FindSignal(combined, RemovedSignals), PlatformRules.FindRemovedSignal(combined, uri));
                        string restriction = FirstNonEmpty(FindSignal(combined, RestrictedSignals), PlatformRules.FindRestrictedSignal(combined, uri));
                        // 只相信用户实际可见的标题/正文。脚本源码可能残留已删除内容的旧标题。
                        bool expectedMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, title + " " + visible);
                        bool authorMatch = MatchesExpectedAuthor(expectedAuthor, title + " " + visible);
                        bool crossDomainArticle = HasCrossDomainArticleIdentity(result, title, visible, body, result.FinalUrl);
                        bool strongContentIdentity = HasStrongRenderedContentIdentity(result, new RenderedPageData
                        {
                            Title = title,
                            Text = visible,
                            Html = body,
                            MainText = ExtractProbableMainContentText(body),
                            MainHtml = ExtractProbableMainContentHtml(body),
                            Url = result.FinalUrl
                        }, expectedMatch);
                        bool reliableTitleIdentity = String.IsNullOrEmpty(signal) &&
                            HasReliablePageTitleIdentity(expectedTitle, title, visible, uri, result.FinalUrl);

                        if (strongContentIdentity || reliableTitleIdentity || crossDomainArticle)
                        {
                            result.Verdict = "仍可访问";
                            result.Evidence = crossDomainArticle
                                ? "原链接已跳转到其他站点，但最终页面标题、正文结构和目标内容仍可核验（HTTP " + code + "）"
                                : authorMatch && expectedMatch
                                ? "页面仍能找到目标内容片段及发文作者“" + expectedAuthor.Trim() + "”（HTTP " + code + "）"
                                : !String.IsNullOrEmpty(signal)
                                ? "页面仍能找到原文标题、摘要或正文片段；“" + signal + "”可能来自评论、推荐或弹窗（HTTP " + code + "）"
                                : "页面仍能找到原文标题、摘要或正文片段（HTTP " + code + "）";
                        }
                        else if (!String.IsNullOrEmpty(signal))
                        {
                            bool explicitRemoval = IsExplicitTargetRemovalPage(signal, result.FinalUrl, title, visible, body,
                                ExtractProbableMainContentText(body), ExtractProbableMainContentHtml(body));
                            result.Verdict = explicitRemoval ? "已失效" : "疑似已处置";
                            result.Evidence = explicitRemoval
                                ? "页面主体明确提示目标内容“" + signal + "”"
                                : "页面出现“" + signal + "”，但尚未确认该提示属于目标正文，已保留待复核";
                        }
                        else if (LooksLikeLogin(result.FinalUrl) || !String.IsNullOrEmpty(restriction))
                        {
                            result.Verdict = NetworkRestrictionCircuitBreaker.IsSecurityOrRateLimitText(restriction)
                                ? "暂时异常" : "人工复核";
                            result.Evidence = !String.IsNullOrEmpty(restriction)
                                ? "遇到登录/验证/风控提示“" + restriction + "”"
                                : "链接跳转到登录或验证页";
                        }
                        else if (LooksLikeErrorPage(result.FinalUrl, title, visible))
                        {
                            result.Verdict = "已失效";
                            result.Evidence = "链接跳转到错误页或页面显示 404";
                        }
                        else if (IsStrongPlatformEmptyState(result.OriginalUrl, result.FinalUrl, expectedTitle, title, visible, body))
                        {
                            result.Verdict = "已失效";
                            result.Evidence = "平台返回目标内容专用错误页，且目标内容编号已消失";
                        }
                        else if (LooksLikePlatformRemovalRedirect(uri, result.FinalUrl))
                        {
                            result.Verdict = "疑似已处置";
                            result.Evidence = "平台将原内容链接跳转到首页或其他内容页，但可能受登录、设备或页面改版影响";
                        }
                        else if (LooksLikeHomepageRedirect(uri, result.FinalUrl))
                        {
                            result.Verdict = "疑似已处置";
                            result.Evidence = "原内容链接跳转到站点首页";
                        }
                        else if (!String.IsNullOrEmpty(mediaType) && mediaType.IndexOf("html", StringComparison.OrdinalIgnoreCase) < 0 && mediaType.IndexOf("json", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            result.Verdict = "仍可访问";
                            result.Evidence = "资源可正常获取（HTTP " + code + "，" + mediaType + "）";
                        }
                        else if (!String.IsNullOrWhiteSpace(expectedTitle) && allowBrowserFallback &&
                            !String.Equals(Environment.GetEnvironmentVariable("LINK_CHECKER_QUICK_PASS"), "1",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            BrowserSnapshot snapshot = await RenderWithBrowserAsync(result.FinalUrl, token);
                            ApplyBrowserResult(result, expectedTitle, snapshot, code);
                        }
                        else if (!String.IsNullOrWhiteSpace(expectedTitle))
                        {
                            result.Verdict = "人工复核";
                            result.Evidence = "快速核验未找到导入数据中的原标题/正文片段；可稍后手动进行浏览器深度复核";
                        }
                        else if (visible.Length < 60 && String.IsNullOrEmpty(title))
                        {
                            result.Verdict = "人工复核";
                            result.Evidence = "页面内容过少或依赖 App/JavaScript，无法自动确认";
                        }
                        else if (IsDynamicShellHost(uri.Host) && LooksGenericTitle(title))
                        {
                            result.Verdict = "人工复核";
                            result.Evidence = "平台只返回动态网页外壳，未提供 Excel 标题无法确认原内容";
                        }
                        else
                        {
                            result.Verdict = "人工复核";
                            result.Evidence = "页面可以打开，但没有原标题、内容编号或其他身份依据，不能仅凭 HTTP " + code + " 判定有效";
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                result.Verdict = "暂时异常";
                result.StatusCode = "超时";
                result.Evidence = "18 秒内未响应，建议稍后重试";
            }
            catch (HttpRequestException ex)
            {
                result.Verdict = "暂时异常";
                result.StatusCode = "连接失败";
                result.Evidence = FriendlyError(ex);
            }
            catch (Exception ex)
            {
                result.Verdict = "人工复核";
                result.StatusCode = "异常";
                result.Evidence = FriendlyError(ex);
            }
            finally
            {
                watch.Stop();
                result.Duration = watch.Elapsed.TotalSeconds.ToString("0.0") + "s";
            }
            if (IsXiaohongshu(uri) && result.Verdict != "已失效" && result.Verdict != "仍可访问")
            {
                // Share links redirect to the generic /explore shell when the note is
                // unavailable. The redirect query carries an explicit note-level error;
                // it is safe to use that signal without requiring the mobile app.
                if (IsXiaohongshuUnavailableRedirect(result.FinalUrl))
                {
                    result.Verdict = "已失效";
                    result.Evidence = "小红书分享页明确提示该内容暂时无法查看";
                    result.SkipDeepReview = true;
                    return result;
                }
                result.Verdict = "人工复核";
                result.StatusCode = String.IsNullOrWhiteSpace(result.StatusCode) ? "需手机复核" : result.StatusCode;
                result.Evidence = "小红书未取得可直接确认的网页证据，可能需要手机扫码或在 App 内查看，已自动转人工复核";
                result.SkipDeepReview = false;
            }
            if (IsBaiduDtArticle(uri) && result.Verdict != "已失效" && result.Verdict != "仍可访问")
            {
                PlatformProbeOutcome retry = await ProbeBaiduDtArticleAsync(uri, expectedTitle, expectedExcerpt, token);
                if (retry != null && retry.Resolved)
                {
                    result.Verdict = retry.Verdict;
                    result.Evidence = retry.Evidence;
                    result.EvidenceTrail = retry.Evidences;
                    if (!String.IsNullOrWhiteSpace(retry.FinalUrl)) result.FinalUrl = retry.FinalUrl;
                }
            }
            return result;
        }

        private async Task<PlatformProbeOutcome> ProbePlatformContentAsync(Uri original, string expectedTitle, string expectedExcerpt, string expectedAuthor, CancellationToken token)
        {
            if (original == null) return null;
            string host = original.Host.ToLowerInvariant();
            Match identity;
            // Bilibili column pages have a stable public metadata endpoint. Run it
            // before the generic dynamic-page branch so the JS shell cannot hide a
            // valid article from the fast pass.
            if (host.EndsWith("bilibili.com", StringComparison.Ordinal))
            {
                Match column = Regex.Match(original.AbsolutePath ?? "", @"/read/cv([0-9]{6,})(?:/|$)", RegexOptions.IgnoreCase);
                if (column.Success)
                {
                    string columnId = column.Groups[1].Value;
                    string columnUrl = "https://api.bilibili.com/x/article/viewinfo?id=" + columnId;
                    var columnHeaders = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36" },
                        { "Referer", original.AbsoluteUri },
                        { "Accept", "application/json, text/plain, */*" },
                        { "Origin", "https://www.bilibili.com" }
                    };
                    ProbeResponse columnProbe = await TryReadProbeAsync(columnUrl, columnHeaders, token);
                    // Some corporate proxies return a synthetic HTTP 200/anti-bot
                    // JSON for Bilibili APIs.  A missing target id is a safe reason
                    // to retry the same official endpoint once without that proxy.
                    if (columnProbe != null && (columnProbe.Status == 429 || columnProbe.Status == 403 || columnProbe.Status == 200) &&
                        !IsBilibiliArticleApiSuccess(columnProbe.Body))
                    {
                        ProbeResponse directColumn = await ReadProbeWithClientAsync(_directClient, columnUrl, null, token);
                        if (directColumn != null && directColumn.Status == 200 &&
                            IsBilibiliArticleApiSuccess(directColumn.Body))
                            columnProbe = directColumn;
                    }
                    if (columnProbe != null && columnProbe.Status == 200)
                    {
                        bool columnRemoved;
                        if (TryMatchBilibiliArticleInfo(columnProbe.Body, columnId, expectedTitle,
                            expectedExcerpt, expectedAuthor, out columnRemoved))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-api", "哔哩哔哩专栏", columnId, "B站官方专栏接口返回目标文章编号、标题和作者", columnUrl, true);
                        if (columnRemoved)
                            return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                                "official-api", "哔哩哔哩专栏", columnId, "B站官方专栏接口确认目标文章不存在", columnUrl, true);
                    }
                }
            }
            // Dongchedi frequently serves a login-required Next.js shell even for
            // live public items.  It is not target-level removal evidence; only a
            // page that explicitly names the missing article can resolve here.
            if (host.EndsWith("dcdapp.com", StringComparison.Ordinal) ||
                host.EndsWith("dongchedi.com", StringComparison.Ordinal))
            {
                ProbeResponse dcdProbe = await TryReadProbeAsync(original.AbsoluteUri, null, token);
                string dcdText = ExtractVisibleText(dcdProbe == null ? "" : dcdProbe.Body);
                if (Regex.IsMatch(dcdText, "文章不存在|内容不存在|该内容已删除|作品不存在|内容已下线", RegexOptions.IgnoreCase))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                        "official-page", "懂车帝", "", "懂车帝页面明确提示目标内容不存在或已删除", original.AbsoluteUri, true);
                // The desktop/mobile page is often a completely empty JS login
                // shell: it contains neither the word "登录" nor the target
                // article text. In quick mode that shell is itself the signal to
                // use the bounded public reader; otherwise live DCD items all
                // fall into review before the independent reader is attempted.
                bool dcdShell = dcdProbe == null ||
                    IsDongchediJavascriptShell(dcdProbe.Body) ||
                    String.IsNullOrWhiteSpace(dcdText) ||
                    LooksLikeLogin(dcdProbe.FinalUrl) ||
                    Regex.IsMatch(dcdText, "登录|验证码|验证", RegexOptions.IgnoreCase);
                if (String.Equals(Environment.GetEnvironmentVariable("LINK_CHECKER_QUICK_PASS"), "1",
                    StringComparison.OrdinalIgnoreCase) && dcdShell)
                {
                    RemoteEvidenceResponse dcdCloud = await TryPublicCloudEvidenceAsync(original, token);
                    string dcdCloudText = WebUtility.HtmlDecode((dcdCloud == null ? "" :
                        (dcdCloud.Title ?? "") + " " + (dcdCloud.Text ?? "")));
                    if (dcdCloud != null && String.IsNullOrWhiteSpace(dcdCloud.Error) && dcdCloud.Status == 200 &&
                        MatchesExpectedContent(expectedTitle, expectedExcerpt, dcdCloudText) &&
                        (String.IsNullOrWhiteSpace(expectedAuthor) || MatchesExpectedAuthor(expectedAuthor, dcdCloudText)))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                            "public-cloud-reader", "懂车帝", "", "懂车帝独立公开读取线路返回匹配标题/正文/作者", dcdCloud.FinalUrl, true);
                    if (dcdCloud != null && String.IsNullOrWhiteSpace(dcdCloud.Error) && dcdCloud.Status == 200 &&
                        Regex.IsMatch(dcdCloudText, "文章不存在|内容不存在|作品不存在|已删除|已下线", RegexOptions.IgnoreCase))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Strong,
                            "public-cloud-reader", "懂车帝", "", "懂车帝独立公开读取线路明确提示目标内容不存在或已删除", dcdCloud.FinalUrl, true);
                }
                return null;
            }
            if (host.EndsWith("jianshu.com", StringComparison.Ordinal))
            {
                Match articleIdentity = Regex.Match(original.AbsolutePath ?? "", @"/p/([a-z0-9]{8,})(?:/|$)", RegexOptions.IgnoreCase);
                if (articleIdentity.Success &&
                    String.Equals(Environment.GetEnvironmentVariable("LINK_CHECKER_QUICK_PASS"), "1",
                        StringComparison.OrdinalIgnoreCase))
                {
                    string articleId = articleIdentity.Groups[1].Value;
                    RemoteEvidenceResponse articleCloud = await TryPublicCloudEvidenceAsync(original, token);
                    string articleCloudText = WebUtility.HtmlDecode(articleCloud == null ? "" :
                        (articleCloud.Title ?? "") + " " + (articleCloud.Text ?? ""));
                    bool articleIdMatch = articleCloudText.IndexOf(articleId, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (articleCloud != null && String.IsNullOrWhiteSpace(articleCloud.Error) &&
                        articleCloud.Status == 200 && articleIdMatch &&
                        MatchesExpectedContent(expectedTitle, expectedExcerpt, articleCloudText) &&
                        (String.IsNullOrWhiteSpace(expectedAuthor) || MatchesExpectedAuthor(expectedAuthor, articleCloudText)))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                            "public-cloud-reader", "简书", articleId,
                            "简书独立公开读取线路返回目标文章编号、匹配标题/正文和作者", articleCloud.FinalUrl, true);
                    if (articleCloud != null && String.IsNullOrWhiteSpace(articleCloud.Error) &&
                        articleCloud.Status == 200 && articleIdMatch && Regex.IsMatch(articleCloudText,
                            "文章不存在|内容不存在|该文章已删除|文章已删除|页面不存在", RegexOptions.IgnoreCase))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Strong,
                            "public-cloud-reader", "简书", articleId,
                            "简书独立公开读取线路明确提示目标文章不存在或已删除", articleCloud.FinalUrl, true);
                }
                return null;
            }
            if (host.EndsWith("yidianzixun.com", StringComparison.Ordinal) ||
                host == "k.sina.com.cn" || host == "k.sina.cn")
            {
                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                    { "Accept-Language", "zh-CN,zh;q=0.9" }
                };
                ProbeResponse pageProbe = await TryReadProbeAsync(original.AbsoluteUri, headers, token);
                if (pageProbe != null && pageProbe.Status == 200)
                {
                    string pageText = ExtractVisibleText(pageProbe.Body);
                    if (Regex.IsMatch(pageText, "文章没有找到哦|出错了[！!]文章没有找到哦", RegexOptions.IgnoreCase))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-page", "一点资讯", "", "一点资讯页面明确提示文章没有找到", pageProbe.FinalUrl, true);
                    if (Regex.IsMatch(pageText, "该文章已不存在", RegexOptions.IgnoreCase))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-page", "新浪看点", "", "新浪页面明确提示该文章已不存在", pageProbe.FinalUrl, true);
                }
            }

            // Baidu's mobile shared article/video pages expose a target-specific
            // empty state even when the outer page is only a JavaScript shell.
            // Reuse the official landing endpoint for all supplier variants so
            // deleted Baijia/Yoojia items do not fall into generic review.
            if (host.EndsWith("baidu.com", StringComparison.Ordinal) || host.EndsWith("baidu.com.cn", StringComparison.Ordinal) ||
                host.EndsWith("yoojia.baidu.com", StringComparison.Ordinal) || host.EndsWith("yoojia.com", StringComparison.Ordinal))
            {
                string articleId = ExtractBaiduArticleId(original);
                Match dtArticle = Regex.Match(original.Query ?? "", @"(?:^|[?&])nid=dt_([0-9]{8,})", RegexOptions.IgnoreCase);
                if (String.IsNullOrWhiteSpace(articleId) && dtArticle.Success) articleId = dtArticle.Groups[1].Value;
                // The article landing endpoint is not a reliable video probe: live
                // video shares may render its generic empty shell. Video URLs are
                // handled by the dedicated Haokan/video probes below.
                if (!String.IsNullOrWhiteSpace(articleId))
                {
                    string articleNid = ExtractBaiduArticleNid(original);
                    string sharedUrl = articleNid.StartsWith("dt_", StringComparison.OrdinalIgnoreCase)
                        ? BuildBaiduPublicArticleUrl(articleId)
                        : "https://mbd.baidu.com/newspage/data/landingreact?nid=" + articleNid;
                    var sharedHeaders = new Dictionary<string, string>
                    {
                        { "User-Agent", articleNid.StartsWith("dt_", StringComparison.OrdinalIgnoreCase)
                            ? "Mozilla/5.0 (compatible; Baiduspider/2.0; +http://www.baidu.com/search/spider.html)"
                            : "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" },
                        { "Referer", "https://mbd.baidu.com/" }
                    };
                    ProbeResponse shared = articleNid.StartsWith("dt_", StringComparison.OrdinalIgnoreCase)
                        ? await TryReadCleanPublicProbeAsync(sharedUrl, sharedHeaders, token)
                        : await TryReadProbeAsync(sharedUrl, sharedHeaders, token);
                    if (shared != null && shared.Status == 200)
                    {
                        string sharedText = ExtractVisibleText(shared.Body);
                        string sharedBody = shared.Body ?? "";
                        bool targetIdentity = sharedBody.IndexOf(articleId, StringComparison.OrdinalIgnoreCase) >= 0;
                        if ((shared.FinalUrl ?? "").IndexOf("/newspage/data/error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            sharedBody.IndexOf("这里空空如也", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            Regex.IsMatch(sharedText, "内容不存在|文章(?:已|已经)删除|视频(?:已|已经)删除", RegexOptions.IgnoreCase))
                            return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                                "official-share-page", "百度系图文", articleId, "百度系官方共享页明确提示目标内容不存在或已删除", shared.FinalUrl, true);
                        if (targetIdentity && (MatchesExpectedTitle(expectedTitle, ExtractTitle(sharedBody) + " " + sharedText) ||
                            String.IsNullOrWhiteSpace(expectedTitle)))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                                "official-share-page", "百度系图文", articleId, "百度系官方共享页返回目标内容编号和正文标题", shared.FinalUrl, true);
                    }
                }
            }
            if (host.EndsWith("douyin.com", StringComparison.Ordinal) || host.EndsWith("iesdouyin.com", StringComparison.Ordinal))
            {
                Match articleIdentity = Regex.Match(original.AbsolutePath ?? "", @"/article/([0-9]{12,})(?:/|$)", RegexOptions.IgnoreCase);
                if (articleIdentity.Success &&
                    String.Equals(Environment.GetEnvironmentVariable("LINK_CHECKER_QUICK_PASS"), "1",
                        StringComparison.OrdinalIgnoreCase))
                {
                    string articleId = articleIdentity.Groups[1].Value;
                    RemoteEvidenceResponse articleCloud = await TryPublicCloudEvidenceAsync(original, token);
                    string articleCloudText = WebUtility.HtmlDecode(articleCloud == null ? "" :
                        (articleCloud.Title ?? "") + " " + (articleCloud.Text ?? ""));
                    bool articleIdMatch = articleCloudText.IndexOf(articleId, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (articleCloud != null && String.IsNullOrWhiteSpace(articleCloud.Error) &&
                        articleCloud.Status == 200 && articleIdMatch &&
                        MatchesExpectedContent(expectedTitle, expectedExcerpt, articleCloudText) &&
                        (String.IsNullOrWhiteSpace(expectedAuthor) || MatchesExpectedAuthor(expectedAuthor, articleCloudText)))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                            "public-cloud-reader", "抖音图文", articleId,
                            "抖音独立公开读取线路返回目标图文编号、匹配标题/正文和作者", articleCloud.FinalUrl, true);
                    if (articleCloud != null && String.IsNullOrWhiteSpace(articleCloud.Error) &&
                        articleCloud.Status == 200 && articleIdMatch && Regex.IsMatch(articleCloudText,
                            "作品不存在|内容不存在|该内容已删除|图文已删除|已下线", RegexOptions.IgnoreCase))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Strong,
                            "public-cloud-reader", "抖音图文", articleId,
                            "抖音独立公开读取线路明确提示目标图文不存在或已删除", articleCloud.FinalUrl, true);
                    return null;
                }
                identity = Regex.Match(original.AbsolutePath ?? "", @"/(?:share/)?(?:video|note)/([0-9]{12,})", RegexOptions.IgnoreCase);
                if (!identity.Success) return null;
                string id = identity.Groups[1].Value;
                string sourceUrl = original.AbsoluteUri;
                Task<PlatformProbeOutcome> operation = DouyinProbeCache.GetOrAdd(id,
                    ignored => ProbeDouyinContentAsync(sourceUrl, id, expectedTitle, expectedExcerpt, expectedAuthor, CancellationToken.None));
                Task finished = await Task.WhenAny(operation, Task.Delay(18000, token));
                if (finished != operation) { token.ThrowIfCancellationRequested(); return null; }
                return await operation;
            }
            if (host.EndsWith("kuaishou.com", StringComparison.Ordinal) || host.EndsWith("gifshow.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/(?:short-video|fw/photo)/([A-Za-z0-9]{10,})", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://m.gifshow.com/fw/photo/" + id;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" },
                        { "Referer", "https://www.kuaishou.com/" }
                    };
                    ProbeResponse probe;
                    await KuaishouProbeGate.WaitAsync(token);
                    try
                    {
                        probe = await TryReadProbeAsync(probeUrl, headers, token);
                        if (probe == null || probe.Status != 200 ||
                            (probe.Body ?? "").IndexOf("photoId=" + id, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            await Task.Delay(250, token);
                            probe = await TryReadProbeAsync(probeUrl, headers, token);
                        }
                    }
                    finally { KuaishouProbeGate.Release(); }
                    string currentCaption;
                    string currentAuthor;
                    if (probe != null && probe.Status == 200 &&
                        IsKuaishouRemovedSsrContent(probe.Body, id))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-mobile-page", "快手", id,
                            "快手官方作品页针对该作品返回“获取失败，作品可能已被删除或尚未上传”",
                            probe.FinalUrl, true);
                    if (probe != null && probe.Status == 200 &&
                        TryMatchKuaishouSsrContent(probe.Body, id, expectedTitle, expectedAuthor, out currentCaption, out currentAuthor))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-mobile-page", "快手", id, "快手官方移动页返回目标作品编号、公开状态、匹配文案" +
                                (String.IsNullOrWhiteSpace(currentAuthor) ? "" : "和作者“" + currentAuthor + "”"), probe.FinalUrl, true);
                }
            }
            if (host.EndsWith("share.dzh.com.cn", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.Query ?? "", @"(?:^|[?&])id=([A-Za-z0-9-]+)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    ProbeResponse probe = await TryReadProbeAsync(original.AbsoluteUri, null, token);
                    string currentTitle;
                    if (probe != null && probe.Status == 200 &&
                        TryMatchDzhArticlePage(probe.Body, id, expectedTitle, expectedExcerpt, out currentTitle))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-page-data", "大智慧", id,
                            "大智慧目标资讯页返回文章编号、Found=1 和匹配标题“" + currentTitle + "”", probe.FinalUrl, true);
                }
            }
            if (host == "zc.dingxinwen.cn")
            {
                identity = Regex.Match(original.Fragment ?? "", @"(?:^|[?&#])id=([0-9]+)(?:&|$)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string appId = "231020150243912027";
                    string timestamp = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds).ToString();
                    string sign = Md5Hex("app-id=" + appId + "authorization=timestamp=" + timestamp +
                        "ffdd7a25d87c05a7c5a019b837f5a05b");
                    string probeUrl = "https://community.topnews.cn/apiv2/api/topic/query?uuid=" + Uri.EscapeDataString(id);
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" },
                        { "Referer", "https://zc.dingxinwen.cn/" },
                        { "Origin", "https://zc.dingxinwen.cn" },
                        { "timestamp", timestamp },
                        { "app-id", appId },
                        { "authorization", "" },
                        { "sign", sign }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && probe.Status == 200 && IsDingxinwenMissingTopicResponse(probe.Body))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-api", "顶端柘城", id, "顶端柘城公开详情接口明确提示目标帖子不存在", probeUrl, true);
                }
            }
            if (host.EndsWith("xueqiu.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/([0-9]+)/([0-9]{8,})(?:/|$)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[2].Value;
                    // The normal page is frequently an anti-bot shell. The
                    // public status endpoint can still expose the post object;
                    // accept only an exact id plus body/author identity.
                    string statusUrl = "https://xueqiu.com/statuses/show.json?id=" + id;
                    ProbeResponse statusProbe = await TryReadProbeAsync(statusUrl, null, token);
                    if (statusProbe != null && statusProbe.Status == 200 &&
                        TryMatchXueqiuStatus(statusProbe.Body, id, expectedTitle, expectedExcerpt, expectedAuthor))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-api", "雪球", id, "雪球公开状态接口返回目标编号、正文和作者", statusUrl, true);
                    if (statusProbe != null && statusProbe.Status == 200 &&
                        IsXueqiuStatusRemoved(statusProbe.Body, id))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-api", "雪球", id, "雪球公开状态接口明确提示目标帖子不存在或已删除", statusUrl, true);
                    // Xueqiu's JSON endpoint is often replaced by an Aliyun WAF
                    // shell. The public page reader can still expose the target
                    // post body without opening a visible browser. Accept it only
                    // when the target identity/content is present or the page has
                    // a target-specific removal state.
                    if (String.Equals(Environment.GetEnvironmentVariable("LINK_CHECKER_QUICK_PASS"), "1",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        RemoteEvidenceResponse cloud = await TryPublicCloudEvidenceAsync(original, token);
                        string cloudText = WebUtility.HtmlDecode((cloud == null ? "" :
                            ((cloud.Title ?? "") + " " + (cloud.Text ?? ""))));
                        bool cloudIdMatch = cloudText.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (cloud != null && String.IsNullOrWhiteSpace(cloud.Error) && cloud.Status == 200 &&
                            cloudIdMatch && Regex.IsMatch(cloudText,
                                "原帖已被作者删除|帖子已被作者删除|该帖子不存在|该帖已删除",
                                RegexOptions.IgnoreCase))
                            return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                                "public-cloud-reader", "雪球", id,
                                "雪球独立公开读取线路明确提示原帖已被作者删除", cloud.FinalUrl, true);
                        bool cloudContentMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, cloudText);
                        bool cloudAuthorMatch = String.IsNullOrWhiteSpace(expectedAuthor) ||
                            MatchesExpectedAuthor(expectedAuthor, cloudText);
                        if (cloud != null && String.IsNullOrWhiteSpace(cloud.Error) && cloud.Status == 200 &&
                            cloudIdMatch && cloudContentMatch && cloudAuthorMatch)
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                                "public-cloud-reader", "雪球", id,
                                "雪球独立公开读取线路返回目标帖子编号、匹配正文和作者", cloud.FinalUrl, true);
                        var cloudResult = new CheckResult
                        {
                            OriginalUrl = original.AbsoluteUri,
                            FinalUrl = original.AbsoluteUri,
                            ExpectedTitle = expectedTitle ?? "",
                            ExpectedExcerpt = expectedExcerpt ?? "",
                            ExpectedAuthor = expectedAuthor ?? "",
                            Platform = "雪球"
                        };
                        if (ApplyRemoteEvidence(cloudResult, cloud, "public-cloud-reader",
                            new List<VerificationEvidence>(), original))
                            return ProbeOutcome(cloudResult.Verdict == "已失效"
                                    ? EvidenceKind.TargetRemovalExplicit : EvidenceKind.TargetContentPresent,
                                EvidenceStrength.Strong, "public-cloud-reader", "雪球", id,
                                cloudResult.Evidence, cloudResult.FinalUrl, true);
                    }
                    PlatformProbeOutcome xueqiuProbe = await ProbeRenderedSocialPostAsync(original.AbsoluteUri,
                        "雪球", id, expectedTitle, expectedExcerpt, expectedAuthor, token);
                    if (xueqiuProbe != null) return xueqiuProbe;
                }
            }
            if (host.EndsWith("tieba.baidu.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/p/([0-9]{8,})(?:/|$)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    PlatformProbeOutcome tiebaProbe = await ProbeRenderedSocialPostAsync(original.AbsoluteUri,
                        "百度贴吧", identity.Groups[1].Value, expectedTitle, expectedExcerpt, expectedAuthor, token);
                    if (tiebaProbe != null) return tiebaProbe;
                }
            }
            if (host.EndsWith("bilibili.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/(?:dynamic|opus)/([0-9]{8,})(?:/|$)", RegexOptions.IgnoreCase);
                if (!identity.Success && host == "t.bilibili.com")
                    identity = Regex.Match(original.AbsolutePath ?? "", @"/([0-9]{8,})(?:/|$)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string opusUrl = "https://www.bilibili.com/opus/" + id;
                    var opusHeaders = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" },
                        { "Referer", "https://www.bilibili.com/" }
                    };
                    ProbeResponse opusProbe;
                    await BilibiliProbeGate.WaitAsync(token);
                    try
                    {
                        opusProbe = await TryReadProbeAsync(opusUrl, opusHeaders, token);
                        await Task.Delay(250, token);
                    }
                    finally { BilibiliProbeGate.Release(); }
                    if (opusProbe != null && opusProbe.Status == 200 &&
                        TryMatchBilibiliOpusPage(opusProbe.Body, id, expectedTitle, expectedExcerpt, expectedAuthor))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-opus-page", "B站", id, "B站官方 Opus 页返回目标动态编号、匹配正文和作者", opusUrl, true);

                    PlatformProbeOutcome bilibiliProbe = await ProbeRenderedSocialPostAsync(original.AbsoluteUri,
                        "B站", id, expectedTitle, expectedExcerpt, expectedAuthor, token);
                    if (bilibiliProbe != null) return bilibiliProbe;
                }
            }
            if (host.EndsWith("iqiyi.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/(v_[A-Za-z0-9]+)\.html(?:/|$)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (compatible; Baiduspider/2.0; +http://www.baidu.com/search/spider.html)" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" },
                        { "Referer", "https://www.iqiyi.com/" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(original.AbsoluteUri, headers, token);
                    if (probe != null && probe.Status == 200 &&
                        TryMatchIqiyiCrawlerPage(probe.Body, id, expectedTitle, expectedExcerpt, expectedAuthor))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-crawler-page", "爱奇艺", id, "爱奇艺公开索引页返回目标视频编号、匹配标题和发布者", probe.FinalUrl, true);
                }
            }
            if (host.EndsWith("weibo.com", StringComparison.Ordinal) || host.EndsWith("weibo.cn", StringComparison.Ordinal))
            {
                Match longArticle = Regex.Match(original.Query ?? "", @"(?:^|[?&])id=([0-9]{12,})", RegexOptions.IgnoreCase);
                if (longArticle.Success && (original.AbsolutePath ?? "").IndexOf("/ttarticle/p/show", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string id = longArticle.Groups[1].Value;
                    ProbeResponse page = await TryReadProbeAsync(original.AbsoluteUri, null, token);
                    if (page != null && page.Status == 200)
                    {
                        // Weibo long articles may be truncated for non-followers. The
                        // authoritative title, article id and owner in the page config
                        // still prove that the target article exists; do not require
                        // the supplier's first-sentence title to be repeated in body.
                        bool live = TryMatchWeiboLongArticle(page.Body, id, expectedTitle, expectedExcerpt, expectedAuthor) ||
                            IsWeiboLongArticleIdentityPresent(page.Body, id, expectedTitle, expectedAuthor);
                        if (live)
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-page", "微博长文", id, "微博长文原页返回目标编号、页面标题和发布账号", page.FinalUrl, true);
                        if (IsWeiboLongArticleRemovalPage(page.Body, id))
                            return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                                "official-page", "微博长文", id, "微博长文原页明确提示目标文章不存在或已删除", page.FinalUrl, true);
                    }
                }
                identity = Regex.Match(original.AbsolutePath ?? "", @"/[0-9]+/([A-Za-z0-9]+)(?:/|$)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    PlatformProbeOutcome weiboProbe = await ProbeWeiboStatusAsync(id, expectedTitle, expectedExcerpt, expectedAuthor, token);
                    if (weiboProbe != null) return weiboProbe;
                }
            }
            if (host.EndsWith("10jqka.com.cn", StringComparison.Ordinal))
            {
                Match contentId = Regex.Match(original.Query ?? "", @"(?:^|[?&])contentId=([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
                if (contentId.Success)
                {
                    string id = contentId.Groups[1].Value;
                    string probeUrl = "https://c.10jqka.com.cn/lgt/post/open/api/post/info/get?content_id=" + id;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" },
                        { "Accept", "application/json, text/plain, */*" },
                        { "Referer", "https://c.10jqka.com.cn/m/post/discussDetail/?contentId=" + id }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && probe.Status == 200)
                    {
                        string body = probe.Body ?? "";
                        if (IsTonghuashunRemovedResponse(body))
                            return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                                "official-api", "同花顺社区", id, "同花顺官方公开详情接口明确确认目标帖子已被删除", probeUrl, true);

                        string currentAuthor;
                        if (TryMatchTonghuashunPost(body, id, expectedTitle, expectedExcerpt, out currentAuthor))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-api", "同花顺社区", id, "同花顺官方公开详情接口返回目标帖子编号和匹配正文" +
                                    (String.IsNullOrWhiteSpace(currentAuthor) ? "" : "，当前作者“" + currentAuthor + "”"), probeUrl, true);
                    }
                }

                Match articlePid = Regex.Match(original.Query ?? "", @"(?:^|[?&])pid=([0-9]+)", RegexOptions.IgnoreCase);
                if (articlePid.Success && (original.AbsolutePath ?? "").IndexOf("article_detail", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string id = articlePid.Groups[1].Value;
                    string probeUrl = "https://t.10jqka.com.cn/lgt/article_query/open/api/article/v1/detail?pid=" + id;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" },
                        { "Accept", "application/json, text/plain, */*" },
                        { "Referer", "https://t.10jqka.com.cn/lgt/article_detail/index.html?pid=" + id }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && probe.Status == 200)
                    {
                        string body = probe.Body ?? "";
                        bool targetId = Regex.IsMatch(body, "\\\"pid\\\"\\s*:\\s*" + Regex.Escape(id) + "(?:,|})", RegexOptions.IgnoreCase);
                        string currentTitle = ExtractJsonStringLong(body, "title", 1000);
                        string currentContent = ExtractJsonStringLong(body, "content", 12000);
                        if (targetId && !String.IsNullOrWhiteSpace(currentContent) &&
                            MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + currentContent))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-api", "同花顺文章", id, "同花顺官方公开文章接口返回目标文章编号、匹配标题和正文", probeUrl, true);
                    }
                }
                Match mobileNewsId = Regex.Match(original.AbsolutePath ?? "", @"/m([0-9]+)/?$", RegexOptions.IgnoreCase);
                if (mobileNewsId.Success)
                {
                    ProbeResponse page = await TryReadProbeAsync(original.AbsoluteUri, null, token);
                    string redirect = page == null ? "" : (page.FinalUrl ?? "");
                    if (page != null && page.Status == 200 && !String.IsNullOrWhiteSpace(redirect) &&
                        !redirect.Contains("10jqka.com.cn"))
                    {
                        string redirectedText = ExtractTitle(page.Body) + " " + ExtractProbableMainContentText(page.Body) + " " + ExtractVisibleText(page.Body);
                        // Supplier feeds occasionally label a syndicated article with
                        // a platform title. A same-request cross-site redirect is
                        // acceptable only when the final page carries the original
                        // headline/author or a reliable fragment of the excerpt.
                        bool titleMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, redirectedText) ||
                            MatchesExpectedTitleByFragments(expectedTitle, redirectedText);
                        // A cross-site syndicated source may legitimately show its
                        // own byline instead of the supplier's platform account.
                        // Require the target headline/body identity, but do not turn
                        // a byline mismatch into a false review.
                        if (titleMatch)
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                                "official-redirect", "同花顺资讯", mobileNewsId.Groups[1].Value,
                                "同花顺资讯原链接跳转到匹配原标题、正文或发布作者的来源文章", redirect, true);
                    }
                }
            }

            // 生活圈 shares redirect to /simple/<id>.  That page is a
            // target-level empty state, not a generic shell or a network
            // failure, so it can be resolved without a browser pass.
            if (host.EndsWith("fafengtuqiang.cn", StringComparison.Ordinal))
            {
                ProbeResponse pageProbe = await TryReadProbeAsync(original.AbsoluteUri, null, token);
                if (pageProbe != null && pageProbe.Status == 200)
                {
                    string pageTitle = ExtractTitle(pageProbe.Body);
                    string pageText = ExtractVisibleText(pageProbe.Body);
                    if (Regex.IsMatch(pageTitle + " " + pageText, "文章找不到啦|文章不存在|内容不存在", RegexOptions.IgnoreCase))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-page", "生活圈", "", "生活圈页面明确提示文章找不到", pageProbe.FinalUrl, true);
                }
            }

            // Topnews share pages are a JavaScript shell.  The same public API
            // used by the share page exposes target id, visibility flags and
            // decoded body, so use it as the authoritative fast-pass source.
            if (host.EndsWith("dingxinwen.com", StringComparison.Ordinal) ||
                host.EndsWith("dingnews.net", StringComparison.Ordinal) ||
                host.EndsWith("topnews.cn", StringComparison.Ordinal))
            {
                Match topnewsId = Regex.Match(original.Query ?? "", @"(?:^|[?&])id=([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
                if (!topnewsId.Success)
                    topnewsId = Regex.Match(original.AbsolutePath ?? "", @"/news/([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
                if (topnewsId.Success)
                {
                    string id = topnewsId.Groups[1].Value;
                    string apiUrl = BuildTopnewsApiUrl(id);
                    ProbeResponse api = await TryReadProbeAsync(apiUrl, new Dictionary<string, string>
                    {
                        { "Referer", original.AbsoluteUri },
                        { "Accept", "application/json, text/plain, */*" }
                    }, token);
                    if (api != null && api.Status == 200)
                    {
                        int apiCode = ExtractJsonInt(api.Body, "code", Int32.MinValue);
                        string data = ExtractJsonObject(api.Body, "data");
                        int returnedId = ExtractJsonInt(data, "n_id", Int32.MinValue);
                        int deleted = ExtractJsonInt(data, "n_del", -1);
                        int privateOnly = ExtractJsonInt(data, "n_only_me_see", -1);
                        string content = ExtractJsonStringLong(data, "n_content", 30000);
                        string currentTitle = ExtractJsonStringLong(data, "n_title", 1500);
                        string author = ExtractJsonStringLong(data, "n_author", 300);
                        if (apiCode == 0 && returnedId.ToString() == id && (deleted != 0 || privateOnly != 0 || String.IsNullOrWhiteSpace(content)))
                            return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                                "official-api", "顶端新闻", id, "顶端新闻官方接口确认目标稿件已下线或不可公开查看", apiUrl, true);
                        if (apiCode == 0 && returnedId.ToString() == id && !String.IsNullOrWhiteSpace(content) &&
                            (MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + content) ||
                             String.IsNullOrWhiteSpace(expectedTitle)))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-api", "顶端新闻", id, "顶端新闻官方接口返回目标稿件编号、公开状态和正文" +
                                    (String.IsNullOrWhiteSpace(author) ? "" : "，作者“" + author + "”"), apiUrl, true);
                    }
                }
            }

            // Interactive Easy pages expose the question body through the
            // public JSON route used by their Vue client.
            if (host == "irm.cninfo.com.cn" || host.EndsWith(".cninfo.com.cn", StringComparison.Ordinal))
            {
                Match questionId = Regex.Match(original.Query ?? "", @"(?:^|[?&])questionId=([0-9]+)", RegexOptions.IgnoreCase);
                if (questionId.Success)
                {
                    string id = questionId.Groups[1].Value;
                    string apiUrl = "https://irm.cninfo.com.cn/newircs/question/getQuestionDetail?questionId=" + id +
                        "&_t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                    ProbeResponse api = await TryReadProbeAsync(apiUrl, new Dictionary<string, string>
                    {
                        { "Referer", original.AbsoluteUri },
                        { "X-Requested-With", "XMLHttpRequest" },
                        { "Accept", "application/json, text/plain, */*" }
                    }, token);
                    if (api != null && api.Status == 200)
                    {
                        int status = ExtractJsonInt(api.Body, "statusCode", Int32.MinValue);
                        string body = ExtractJsonStringLong(api.Body, "questionContent", 30000);
                        string questioner = ExtractJsonString(api.Body, "questioner");
                        if (status == 200 && !String.IsNullOrWhiteSpace(body) &&
                            MatchesExpectedContent(expectedTitle, expectedExcerpt, body) &&
                            (String.IsNullOrWhiteSpace(expectedAuthor) || MatchesExpectedAuthor(expectedAuthor, questioner + " " + api.Body)))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-api", "互动易", id, "深交所互动易官方接口返回目标问题正文和提问账号", apiUrl, true);
                    }
                }
            }
            if (host.EndsWith("qctt.cn", StringComparison.Ordinal))
            {
                Match video = Regex.Match(original.AbsolutePath ?? "", @"/video/([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
                if (video.Success)
                {
                    ProbeResponse page = await TryReadProbeAsync(original.AbsoluteUri, null, token);
                    if (page != null && page.Status == 200 &&
                        TryMatchQcttVideoPage(page.Body, video.Groups[1].Value, expectedTitle, expectedExcerpt, expectedAuthor))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-page", "汽车头条", video.Groups[1].Value,
                            "汽车头条视频原页返回目标视频编号、匹配标题和作者", page.FinalUrl, true);
                }
            }
            if (host.EndsWith("hexun.com", StringComparison.Ordinal))
            {
                Match newsId = Regex.Match(original.Query ?? "", @"(?:^|[?&])nid=([0-9]+)", RegexOptions.IgnoreCase);
                if (newsId.Success)
                {
                    string id = newsId.Groups[1].Value;
                    string probeUrl = "https://wapi.hexun.com/detail_master.cc?newsId=" + id + "&version=808";
                    ProbeResponse page = await TryReadProbeAsync(probeUrl, null, token);
                    if (page != null && page.Status == 200 &&
                        TryMatchHexunNewsResponse(page.Body, id, expectedTitle, expectedExcerpt))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-api", "和讯", id, "和讯公开正文接口返回目标新闻编号、匹配标题和正文", probeUrl, true);
                }
            }
            if (host.EndsWith("emcreative.eastmoney.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/Share_ArticleDetail/([0-9]{16,})", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://caifuhao.eastmoney.com/news/" + id;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" },
                        { "Referer", "https://emcreative.eastmoney.com/" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && probe.Status == 200)
                    {
                        string body = probe.Body ?? "";
                        string visible = ExtractVisibleText(body);
                        if (IsEastmoneyFortuneRemovedPage(visible))
                            return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                                "official-public-page", "东方财富财富号", id, "东方财富财富号官方正文页明确提示目标文章已被删除", probeUrl, true);

                        bool targetId = body.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
                        string currentTitle = ExtractTitle(body);
                        if (targetId && MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + visible))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-public-page", "东方财富财富号", id, "东方财富财富号官方正文页返回目标编号、匹配标题和正文", probeUrl, true);
                    }
                }
            }
            if (host.EndsWith("bilibili.com", StringComparison.Ordinal))
            {
                Match articleIdentity = Regex.Match(original.AbsolutePath ?? "", @"/read/cv([0-9]{6,})(?:/|$)", RegexOptions.IgnoreCase);
                if (articleIdentity.Success)
                {
                    string articleId = articleIdentity.Groups[1].Value;
                    string articleProbeUrl = "https://api.bilibili.com/x/article/viewinfo?id=" + articleId;
                    var articleHeaders = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126 Safari/537.36" },
                        { "Referer", original.AbsoluteUri },
                        { "Accept", "application/json" }
                    };
                    ProbeResponse articleProbe = await TryReadProbeAsync(articleProbeUrl, articleHeaders, token);
                    if (articleProbe != null && (articleProbe.Status == 429 || articleProbe.Status == 403 || articleProbe.Status == 200) &&
                        !IsBilibiliArticleApiSuccess(articleProbe.Body))
                    {
                        ProbeResponse directArticle = await ReadProbeWithClientAsync(_directClient, articleProbeUrl, articleHeaders, token);
                        if (directArticle != null && directArticle.Status == 200 &&
                            IsBilibiliArticleApiSuccess(directArticle.Body))
                            articleProbe = directArticle;
                    }
                    if (articleProbe != null && articleProbe.Status == 200)
                    {
                        string articleBody = articleProbe.Body ?? "";
                        bool articleRemoved;
                        bool articleMatch = TryMatchBilibiliArticleInfo(articleBody, articleId, expectedTitle,
                            expectedExcerpt, expectedAuthor, out articleRemoved);
                        if (articleRemoved)
                            return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                                "official-api", "哔哩哔哩专栏", articleId, "B站官方专栏接口确认目标文章不存在", articleProbeUrl, true);
                        if (articleMatch)
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-api", "哔哩哔哩专栏", articleId, "B站官方专栏接口返回目标文章编号、匹配标题和作者", articleProbeUrl, true);
                    }
                }
                identity = Regex.Match(original.AbsolutePath ?? "", @"/video/(?:av)?([0-9]{8,})", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string aid = identity.Groups[1].Value;
                    string probeUrl = "https://api.bilibili.com/x/web-interface/view?aid=" + aid;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36" },
                        { "Referer", "https://www.bilibili.com/" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe == null || probe.Status != 200) return null;
                    string body = probe.Body ?? "";
                    int apiCode;
                    Match codeMatch = Regex.Match(body, "\\\"code\\\"\\s*:\\s*(-?[0-9]+)");
                    if (!codeMatch.Success || !Int32.TryParse(codeMatch.Groups[1].Value, out apiCode)) return null;
                    if (apiCode == -404)
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-api", "哔哩哔哩", aid, "B站官方视频接口确认目标 AV 编号不存在", probeUrl, true);
                    string currentAid = ExtractJsonString(body, "aid");
                    if (String.IsNullOrEmpty(currentAid))
                    {
                        Match aidMatch = Regex.Match(body, "\\\"aid\\\"\\s*:\\s*" + Regex.Escape(aid) + "(?:,|})");
                        if (aidMatch.Success) currentAid = aid;
                    }
                    string currentTitle = ExtractJsonString(body, "title");
                    string currentOwner = "";
                    Match ownerMatch = Regex.Match(body, "\\\"owner\\\"\\s*:\\s*\\{[^}]*\\\"name\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase);
                    if (ownerMatch.Success) currentOwner = CleanText(WebUtility.HtmlDecode(DecodeJsonUnicode(Regex.Unescape(ownerMatch.Groups[1].Value))), 180);
                    bool idMatch = currentAid == aid;
                    bool titleMatch = MatchesExpectedTitle(expectedTitle, currentTitle);
                    bool authorMatch = String.IsNullOrWhiteSpace(expectedAuthor) || MatchesExpectedAuthor(expectedAuthor, currentOwner);
                    if (idMatch && titleMatch && authorMatch)
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                            "official-api", "哔哩哔哩", aid, "B站官方视频接口返回目标 AV 编号和匹配的视频标题" +
                            (String.IsNullOrWhiteSpace(currentOwner) ? "" : "，作者“" + currentOwner + "”"), probeUrl, true);
                    return null;
                }
            }
            if (host == "t.bilibili.com" || host.EndsWith(".t.bilibili.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/([0-9]{8,})", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string dynamicId = identity.Groups[1].Value;
                    // The desktop endpoint is the public endpoint used by the
                    // current Bilibili web client. The legacy v1 route increasingly
                    // returns -352 even for public dynamics, so try desktop first
                    // and retain the legacy fallback for older deployments.
                    string probeUrl = "https://api.bilibili.com/x/polymer/web-dynamic/desktop/v1/detail?id=" + dynamicId + "&features=itemOpusStyle";
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126 Safari/537.36" },
                        { "Referer", original.AbsoluteUri },
                        { "Origin", "https://www.bilibili.com" },
                        { "Accept", "application/json, text/plain, */*" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && probe.Status == 200 && ExtractJsonInt(probe.Body, "code", Int32.MinValue) == -352)
                    {
                        string legacyUrl = "https://api.bilibili.com/x/polymer/web-dynamic/v1/detail?id=" + dynamicId;
                        ProbeResponse legacy = await TryReadProbeAsync(legacyUrl, headers, token);
                        if (legacy != null && legacy.Status == 200 && ExtractJsonInt(legacy.Body, "code", Int32.MinValue) != -352)
                        {
                            probeUrl = legacyUrl;
                            probe = legacy;
                        }
                    }
                    if (probe != null && probe.Status == 200)
                    {
                        int apiCode = ExtractJsonInt(probe.Body, "code", Int32.MinValue);
                        string apiMessage = ExtractJsonString(probe.Body, "message");
                        if (apiCode == 4101152 || apiMessage.IndexOf("动态不可见", StringComparison.OrdinalIgnoreCase) >= 0)
                            return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                                "official-api", "哔哩哔哩动态", dynamicId, "B站官方动态接口确认目标动态不可见", probeUrl, true);
                        if (apiCode == 0 && TryMatchBilibiliDynamicInfo(probe.Body, dynamicId, expectedTitle, expectedExcerpt, expectedAuthor))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-api", "B站动态", dynamicId, "B站官方动态接口返回目标动态编号、匹配内容和作者", probeUrl, true);
                    }
                    PlatformProbeOutcome renderedDynamic = await ProbeRenderedSocialPostAsync(original.AbsoluteUri,
                        "B站动态", dynamicId, expectedTitle, expectedExcerpt, expectedAuthor, token);
                    if (renderedDynamic != null) return renderedDynamic;
                }
            }

            Match choiceNews = Regex.Match(original.Fragment ?? "", @"(?:^|[?&])infoCode=(?:SN)?([0-9]+)", RegexOptions.IgnoreCase);
            Match fundNews = Regex.Match(original.Query ?? "", @"(?:^|[?&])code=([0-9]{12,})", RegexOptions.IgnoreCase);
            if ((host.EndsWith("choicew2z.eastmoney.com", StringComparison.Ordinal) && choiceNews.Success) ||
                (host.EndsWith("1234567.com.cn", StringComparison.Ordinal) && fundNews.Success))
            {
                string id = choiceNews.Success ? choiceNews.Groups[1].Value : fundNews.Groups[1].Value;
                string probeUrl = "https://emwap.eastmoney.com/a/" + id + ".html";
                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                    { "Accept-Language", "zh-CN,zh;q=0.9" },
                    { "Referer", "https://wap.eastmoney.com/" }
                };
                ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                if (probe != null && (probe.Status == 404 || probe.Status == 410 ||
                    LooksLikeErrorPage(probe.FinalUrl, ExtractTitle(probe.Body), ExtractVisibleText(probe.Body))))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                        "official-mobile-page", "东方财富资讯", id, "东方财富官方移动正文页确认目标新闻不存在", probe.FinalUrl, true);
                if (probe != null && probe.Status == 200)
                {
                    string pageText = ExtractTitle(probe.Body) + " " + ExtractVisibleText(probe.Body);
                    bool idMatch = (probe.FinalUrl ?? probeUrl).IndexOf("/a/" + id + ".html", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (probe.Body ?? "").IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (idMatch && MatchesExpectedContent(expectedTitle, expectedExcerpt, pageText))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-mobile-page", "东方财富资讯", id, "东方财富官方移动正文页返回目标新闻编号和匹配正文", probe.FinalUrl, true);
                }
            }

            if (host.EndsWith("gf.com.cn", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.Fragment ?? "", @"(?:^#)?/detail/([a-f0-9]{16,})", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://info.gf.com.cn/api/1.0.0/read/article/" + id + "?platform=web&appId=info";
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126 Safari/537.36" },
                        { "Referer", original.AbsoluteUri },
                        { "Accept", "application/json" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && probe.Status == 200)
                    {
                        string body = probe.Body ?? "";
                        int errorCode = ExtractJsonInt(body, "errCode", Int32.MinValue);
                        string currentId = ExtractJsonString(body, "id");
                        string currentTitle = ExtractJsonString(body, "title");
                        string currentContent = ExtractJsonStringLong(body, "content", 12000);
                        if (errorCode == 0 && String.Equals(currentId, id, StringComparison.OrdinalIgnoreCase) &&
                            !String.IsNullOrWhiteSpace(currentContent) && MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + currentContent))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-api", "广发易淘金", id, "广发易淘金官方详情接口返回目标编号、匹配标题和完整正文", probeUrl, true);
                        if (errorCode == 0 && Regex.IsMatch(body, "\"data\"\\s*:\\s*null", RegexOptions.IgnoreCase))
                            return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Strong,
                                "official-api", "广发易淘金", id, "广发易淘金官方详情接口确认目标编号已无内容", probeUrl, true);
                    }
                }
            }

            if (host == "guba.eastmoney.com" || host.EndsWith(".guba.eastmoney.com", StringComparison.Ordinal) ||
                host == "mguba.eastmoney.com" || host.EndsWith(".mguba.eastmoney.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/mguba/article/[^/]+/([0-9]+)", RegexOptions.IgnoreCase);
                if (!identity.Success)
                    identity = Regex.Match(original.AbsolutePath ?? "", @"/news,[^,]*,([0-9]+)\.html", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://mguba.eastmoney.com/api/getArticle?postid=" + id;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" },
                        { "Accept", "application/json, text/plain, */*" },
                        { "Referer", "https://mguba.eastmoney.com/mguba/article/0/" + id },
                        { "Origin", "https://mguba.eastmoney.com" }
                    };
                    string form = "deviceid=ugc&version=200&plat=wap&product=guba&ctoken=&utoken=&postid=" + id +
                        "&type=0&cutword=true&paytext=true&location=WAP%7CArticle%7Cwap%7CTRUE&env=prod&bizfrom=ugc";
                    ProbeResponse probe = await TryPostProbeAsync(probeUrl, form, headers, token);
                    if (probe != null && probe.Status == 200)
                    {
                        string body = probe.Body ?? "";
                        bool targetId = Regex.IsMatch(body, "\\\"post_id\\\"\\s*:\\s*\\\"?" + Regex.Escape(id) + "\\\"?(?:,|})");
                        int state = ExtractJsonInt(body, "post_state", Int32.MinValue);
                        string currentTitle = ExtractJsonStringLong(body, "post_title", 1000);
                        string currentContent = ExtractJsonStringLong(body, "post_content", 12000);
                        string currentAuthor = ExtractJsonString(body, "user_nickname");
                        if (targetId && state == 1 && Regex.IsMatch(currentTitle + " " + currentContent,
                            "帖子不存在|帖子已删除|访问的帖子不存在", RegexOptions.IgnoreCase))
                            return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                                "official-api", "东方财富股吧", id, "东方财富股吧当前官方接口明确标记目标帖子已删除或不存在", probeUrl, true);

                        bool contentMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + currentContent);
                        if (targetId && state == 0 && !String.IsNullOrWhiteSpace(currentContent) && contentMatch)
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-api", "东方财富股吧", id, "东方财富股吧当前官方接口返回目标帖子编号和匹配正文" +
                                    (String.IsNullOrWhiteSpace(currentAuthor) ? "" : "，当前作者“" + currentAuthor + "”"), probeUrl, true);
                    }
                }
            }

            if (host.EndsWith("yiche.com", StringComparison.Ordinal) || host.EndsWith("bitauto.com", StringComparison.Ordinal))
            {
                string forum;
                string threadId;
                if (TryExtractYicheThreadIdentity(original, out forum, out threadId))
                {
                    string probeUrl = "https://baa.m.yiche.com/" + forum + "/thread-" + threadId + ".html";
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && (probe.Status == 404 || probe.Status == 410 ||
                        LooksLikeErrorPage(probe.FinalUrl, ExtractTitle(probe.Body), ExtractVisibleText(probe.Body))))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-mobile-page", "易车论坛", threadId, "易车论坛官方移动页确认目标帖子不存在", probe.FinalUrl, true);
                    if (probe != null && probe.Status == 200)
                    {
                        string pageText = ExtractTitle(probe.Body) + " " + ExtractVisibleText(probe.Body);
                        bool idMatch = (probe.FinalUrl ?? probeUrl).IndexOf("thread-" + threadId + ".html", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (probe.Body ?? "").IndexOf("thread-" + threadId + ".html", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (idMatch && MatchesExpectedContent(expectedTitle, expectedExcerpt, pageText))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-mobile-page", "易车论坛", threadId, "易车论坛官方移动页返回目标帖子编号和匹配正文", probe.FinalUrl, true);
                    }
                }

                identity = Regex.Match(original.AbsolutePath ?? "", @"/hao/wenzhang/([0-9]+)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://news.m.yiche.com/hao/wenzhang/" + id;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && (probe.Status == 404 || probe.Status == 410 ||
                        LooksLikeErrorPage(probe.FinalUrl, ExtractTitle(probe.Body), ExtractVisibleText(probe.Body))))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-mobile-page", "易车", id, "易车官方移动页确认目标文章不存在", probe.FinalUrl, true);
                    if (probe != null && probe.Status == 200)
                    {
                        string visible = ExtractVisibleText(probe.Body);
                        bool idMatch = (probe.FinalUrl ?? probeUrl).IndexOf("/wenzhang/" + id, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool contentMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, ExtractTitle(probe.Body) + " " + visible);
                        bool authorMatch = MatchesExpectedAuthor(expectedAuthor, visible + " " + (probe.Body ?? ""));
                        if (idMatch && contentMatch && (String.IsNullOrWhiteSpace(expectedAuthor) || authorMatch))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-mobile-page", "易车", id, "易车官方移动正文页返回目标编号、匹配标题和发文作者", probe.FinalUrl, true);
                    }
                }
            }

            string ximalayaTrackId = ExtractXimalayaTrackId(original);
            if (host.EndsWith("ximalaya.com", StringComparison.Ordinal) && !String.IsNullOrWhiteSpace(ximalayaTrackId))
            {
                string probeUrl = "https://www.ximalaya.com/revision/track/simple?trackId=" + ximalayaTrackId;
                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                    { "Referer", original.AbsoluteUri },
                    { "Accept", "application/json" }
                };
                ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                if (probe != null && probe.Status == 200)
                {
                    string body = probe.Body ?? "";
                    if (IsXimalayaMissingResponse(body, ximalayaTrackId))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-api", "喜马拉雅", ximalayaTrackId, "喜马拉雅官方声音接口明确确认目标声音已下架", probeUrl, true);

                    int ret = ExtractJsonInt(body, "ret", Int32.MinValue);
                    string currentTitle = ExtractJsonString(body, "title");
                    string richIntro = ExtractJsonStringLong(body, "richIntro", 12000);
                    bool idMatch = Regex.IsMatch(body, "\\\"trackId\\\"\\s*:\\s*" + Regex.Escape(ximalayaTrackId) + "(?:[^0-9]|$)", RegexOptions.IgnoreCase);
                    if (ret == 200 && idMatch && !String.IsNullOrWhiteSpace(currentTitle) &&
                        MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + richIntro))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-api", "喜马拉雅", ximalayaTrackId, "喜马拉雅官方声音接口返回目标编号、匹配标题和声音简介", probeUrl, true);
                }
            }
            if (host.EndsWith("toutiao.com", StringComparison.Ordinal))
            {
                // Toutiao uses both /item/<id> and the shorter /i<id> form;
                // the latter is common in supplier exports and was previously
                // skipped before reaching the official content endpoint.
                identity = Regex.Match(original.AbsolutePath ?? "", @"/(?:item|article|video|w)/([0-9]{7,})|/i([0-9]{7,})", RegexOptions.IgnoreCase);
                if (!identity.Success) return null;
                string id = identity.Groups[1].Success ? identity.Groups[1].Value : identity.Groups[2].Value;
                string publicUrl = "https://www.toutiao.com" + original.AbsolutePath.TrimEnd('/') + "/";
                var publicHeaders = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (compatible; Baiduspider/2.0; +http://www.baidu.com/search/spider.html)" },
                    { "Accept-Language", "zh-CN,zh;q=0.9" }
                };
                ProbeResponse publicPage = await TryReadProbeAsync(publicUrl, publicHeaders, token);
                if (publicPage != null && (publicPage.Status == 404 || publicPage.Status == 410))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                        "official-public-page", "今日头条", id, "今日头条当前公开作品页返回 HTTP " + publicPage.Status + "，目标内容已不可访问", publicPage.FinalUrl, true);
                if (publicPage != null && publicPage.Status == 200)
                {
                    string publicBody = publicPage.Body ?? "";
                    string publicText = ExtractTitle(publicBody) + " " + ExtractVisibleText(publicBody);
                    bool publicIdMatch = publicBody.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (publicIdMatch && MatchesExpectedContent(expectedTitle, expectedExcerpt, publicText))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-public-page", "今日头条", id, "今日头条当前公开作品页返回目标编号和匹配正文", publicPage.FinalUrl, true);
                }
                string probeUrl = "https://m.toutiao.com/i" + id + "/info/";
                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                    { "Accept-Language", "zh-CN,zh;q=0.9" },
                    { "Referer", "https://m.toutiao.com/" }
                };
                ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                if (probe == null) return null;
                string body = probe.Body;
                // Public APIs can retain cached records after the public article has been removed.
                // They may prove absence, but presence must still be confirmed on the current page.
                if (probe.Status == 200 && Regex.IsMatch(body ?? "", "\\\"data\\\"\\s*:\\s*null") && Regex.IsMatch(body ?? "", "\\\"success\\\"\\s*:\\s*false"))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                        "official-api", "今日头条", id, "今日头条公开内容接口确认目标内容不存在", probeUrl, true);
                // The public endpoint uses a non-empty data object with group_source=578
                // for removed items. It contains only the id and no title/content, while
                // live articles expose title/content and a normal source.
                if (probe.Status == 200)
                {
                    Match groupSource = Regex.Match(body ?? "", "\\\"group_source\\\"\\s*:\\s*([0-9]+)", RegexOptions.IgnoreCase);
                    string currentTitle = ExtractJsonString(body, "title");
                    string currentContent = ExtractJsonString(body, "content");
                    if (groupSource.Success && groupSource.Groups[1].Value == "578" &&
                        String.IsNullOrWhiteSpace(currentTitle) && String.IsNullOrWhiteSpace(currentContent))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-api", "今日头条", id, "今日头条公开内容接口返回目标编号但正文已不可用", probeUrl, true);

                    string currentId = ExtractJsonString(body, "gid");
                    string currentAuthor = ExtractToutiaoAuthor(body);
                    bool idMatch = String.Equals(currentId, id, StringComparison.OrdinalIgnoreCase);
                    bool contentMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + currentContent);
                    bool authorMatch = MatchesExpectedAuthor(expectedAuthor, currentAuthor + " " + body);
                    bool currentContentType = groupSource.Success && IsCurrentToutiaoContentSource(groupSource.Groups[1].Value);
                    bool hasBody = !String.IsNullOrWhiteSpace(currentTitle) && !String.IsNullOrWhiteSpace(currentContent);
                    if (idMatch && currentContentType && hasBody && contentMatch &&
                        (String.IsNullOrWhiteSpace(expectedAuthor) || authorMatch))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-api", "今日头条", id, "今日头条公开详情接口返回目标编号、当前内容类型、匹配正文" +
                                (authorMatch ? "和作者“" + expectedAuthor.Trim() + "”" : ""), probeUrl, true);
                }
                return null;
            }

            if (host.EndsWith("zhihu.com", StringComparison.Ordinal))
            {
                Match pinIdentity = Regex.Match(original.AbsolutePath ?? "", @"/pin/([0-9]+)", RegexOptions.IgnoreCase);
                if (pinIdentity.Success)
                {
                    string pinId = pinIdentity.Groups[1].Value;
                    string pinUrl = "https://api.zhihu.com/pins/" + pinId;
                    var pinHeaders = new Dictionary<string, string>
                    {
                        { "User-Agent", "osee2unifiedRelease/19540 osee2unifiedReleaseVersion/10.56.0 Mozilla/5.0" },
                        { "x-api-version", "3.0.91" },
                        { "Referer", "https://www.zhihu.com/" }
                    };
                    ProbeResponse pinProbe;
                    await ZhihuProbeGate.WaitAsync(token);
                    try
                    {
                        int delayMilliseconds;
                        lock (ZhihuProbeTimingSync)
                            delayMilliseconds = Math.Max(0, 1200 - (int)(DateTime.UtcNow - _lastZhihuProbeStartedUtc).TotalMilliseconds);
                        if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, token);
                        lock (ZhihuProbeTimingSync) _lastZhihuProbeStartedUtc = DateTime.UtcNow;
                        pinProbe = await ReadProbeWithClientAsync(_zhihuClient, pinUrl, pinHeaders, token);
                    }
                    finally { ZhihuProbeGate.Release(); }
                    if (pinProbe != null && pinProbe.Status == 200)
                    {
                        string decoded = WebUtility.HtmlDecode(DecodeJsonUnicode(pinProbe.Body ?? ""));
                        bool idMatch = Regex.IsMatch(pinProbe.Body ?? "", "\\\"id\\\"\\s*:\\s*\\\"" + Regex.Escape(pinId) + "\\\"");
                        string currentContent = ExtractJsonStringLong(pinProbe.Body, "content", 12000);
                        bool contentMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, decoded);
                        bool authorMatch = String.IsNullOrWhiteSpace(expectedAuthor) || MatchesExpectedAuthor(expectedAuthor, decoded);
                        if (idMatch && !String.IsNullOrWhiteSpace(currentContent) && contentMatch && authorMatch)
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-api", "知乎想法", pinId, "知乎公开想法接口返回目标编号、正文和发文作者", pinUrl, true);
                    }
                    return null;
                }

                identity = Regex.Match(original.AbsolutePath ?? "", @"/answer/([0-9]+)", RegexOptions.IgnoreCase);
                if (!identity.Success) return null;
                string id = identity.Groups[1].Value;
                // The public web API is more reliable than api.zhihu.com for answer
                // pages.  It also returns the author/question identity when the
                // answer body is truncated for an anonymous visitor.
                string probeUrl = "https://www.zhihu.com/api/v4/answers/" + id +
                    "?include=content%2Cexcerpt%2Cauthor%2Cname%2Cquestion%2Ctitle";
                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126 Safari/537.36" },
                    { "Accept", "application/json" },
                    { "Referer", "https://www.zhihu.com/" }
                };
                ProbeResponse probe;
                await ZhihuProbeGate.WaitAsync(token);
                try
                {
                    int delayMilliseconds;
                    lock (ZhihuProbeTimingSync)
                    {
                        delayMilliseconds = Math.Max(0, 1200 - (int)(DateTime.UtcNow - _lastZhihuProbeStartedUtc).TotalMilliseconds);
                    }
                    if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, token);
                    lock (ZhihuProbeTimingSync) _lastZhihuProbeStartedUtc = DateTime.UtcNow;
                    // Try both the configured proxy and a direct route. Company
                    // proxies frequently return a synthetic 403 while the public
                    // answer API itself remains available.
                    probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && (probe.Status == 403 || probe.Status == 429 ||
                        (probe.Status == 404 && String.IsNullOrWhiteSpace(probe.Body))))
                    {
                        await Task.Delay(1800, token);
                        probe = await ReadProbeWithClientAsync(_zhihuClient, probeUrl, headers, token);
                        // A corporate proxy can consistently synthesize a 403
                        // for Zhihu while the same public API is reachable by a
                        // direct request. Do not leave a valid answer pending
                        // merely because the proxy path was challenged.
                        if (probe != null && (probe.Status == 403 || probe.Status == 429 ||
                            (probe.Status == 404 && String.IsNullOrWhiteSpace(probe.Body))))
                        {
                            ProbeResponse directProbe = await ReadProbeWithClientAsync(_directClient,
                                probeUrl, headers, token);
                            if (directProbe != null && directProbe.Status > 0 &&
                                (directProbe.Status == 200 || directProbe.Status == 404 || directProbe.Status == 410))
                                probe = directProbe;
                        }
                    }
                }
                finally { ZhihuProbeGate.Release(); }
                if (probe == null) return null;
                string body = probe.Body;
                if ((probe.Status == 404 || probe.Status == 410) && IsZhihuRemovedApiResponse(body))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                        "official-api", "知乎", id, "知乎公开回答接口确认目标回答不存在", probeUrl, true);
                // Zhihu's public API is frequently blocked with a ZSE/403 response. The
                // response itself is not deletion evidence, but the blocked API can still
                // expose a target-specific empty-state phrase. Only accept that phrase;
                // generic 403/security pages remain unresolved for browser review.
                if (probe.Status == 403 && IsZhihuRemovedApiResponse(body))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Strong,
                        "official-api", "知乎", id, "知乎受限公开响应仍明确显示目标回答不存在", probeUrl, true);
                // A Zhihu anti-bot response is not deletion evidence. Keep the item
                // unfinished so a later browser pass can use the answer-page redirect.
                if (probe.Status == 403) return null;
                if (probe.Status == 200)
                {
                    string decoded = WebUtility.HtmlDecode(DecodeJsonUnicode(body ?? ""));
                    string currentContent = ExtractJsonStringLong(body, "content", 12000);
                    bool idMatch = Regex.IsMatch(body ?? "", "\\\"id\\\"\\s*:\\s*\\\"" + Regex.Escape(id) + "\\\"");
                    bool titleMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, decoded);
                    bool authorMatch = String.IsNullOrWhiteSpace(expectedAuthor) || MatchesExpectedAuthor(expectedAuthor, decoded);
                    // A short answer can legitimately contain only a sentence or an
                    // image.  The target id, question title and author are still
                    // authoritative identity evidence when the API omits rich HTML.
                    bool questionMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt,
                        ExtractJsonStringLong(body, "title", 1000) + " " + decoded);
                    bool structuredAnswer = idMatch &&
                        body.IndexOf("\"question\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        body.IndexOf("\"author\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        !String.IsNullOrWhiteSpace(currentContent);
                    if (idMatch && ((titleMatch || questionMatch) && authorMatch || structuredAnswer) &&
                        (!String.IsNullOrWhiteSpace(currentContent) || !String.IsNullOrWhiteSpace(expectedAuthor)))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-api", "知乎", id, "知乎公开回答接口返回目标回答编号、问题标题、正文和发文作者", probeUrl, true);
                }
            }

            if (host.EndsWith("gu.qq.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.Fragment ?? "", @"(?:^|[?&])id=(SN[A-Za-z0-9]+)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string apiRoot = "https://proxy.finance.qq.com/ifzqgtimg/appstock/news/newsInfo/getNewsContent?id=";
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Referer", "https://gu.qq.com/" }
                    };
                    bool confirmedEmpty = true;
                    foreach (string candidateId in new[] { id, id + "00" }.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        string probeUrl = apiRoot + candidateId;
                        ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                        if (probe == null || probe.Status != 200 || !IsTencentStockNewsApiResponse(probe.Body))
                        {
                            confirmedEmpty = false;
                            continue;
                        }
                        string currentId = ExtractJsonString(probe.Body, "id");
                        string currentTitle = ExtractJsonString(probe.Body, "title");
                        int deleted = ExtractJsonInt(probe.Body, "is_deleted", -1);
                        int publishStatus = ExtractJsonInt(probe.Body, "publish_status", -1);
                        if (IsTencentStockNewsIdMatch(id, currentId) && deleted == 0 && publishStatus == 1 &&
                            MatchesExpectedTitle(expectedTitle, currentTitle))
                            return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                "official-api", "腾讯自选股", id, "腾讯自选股公开接口返回目标新闻编号、匹配标题和正常发布状态", probeUrl, true);
                        if (!IsTencentStockNewsEmpty(probe.Body)) confirmedEmpty = false;
                    }
                    if (confirmedEmpty)
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-api", "腾讯自选股", id, "腾讯自选股公开接口确认目标新闻编号无内容记录", apiRoot + id, true);
                }
            }

            if (host.EndsWith("chejiahao.autohome.com.cn", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/info/([0-9]+)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://chejiahao.m.autohome.com.cn/info/" + id;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Referer", "https://www.autohome.com.cn/" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && IsAutohomeArticleErrorRedirect(original, probe.FinalUrl, probe.Body))
                        return new PlatformProbeOutcome
                        {
                            Resolved = true,
                            Verdict = "已失效",
                            Evidence = "汽车之家车家号官方页将目标文章重定向到带 pc-error-no-hidden 标记的首页错误页",
                            FinalUrl = probe.FinalUrl
                        };
                    if (probe != null && (probe.Status == 404 || probe.Status == 410))
                        return new PlatformProbeOutcome { Resolved = true, Verdict = "已失效", Evidence = "汽车之家车家号官方移动页确认目标内容不存在", FinalUrl = probeUrl };
                    if (probe != null && probe.Status == 200)
                    {
                        string currentTitle = ExtractTitle(probe.Body);
                        bool titleMatch = MatchesExpectedTitle(expectedTitle, currentTitle);
                        bool authorMatch = MatchesExpectedAuthor(expectedAuthor, ExtractVisibleText(probe.Body));
                        bool idMatch = (probe.FinalUrl ?? probeUrl).IndexOf("/info/" + id, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (probe.Body ?? "").IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (idMatch && titleMatch)
                            return new PlatformProbeOutcome
                            {
                                Resolved = true,
                                Verdict = "仍可访问",
                                Evidence = "汽车之家车家号官方移动页返回目标内容编号和匹配标题" +
                                    (authorMatch && !String.IsNullOrWhiteSpace(expectedAuthor) ? "，作者“" + expectedAuthor.Trim() + "”" : ""),
                                FinalUrl = probe.FinalUrl
                            };
                    }
                }
            }

            if (host.EndsWith("yidianzixun.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/article/([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://www.yidianzixun.com/article/" + id;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && probe.Status == 200)
                    {
                        string visible = ExtractVisibleText(probe.Body);
                        bool idMatch = (probe.FinalUrl ?? probeUrl).IndexOf("/article/" + id, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (probe.Body ?? "").IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool contentMatch = MatchesExpectedContent(expectedTitle, "", visible);
                        bool authorMatch = MatchesExpectedAuthor(expectedAuthor, visible);
                        if (idMatch && contentMatch)
                            return new PlatformProbeOutcome
                            {
                                Resolved = true,
                                Verdict = "仍可访问",
                                Evidence = "一点资讯官方移动页返回目标内容编号和匹配的正文/视频摘要" +
                                    (authorMatch && !String.IsNullOrWhiteSpace(expectedAuthor) ? "，作者“" + expectedAuthor.Trim() + "”" : ""),
                                FinalUrl = probe.FinalUrl
                            };
                    }
                }
            }

            if (host.EndsWith("myzaker.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/article/([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://app.myzaker.com/news/article.php?pk=" + id;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && (probe.Status == 404 || probe.Status == 410 || IsZakerMissingPage(probe.FinalUrl)))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-page", "ZAKER", id, "ZAKER 官方内容页跳转到目标不存在页面", probe.FinalUrl, true);
                    if (probe != null && probe.Status == 200)
                    {
                        string currentTitle = ExtractTitle(probe.Body);
                        string visible = ExtractVisibleText(probe.Body);
                        bool idMatch = (probe.FinalUrl ?? probeUrl).IndexOf("pk=" + id, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool titleMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + visible);
                        bool authorMatch = MatchesExpectedAuthor(expectedAuthor, visible);
                        if (idMatch && titleMatch)
                            return new PlatformProbeOutcome
                            {
                                Resolved = true,
                                Verdict = "仍可访问",
                                Evidence = "ZAKER 官方内容页返回目标内容编号和匹配标题" +
                                    (authorMatch && !String.IsNullOrWhiteSpace(expectedAuthor) ? "，作者“" + expectedAuthor.Trim() + "”" : ""),
                                FinalUrl = probe.FinalUrl
                            };
                    }
                }
            }

            if (host.EndsWith("new.qq.com", StringComparison.Ordinal) || host.EndsWith("view.inews.qq.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/(?:rain/)?a/([0-9]{8}[A-Z0-9]+)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://view.inews.qq.com/a/" + id;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Referer", "https://new.qq.com/" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && (probe.Status == 404 || probe.Status == 410 ||
                        LooksLikeErrorPage(probe.FinalUrl, ExtractTitle(probe.Body), ExtractVisibleText(probe.Body))))
                        return new PlatformProbeOutcome { Resolved = true, Verdict = "已失效", Evidence = "腾讯新闻官方移动页确认目标内容进入错误页", FinalUrl = probe.FinalUrl };
                    if (probe != null && probe.Status == 200)
                    {
                        string currentTitle = ExtractTitle(probe.Body);
                        string visible = ExtractVisibleText(probe.Body);
                        string mainText = ExtractProbableMainContentText(probe.Body);
                        bool idMatch = (probe.FinalUrl ?? probeUrl).IndexOf("/a/" + id, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (probe.Body ?? "").IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool titleMatch = MatchesExpectedTitle(expectedTitle, currentTitle + " " + visible);
                        bool authorMatch = MatchesExpectedAuthor(expectedAuthor, mainText + " " + visible);
                        if (idMatch && (titleMatch || (authorMatch && mainText.Length >= 160)))
                            return new PlatformProbeOutcome
                            {
                                Resolved = true,
                                Verdict = "仍可访问",
                                Evidence = titleMatch
                                    ? "腾讯新闻官方移动页返回目标内容编号和匹配标题"
                                    : "腾讯新闻官方移动页返回目标内容编号、完整正文和发文作者“" + expectedAuthor.Trim() + "”，标题已编辑",
                                FinalUrl = probe.FinalUrl
                            };
                    }
                }
            }

            if (host.EndsWith("sohu.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/a/([0-9]+_[0-9]+)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://m.sohu.com/a/" + id;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && (probe.Status == 404 || probe.Status == 410 ||
                        Regex.IsMatch(probe.FinalUrl ?? "", @"/404(?:\.html)?(?:[?#]|$)", RegexOptions.IgnoreCase)))
                        return new PlatformProbeOutcome
                        {
                            Resolved = true,
                            Verdict = "已失效",
                            Evidence = "搜狐官方正文页确认目标文章已跳转到 404 不存在页面",
                            FinalUrl = probe.FinalUrl
                        };
                    if (probe != null && probe.Status == 200)
                    {
                        string currentTitle = ExtractTitle(probe.Body);
                        string visible = ExtractVisibleText(probe.Body);
                        bool idMatch = (probe.FinalUrl ?? probeUrl).IndexOf("/a/" + id, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool titleMatch = MatchesExpectedTitle(expectedTitle, currentTitle + " " + visible);
                        bool authorMatch = MatchesExpectedAuthor(expectedAuthor, visible);
                        if (idMatch && titleMatch)
                            return new PlatformProbeOutcome
                            {
                                Resolved = true,
                                Verdict = "仍可访问",
                                Evidence = "搜狐官方移动正文页返回目标内容编号和匹配标题" +
                                    (authorMatch && !String.IsNullOrWhiteSpace(expectedAuthor) ? "，作者“" + expectedAuthor.Trim() + "”" : ""),
                                FinalUrl = probe.FinalUrl
                            };
                    }
                }
            }

            if (host.EndsWith("v.qq.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/x/page/([A-Za-z0-9]+)\.html", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://vv.video.qq.com/getinfo?vids=" + id + "&platform=101001&charge=0&otype=json";
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126 Safari/537.36" },
                        { "Referer", "https://v.qq.com/x/page/" + id + ".html" }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && probe.Status == 200)
                    {
                        string body = probe.Body ?? "";
                        string currentId = ExtractJsonString(body, "vid");
                        string currentTitle = ExtractJsonString(body, "ti");
                        if (String.Equals(currentId, id, StringComparison.OrdinalIgnoreCase) &&
                            ExtractJsonInt(body, "em", -1) == 0 && MatchesExpectedTitle(expectedTitle, currentTitle))
                            return new PlatformProbeOutcome
                            {
                                Resolved = true,
                                Verdict = "仍可访问",
                                Evidence = "腾讯视频官方信息接口返回目标视频编号和匹配视频名称",
                                FinalUrl = probeUrl
                            };
                        if (IsTencentVideoUnavailableResponse(body, id))
                            return new PlatformProbeOutcome
                            {
                                Resolved = true,
                                Verdict = "已失效",
                                Evidence = "腾讯视频官方信息接口返回目标视频不可观看（em=80），当前公开地址已失效",
                                FinalUrl = probeUrl
                            };
                    }
                }
            }

            if (host.EndsWith("kandianshare.html5.qq.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/v2/news/([0-9]+)", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" }
                    };
                    string shareUrl = "https://newsa.html5.qq.com/v1/share-article?docId=" + id;
                    ProbeResponse shareProbe = await TryReadProbeAsync(shareUrl, headers, token);
                    if (shareProbe != null && shareProbe.Status == 200)
                    {
                        string shareTitle = ExtractTitle(shareProbe.Body);
                        string shareVisible = ExtractVisibleText(shareProbe.Body);
                        bool titleMatch = MatchesExpectedTitle(expectedTitle, shareTitle + " " + shareVisible);
                        bool authorMatch = MatchesExpectedAuthor(expectedAuthor, shareVisible + " " + (shareProbe.Body ?? ""));
                        if (titleMatch || (authorMatch && shareVisible.Length >= 300))
                            return new PlatformProbeOutcome
                            {
                                Resolved = true,
                                Verdict = "仍可访问",
                                Evidence = titleMatch
                                    ? "腾讯新闻官方分享页返回目标内容编号和匹配正文"
                                    : "腾讯新闻官方分享页返回目标内容编号、完整正文和作者“" + expectedAuthor.Trim() + "”，采集标题为正文首句",
                                FinalUrl = shareProbe.FinalUrl
                            };
                    }
                    string probeUrl = "https://view.inews.qq.com/k/" + id + "?scene=wap&no-redirect=1";
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (probe != null && (probe.Status == 404 || probe.Status == 410 ||
                        LooksLikeErrorPage(probe.FinalUrl, ExtractTitle(probe.Body), ExtractVisibleText(probe.Body))))
                        return new PlatformProbeOutcome
                        {
                            Resolved = true,
                            Verdict = "已失效",
                            Evidence = "腾讯新闻官方移动页确认旧分享内容进入错误页",
                            FinalUrl = probe.FinalUrl
                        };
                }
            }

            if (host.EndsWith("163.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/v/video/([A-Za-z0-9]+)\.html", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string detailUrl = "https://c.m.163.com/nc/video/detail/" + id + ".html";
                    string pageUrl = "https://www.163.com/v/video/" + id + ".html";
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" }
                    };
                    ProbeResponse detailProbe = await TryReadProbeAsync(detailUrl, headers, token);
                    ProbeResponse pageProbe = await TryReadProbeAsync(pageUrl, headers, token);
                    string detailBody = detailProbe == null ? "" : (detailProbe.Body ?? "");
                    string currentTitle = ExtractJsonString(detailBody, "title");
                    if (detailProbe != null && detailProbe.Status == 200 &&
                        detailBody.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0 && MatchesExpectedTitle(expectedTitle, currentTitle))
                        return new PlatformProbeOutcome
                        {
                            Resolved = true,
                            Verdict = "仍可访问",
                            Evidence = "网易官方视频详情接口返回目标视频编号和匹配标题",
                            FinalUrl = detailUrl
                        };
                    if (detailProbe != null && detailProbe.Status == 200 && detailBody.Trim() == "{}" &&
                        pageProbe != null && (pageProbe.Status == 404 || pageProbe.Status == 410))
                        return new PlatformProbeOutcome
                        {
                            Resolved = true,
                            Verdict = "已失效",
                            Evidence = "网易官方视频页返回 HTTP 404，且视频详情接口确认目标编号无记录",
                            FinalUrl = pageUrl
                        };
                }
            }

            string ucArticleId = ExtractUcArticleId(original);
            if ((host.EndsWith("uczzd.cn", StringComparison.Ordinal) || host == "a.mp.uc.cn" || host == "mparticle.uc.cn") &&
                !String.IsNullOrWhiteSpace(ucArticleId))
            {
                string id = ucArticleId;
                string probeUrl = "https://m.uczzd.cn/ucnews/news?aid=" + id;
                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                    { "Accept-Language", "zh-CN,zh;q=0.9" }
                };
                ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                if (probe != null && probe.Status == 200)
                {
                    string currentTitle = ExtractTitle(probe.Body);
                    string visible = ExtractVisibleText(probe.Body);
                    bool idMatch = (probe.Body ?? "").IndexOf("\"id\":\"" + id + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool contentMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + visible);
                    bool authorMatch = MatchesExpectedAuthor(expectedAuthor, visible + " " + (probe.Body ?? ""));
                    if (idMatch && contentMatch && (String.IsNullOrWhiteSpace(expectedAuthor) || authorMatch))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-mobile-page", "UC/大鱼号", id, "UC/大鱼号官方移动页返回目标内容编号、匹配正文" +
                                (authorMatch ? "和作者“" + expectedAuthor.Trim() + "”" : ""), probe.FinalUrl, true);
                    if (IsUcMissingArticlePage(probe.Body, probe.FinalUrl, id))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-mobile-page", "UC/大鱼号", id, "UC/大鱼号官方移动页明确提示目标文章不存在", probe.FinalUrl, true);
                }
            }

            Match eastmoneyArticle = Regex.Match(original.AbsolutePath ?? "", @"/a/([0-9]+)\.html", RegexOptions.IgnoreCase);
            Match fundArticle = Regex.Match(original.Query ?? "", @"(?:^|[?&])code=([0-9]+)", RegexOptions.IgnoreCase);
            if ((host.EndsWith("eastmoney.com", StringComparison.Ordinal) && eastmoneyArticle.Success) ||
                (host.EndsWith("1234567.com.cn", StringComparison.Ordinal) && fundArticle.Success))
            {
                string id = eastmoneyArticle.Success ? eastmoneyArticle.Groups[1].Value : fundArticle.Groups[1].Value;
                string probeUrl = "https://emwap.eastmoney.com/a/" + id + ".html";
                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                    { "Accept-Language", "zh-CN,zh;q=0.9" }
                };
                ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                if (probe != null && (probe.Status == 404 || probe.Status == 410 ||
                    LooksLikeErrorPage(probe.FinalUrl, ExtractTitle(probe.Body), ExtractVisibleText(probe.Body))))
                    return new PlatformProbeOutcome { Resolved = true, Verdict = "已失效", Evidence = "东方财富官方移动正文页确认目标内容不存在", FinalUrl = probe.FinalUrl };
                if (probe != null && probe.Status == 200)
                {
                    string currentTitle = ExtractTitle(probe.Body);
                    string visible = ExtractVisibleText(probe.Body);
                    bool idMatch = (probe.FinalUrl ?? probeUrl).IndexOf("/a/" + id + ".html", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (probe.Body ?? "").IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool titleMatch = MatchesExpectedTitle(expectedTitle, currentTitle + " " + visible);
                    bool authorMatch = MatchesExpectedAuthor(expectedAuthor, visible + " " + (probe.Body ?? ""));
                    if (idMatch && (titleMatch || (authorMatch && visible.Length >= 300)))
                        return new PlatformProbeOutcome
                        {
                            Resolved = true,
                            Verdict = "仍可访问",
                            Evidence = titleMatch
                                ? "东方财富官方移动正文页返回目标内容编号和匹配标题"
                                : "东方财富官方移动正文页返回目标内容编号、完整正文和作者“" + expectedAuthor.Trim() + "”，采集标题为正文首句",
                            FinalUrl = probe.FinalUrl
                        };
                }
            }

            string baiduVideoId = ExtractBaiduVideoId(original);
            if (!String.IsNullOrEmpty(baiduVideoId) && !host.EndsWith("haokan.baidu.com", StringComparison.Ordinal))
            {
                string probeUrl = "https://haokan.baidu.com/v?vid=" + baiduVideoId;
                ProbeResponse probe = await TryReadProbeAsync(probeUrl, null, token);
                if (probe != null && probe.Status == 200 && IsHaokanErrorResponse(probe.Body, baiduVideoId))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                        "official-video-page", "好看视频", baiduVideoId, "百度系视频共享内容页确认目标视频编号已进入专用错误页", probeUrl, true);
                // The current Haokan page can render a shared-video shell while the
                // video metadata is still present in the embedded JSON. Treat a
                // matching target ID plus a non-empty title/description as accessible;
                // only the dedicated error bundle is a removal signal.
                if (probe != null && probe.Status == 200 &&
                    (probe.Body ?? "").IndexOf(baiduVideoId, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !IsHaokanErrorResponse(probe.Body, baiduVideoId) &&
                    HasBaiduVideoIdentity(probe.Body, baiduVideoId, expectedTitle))
                {
                    string probeTitle = ExtractTitle(probe.Body);
                    string probeText = ExtractVisibleText(probe.Body);
                    if (MatchesExpectedTitle(expectedTitle, probeTitle + " " + probeText) ||
                        Regex.IsMatch(probe.Body ?? "", "(?:videoId|vid)\\D{0,12}" + Regex.Escape(baiduVideoId), RegexOptions.IgnoreCase))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                            "official-video-page", "好看视频", baiduVideoId, "百度系视频共享页返回目标视频编号和可用页面数据", probe.FinalUrl, true);
                }
            }

            string baiduArticleId = ExtractBaiduArticleId(original);
            if (!String.IsNullOrEmpty(baiduArticleId))
            {
                string articleNid = ExtractBaiduArticleNid(original);
                string probeUrl = articleNid.StartsWith("dt_", StringComparison.OrdinalIgnoreCase)
                    ? "https://mbd.baidu.com/newspage/data/dtlandingwise?nid=" + articleNid
                    : "https://mbd.baidu.com/newspage/data/landingreact?nid=" + articleNid;
                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                    { "Accept-Language", "zh-CN,zh;q=0.9" }
                };
                ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                if (probe != null && probe.Status == 200)
                {
                    string probeTitle = ExtractTitle(probe.Body);
                    bool errorPage = (probe.FinalUrl ?? "").IndexOf("/newspage/data/error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ((probe.Body ?? "").IndexOf(baiduArticleId, StringComparison.OrdinalIgnoreCase) < 0 &&
                         (probe.Body ?? "").IndexOf("这里空空如也", StringComparison.Ordinal) >= 0);
                    if (errorPage)
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-share-page", "百度系图文", baiduArticleId, "百度系图文共享内容页确认目标内容编号已进入专用错误页", probe.FinalUrl, true);
                    if ((probe.Body ?? "").IndexOf(baiduArticleId, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (MatchesExpectedTitle(expectedTitle, probeTitle + " " + ExtractVisibleText(probe.Body)) || String.IsNullOrWhiteSpace(expectedTitle)))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                            "official-share-page", "百度系图文", baiduArticleId, "百度系官方共享图文页返回目标内容编号和匹配标题", probe.FinalUrl, true);
                }
            }

            // Yoojia/Baidu shared pages often keep only a JS shell in the first
            // response. The public landing page is still authoritative when it
            // explicitly says the target article was deleted.
            if (host.EndsWith("yoojia.baidu.com", StringComparison.Ordinal) ||
                host.EndsWith("yoojia.com", StringComparison.Ordinal))
            {
                string sharedId = ExtractBaiduArticleId(original);
                if (!String.IsNullOrEmpty(sharedId))
                {
                    string sharedUrl = "https://mbd.baidu.com/newspage/data/landingreact?nid=" + ExtractBaiduArticleNid(original);
                    var sharedHeaders = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                        { "Accept-Language", "zh-CN,zh;q=0.9" }
                    };
                    ProbeResponse shared = await TryReadProbeAsync(sharedUrl, sharedHeaders, token);
                    string sharedText = ExtractVisibleText(shared == null ? "" : shared.Body);
                    if (shared != null && shared.Status == 200 &&
                        ((shared.Body ?? "").IndexOf("这里空空如也", StringComparison.Ordinal) >= 0 ||
                         Regex.IsMatch(sharedText, "该文章(?:已|已经)删除|内容不存在", RegexOptions.IgnoreCase)))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-share-page", "有驾", sharedId, "百度/有驾官方共享页明确提示目标内容已删除或不存在", shared.FinalUrl, true);
                }
            }
            return null;
        }

        // Zhihu often returns a security JSON body with HTTP 403. Only a target
        // removal response is strong evidence; generic "resource not found"
        // text can be emitted by the anti-bot redirect and must stay reviewable.
        private static bool IsZhihuRemovedApiResponse(string body)
        {
            string source = body ?? "";
            if (String.IsNullOrWhiteSpace(source)) return false;
            if (Regex.IsMatch(source, "没有知识存在的荒原|该回答不存在|回答不存在", RegexOptions.IgnoreCase)) return true;
            if (!Regex.IsMatch(source, "资源不存在", RegexOptions.IgnoreCase)) return false;
            return !Regex.IsMatch(source, "need_login|unhuman|安全验证|访问异常|验证码|captcha|security", RegexOptions.IgnoreCase);
        }

        internal static bool TryExtractYicheThreadIdentity(Uri uri, out string forum, out string threadId)
        {
            forum = "";
            threadId = "";
            if (uri == null) return false;
            string host = uri.Host.ToLowerInvariant();
            if (!(host.EndsWith("yiche.com", StringComparison.Ordinal) || host.EndsWith("bitauto.com", StringComparison.Ordinal))) return false;
            Match match = Regex.Match(uri.AbsolutePath ?? "", @"/([^/]+)/thread-([0-9]+)\.html", RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            forum = match.Groups[1].Value;
            threadId = match.Groups[2].Value;
            return forum.Length > 0 && threadId.Length > 0;
        }

        internal static string ExtractXimalayaTrackId(Uri uri)
        {
            if (uri == null || !uri.Host.EndsWith("ximalaya.com", StringComparison.OrdinalIgnoreCase)) return "";
            Match match = Regex.Match(uri.AbsolutePath ?? "", @"/(?:sound|track)/([0-9]+)(?:/|$)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value;
            match = Regex.Match(uri.Query ?? "", @"(?:^|[?&])trackId=([0-9]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "";
        }

        internal static bool IsXimalayaMissingResponse(string body, string trackId)
        {
            string source = body ?? "";
            if (ExtractJsonInt(source, "ret", Int32.MinValue) != 404) return false;
            string message = ExtractJsonString(source, "msg");
            if (String.IsNullOrWhiteSpace(message) || String.IsNullOrWhiteSpace(trackId)) return false;
            return message.IndexOf(trackId, StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(message, "已下架|不存在|已删除", RegexOptions.IgnoreCase);
        }

        internal static bool IsZakerMissingPage(string finalUrl)
        {
            Uri final;
            if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out final) || !final.Host.EndsWith("myzaker.com", StringComparison.OrdinalIgnoreCase)) return false;
            return Regex.IsMatch(final.AbsolutePath ?? "", @"/news/404\.php$", RegexOptions.IgnoreCase);
        }

        internal static string ExtractUcArticleId(Uri uri)
        {
            if (uri == null) return "";
            string host = uri.Host.ToLowerInvariant();
            if (!(host.EndsWith("uczzd.cn", StringComparison.Ordinal) || host == "a.mp.uc.cn" || host == "mparticle.uc.cn")) return "";
            string source = (uri.Query ?? "") + "&" + (uri.Fragment ?? "");
            Match match = Regex.Match(source, @"(?:^|[?&!#])(?:aid|wm_aid|sm_article_id|xss_item_id|video_id)=([0-9]{8,})", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "";
        }

        internal static bool IsCurrentToutiaoContentSource(string groupSource)
        {
            // group_source=148 is a cached question card: its API record can remain
            // after the public article route has become a 404, so it is excluded.
            return new[] { "2", "5", "15", "19", "20", "21", "661" }.Contains(groupSource ?? "", StringComparer.Ordinal);
        }

        internal static string ExtractToutiaoAuthor(string json)
        {
            string source = ExtractJsonString(json, "name");
            if (!String.IsNullOrWhiteSpace(source)) return source;
            source = ExtractJsonString(json, "source");
            return source;
        }

        internal static bool TryMatchKuaishouSsrContent(string html, string shortId, string expectedTitle,
            string expectedAuthor, out string currentCaption, out string currentAuthor)
        {
            currentCaption = "";
            currentAuthor = "";
            if (String.IsNullOrWhiteSpace(html) || String.IsNullOrWhiteSpace(shortId)) return false;

            Match identity = Regex.Match(html,
                "\\\"share_info\\\"\\s*:\\s*\\\"[^\\\"]*photoId=" + Regex.Escape(shortId) + "(?:&|\\\")",
                RegexOptions.IgnoreCase);
            if (!identity.Success) return false;

            int start = Math.Max(0, identity.Index - 24000);
            int length = Math.Min(html.Length - start, identity.Index - start + identity.Length + 1200);
            string targetBlock = html.Substring(start, length);
            string statusBlock = targetBlock.Substring(Math.Max(0, identity.Index - start));
            if (!Regex.IsMatch(statusBlock, "\\\"photoStatus\\\"\\s*:\\s*0(?:[^0-9]|$)", RegexOptions.IgnoreCase)) return false;

            MatchCollection captions = Regex.Matches(targetBlock,
                "\\\"caption\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase);
            MatchCollection authors = Regex.Matches(targetBlock,
                "\\\"userName\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase);
            if (captions.Count == 0 || authors.Count == 0) return false;

            currentCaption = CleanText(WebUtility.HtmlDecode(DecodeJsonUnicode(Regex.Unescape(captions[captions.Count - 1].Groups[1].Value))), 1000);
            currentAuthor = CleanText(WebUtility.HtmlDecode(DecodeJsonUnicode(Regex.Unescape(authors[authors.Count - 1].Groups[1].Value))), 180);
            if (String.IsNullOrWhiteSpace(currentCaption)) return false;

            string expectedTrimmed = (expectedTitle ?? "").Trim();
            bool placeholderTitle = Regex.IsMatch(expectedTrimmed, @"^[\.\u2026。]+$");
            bool captionMatch = placeholderTitle
                ? String.Equals(expectedTrimmed, currentCaption.Trim(), StringComparison.Ordinal)
                : MatchesExpectedTitle(expectedTitle, currentCaption);
            if (!captionMatch) return false;

            if (String.IsNullOrWhiteSpace(expectedAuthor)) return !placeholderTitle;
            string expectedAuthorKey = NormalizeForMatch(expectedAuthor);
            string currentAuthorKey = NormalizeForMatch(currentAuthor);
            bool authorMatch = expectedAuthorKey.Length >= 3 && currentAuthorKey.Length >= 3 &&
                (expectedAuthorKey.Contains(currentAuthorKey) || currentAuthorKey.Contains(expectedAuthorKey));
            return authorMatch;
        }

        internal static bool IsKuaishouRemovedSsrContent(string html, string shortId)
        {
            string source = html ?? "";
            if (source.Length == 0) return false;
            // The mobile SSR endpoint emits this target-page-specific error
            // when the requested work is gone or has never been published.
            // Require the endpoint's exact phrase; generic JS/runtime errors
            // must remain unresolved.
            return Regex.IsMatch(source,
                "获取失败\\s*[，,]?\\s*作品可能已被删除或尚未上传",
                RegexOptions.IgnoreCase);
        }

        internal static bool IsWeiboPresentResponse(string body, string expectedMblogId)
        {
            string source = body ?? "";
            if (!Regex.IsMatch(source, "\\\"ok\\\"\\s*:\\s*1", RegexOptions.IgnoreCase)) return false;
            string currentId = ExtractJsonString(source, "mblogid");
            return !String.IsNullOrWhiteSpace(expectedMblogId) && String.Equals(currentId, expectedMblogId, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsWeiboUnavailableResponse(string body, out string reason)
        {
            reason = "";
            string source = body ?? "";
            Match error = Regex.Match(source, "\\\"error_code\\\"\\s*:\\s*(20101|20112)", RegexOptions.IgnoreCase);
            if (!error.Success || !Regex.IsMatch(source, "\\\"ok\\\"\\s*:\\s*0", RegexOptions.IgnoreCase)) return false;
            reason = error.Groups[1].Value == "20112" ? "当前已被作者隐藏或无公开查看权限" : "当前已不存在";
            return true;
        }

        internal static bool IsTonghuashunRemovedResponse(string body)
        {
            return ExtractJsonInt(body, "status_code", Int32.MinValue) == -2 &&
                Regex.IsMatch(ExtractJsonString(body, "status_msg"), "帖子已被删除|帖子不存在", RegexOptions.IgnoreCase);
        }

        internal static bool TryMatchTonghuashunPost(string body, string contentId, string expectedTitle,
            string expectedExcerpt, out string currentAuthor)
        {
            currentAuthor = ExtractJsonString(body, "nickname");
            string currentId = ExtractJsonString(body, "content_id");
            string currentContent = ExtractJsonStringLong(body, "content", 12000);
            return ExtractJsonInt(body, "status_code", Int32.MinValue) == 0 &&
                ExtractJsonInt(body, "valid", Int32.MinValue) == 1 &&
                String.Equals(currentId, contentId, StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrWhiteSpace(currentContent) &&
                MatchesExpectedContent(expectedTitle, expectedExcerpt, currentContent);
        }

        internal static bool IsEastmoneyFortuneRemovedPage(string visibleText)
        {
            return Regex.IsMatch(visibleText ?? "", "抱歉[，,]?该文章已被删除", RegexOptions.IgnoreCase);
        }

        private async Task<PlatformProbeOutcome> ProbeWeiboStatusAsync(string id, string expectedTitle,
            string expectedExcerpt, string expectedAuthor, CancellationToken token)
        {
            PlatformProbeOutcome apiOutcome = null;
            await WeiboProbeGate.WaitAsync(token);
            try
            {
                string visitorCookie = await GetWeiboVisitorCookieAsync(token);
                if (!String.IsNullOrWhiteSpace(visitorCookie))
                {
                    string probeUrl = "https://weibo.com/ajax/statuses/show?id=" + id + "&locale=zh-CN";
                    var headers = new Dictionary<string, string>
                    {
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126 Safari/537.36" },
                        { "Referer", "https://weibo.com/" },
                        { "X-Requested-With", "XMLHttpRequest" },
                        { "Cookie", visitorCookie }
                    };
                    ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
                    if (IsWeiboRetryableVisitorResponse(probe == null ? "" : probe.Body))
                    {
                        InvalidateWeiboVisitorCookie();
                        await Task.Delay(350, token);
                        visitorCookie = await GetWeiboVisitorCookieAsync(token);
                        if (!String.IsNullOrWhiteSpace(visitorCookie))
                        {
                            headers["Cookie"] = visitorCookie;
                            probe = await TryReadProbeAsync(probeUrl, headers, token);
                        }
                    }
                    if (probe != null && probe.Status == 200)
                    {
                        string body = probe.Body ?? "";
                        string unavailableReason;
                        if (IsWeiboUnavailableResponse(body, out unavailableReason))
                            apiOutcome = ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                                "official-api", "微博", id, "微博官方单条接口确认目标微博" + unavailableReason, probeUrl, true);
                        else
                        {
                            string currentText = ExtractJsonStringLong(body, "text_raw", 12000);
                            if (String.IsNullOrWhiteSpace(currentText)) currentText = ExtractJsonStringLong(body, "text", 12000);
                            bool contentMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, currentText + " " + body);
                            bool authorMatch = MatchesExpectedAuthor(expectedAuthor, body);
                            if (IsWeiboPresentResponse(body, id) && !String.IsNullOrWhiteSpace(currentText) && contentMatch &&
                                (String.IsNullOrWhiteSpace(expectedAuthor) || authorMatch))
                                apiOutcome = ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                                    "official-api", "微博", id, "微博官方单条接口返回目标微博编号、匹配正文" +
                                        (authorMatch ? "和作者“" + expectedAuthor.Trim() + "”" : ""), probeUrl, true);
                        }
                    }
                }
            }
            finally { WeiboProbeGate.Release(); }
            if (apiOutcome != null) return apiOutcome;
            return await ProbeRenderedSocialPostAsync("https://m.weibo.cn/detail/" + id,
                "微博", id, expectedTitle, expectedExcerpt, expectedAuthor, token);
        }

        private async Task<PlatformProbeOutcome> ProbeRenderedSocialPostAsync(string probeUrl, string platform,
            string id, string expectedTitle, string expectedExcerpt, string expectedAuthor, CancellationToken token)
        {
            // Fast audit is intentionally transport/API-only. Rendered browser
            // evidence belongs to the explicit deep-review action.
            if (String.Equals(Environment.GetEnvironmentVariable("LINK_CHECKER_QUICK_PASS"), "1",
                StringComparison.OrdinalIgnoreCase)) return null;
            await RenderedSocialProbeGate.WaitAsync(token);
            try
            {
                BrowserSnapshot snapshot = await RenderWithBrowserAsync(probeUrl, token);
                if (snapshot == null || String.IsNullOrWhiteSpace(snapshot.Html)) return null;
                string html = snapshot.Html ?? "";
                string title = ExtractTitle(html);
                string visible = ExtractVisibleText(html);
                string mainText = ExtractProbableMainContentText(html);
                string combined = title + " " + mainText + " " + visible;
                bool targetIdentity = !String.IsNullOrWhiteSpace(id) &&
                    html.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;

                if (platform == "雪球" && IsXueqiuRenderedRemoval(html, id))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                        "official-rendered-page", platform, id, "雪球目标页明确提示原帖已被作者删除", probeUrl, true);
                if (platform == "微博" && IsWeiboRenderedUnavailable(html, id))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                        "official-rendered-page", platform, id, "微博目标页明确提示该博文已删除、隐藏或暂时无法公开传播", probeUrl, true);
                if (platform == "百度贴吧" && IsTiebaRenderedRemoval(html, id))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                        "official-rendered-page", platform, id, "百度贴吧目标页明确提示帖子可能已被删除", probeUrl, true);
                if (platform == "B站" && IsBilibiliRenderedUnavailable(html, id))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                        "official-rendered-page", platform, id, "B站动态官方页明确提示目标动态不存在或不可见", probeUrl, true);

                bool contentMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, combined);
                bool authorMatch = MatchesExpectedAuthor(expectedAuthor, combined);
                bool structuredBody = HasArticleBodyStructure(html, mainText) ||
                    Regex.IsMatch(html, "<(?:article|main)[^>]*>", RegexOptions.IgnoreCase);
                if (targetIdentity && contentMatch && structuredBody)
                    return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                        "official-rendered-page", platform, id, platform + "目标页返回原内容编号、匹配正文" +
                            (authorMatch ? "和作者“" + expectedAuthor.Trim() + "”" : ""), probeUrl, true);
                return null;
            }
            finally { RenderedSocialProbeGate.Release(); }
        }

        internal static bool IsXueqiuRenderedRemoval(string html, string id)
        {
            string source = html ?? "";
            if (Regex.IsMatch(source, "当前内容不适合展示[，,]?无法查看", RegexOptions.IgnoreCase)) return true;
            return !String.IsNullOrWhiteSpace(id) && source.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(source, "原帖已被作者删除|该帖已被作者删除", RegexOptions.IgnoreCase);
        }

        internal static bool IsWeiboRenderedUnavailable(string html, string id)
        {
            string source = html ?? "";
            return !String.IsNullOrWhiteSpace(id) && source.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(source,
                    "微博不存在或暂无查看权限|抱歉[，,]?此微博已被删除|博文涉及营销推广正在审核中[，,]?暂时无法传播|该博文仅自己可见|该微博已被屏蔽",
                    RegexOptions.IgnoreCase);
        }

        internal static bool IsTiebaRenderedRemoval(string html, string id)
        {
            string source = html ?? "";
            return !String.IsNullOrWhiteSpace(id) && source.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(source, "贴子可能已被删除|帖子可能已被删除|该贴子不存在|该帖子不存在", RegexOptions.IgnoreCase);
        }

        internal static bool IsBilibiliRenderedUnavailable(string html, string id)
        {
            string source = html ?? "";
            return !String.IsNullOrWhiteSpace(id) && source.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(source, "动态不存在|动态已被删除|该动态不存在|内容不存在|" +
                    "该内容已被删除|该内容不可见", RegexOptions.IgnoreCase);
        }

        internal static bool TryMatchBilibiliOpusPage(string html, string id, string expectedTitle,
            string expectedExcerpt, string expectedAuthor)
        {
            string source = html ?? "";
            if (String.IsNullOrWhiteSpace(id) || source.IndexOf(id, StringComparison.OrdinalIgnoreCase) < 0) return false;
            string combined = ExtractTitle(source) + " " + ExtractProbableMainContentText(source) + " " + ExtractVisibleText(source);
            return MatchesExpectedContent(expectedTitle, expectedExcerpt, combined) &&
                (String.IsNullOrWhiteSpace(expectedAuthor) || MatchesExpectedAuthor(expectedAuthor, combined));
        }

        internal static bool TryMatchXueqiuStatus(string body, string id, string expectedTitle,
            string expectedExcerpt, string expectedAuthor)
        {
            string source = body ?? "";
            if (String.IsNullOrWhiteSpace(id) || source.Length == 0) return false;
            string currentId = ExtractJsonString(source, "id");
            if (!String.Equals(currentId, id, StringComparison.OrdinalIgnoreCase)) return false;
            string text = ExtractJsonStringLong(source, "text", 20000);
            if (String.IsNullOrWhiteSpace(text)) text = ExtractJsonStringLong(source, "description", 20000);
            string user = ExtractJsonStringLong(source, "screen_name", 300);
            if (String.IsNullOrWhiteSpace(user)) user = ExtractJsonStringLong(source, "user_name", 300);
            bool contentMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, text + " " + source);
            bool authorMatch = String.IsNullOrWhiteSpace(expectedAuthor) ||
                MatchesExpectedAuthor(expectedAuthor, user + " " + source);
            return !String.IsNullOrWhiteSpace(text) && contentMatch && authorMatch;
        }

        internal static bool IsXueqiuStatusRemoved(string body, string id)
        {
            string source = body ?? "";
            if (String.IsNullOrWhiteSpace(id)) return false;
            return Regex.IsMatch(source, "不存在|已删除|已下架|not found|deleted", RegexOptions.IgnoreCase) &&
                (source.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 Regex.IsMatch(source, "error|status_code|result", RegexOptions.IgnoreCase));
        }

        internal static bool IsTencentVideoUnavailableResponse(string body, string id)
        {
            string source = body ?? "";
            if (String.IsNullOrWhiteSpace(source) || String.IsNullOrWhiteSpace(id)) return false;
            string currentId = ExtractJsonString(source, "vid");
            int errorMode = ExtractJsonInt(source, "em", Int32.MinValue);
            string message = ExtractJsonString(source, "msg");
            if (!String.IsNullOrWhiteSpace(currentId) &&
                !String.Equals(currentId, id, StringComparison.OrdinalIgnoreCase)) return false;
            return errorMode == 80 &&
                Regex.IsMatch(message + " " + source, "该内容暂时不支持观看|暂无法观看|内容已下架|视频已下架",
                    RegexOptions.IgnoreCase);
        }

        internal static bool IsAutohomeArticleErrorRedirect(Uri requested, string finalUrl, string body)
        {
            if (requested == null || !requested.Host.EndsWith("chejiahao.autohome.com.cn", StringComparison.OrdinalIgnoreCase))
                return false;
            Match id = Regex.Match(requested.AbsolutePath ?? "", @"/info/([0-9]+)", RegexOptions.IgnoreCase);
            if (!id.Success) return false;
            Uri final;
            if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out final) ||
                !final.Host.EndsWith("autohome.com.cn", StringComparison.OrdinalIgnoreCase)) return false;
            string source = body ?? "";
            bool sameArticle = (final.AbsoluteUri ?? "").IndexOf("/info/" + id.Groups[1].Value,
                StringComparison.OrdinalIgnoreCase) >= 0;
            bool homeRedirect = String.IsNullOrWhiteSpace(final.AbsolutePath.Trim('/')) ||
                final.AbsolutePath.Trim('/').Equals("index.html", StringComparison.OrdinalIgnoreCase);
            string finalSource = final.AbsoluteUri ?? "";
            try { finalSource = WebUtility.UrlDecode(WebUtility.UrlDecode(finalSource)); }
            catch { }
            bool marker = source.IndexOf("pc-error-no-hidden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                finalSource.IndexOf("pc-error-no-hidden", StringComparison.OrdinalIgnoreCase) >= 0;
            return !sameArticle && homeRedirect && marker;
        }

        internal static bool TryMatchBilibiliArticleInfo(string json, string id, string expectedTitle,
            string expectedExcerpt, string expectedAuthor, out bool removed)
        {
            removed = false;
            string source = json ?? "";
            if (String.IsNullOrWhiteSpace(id) || source.Length == 0) return false;
            int code = ExtractJsonInt(source, "code", Int32.MinValue);
            if (code == -404 || code == -40401) { removed = true; return false; }
            // The official viewinfo response binds the request id in the URL,
            // but many valid responses do not echo that numeric id in JSON.
            // Requiring the id in the body caused real B站 columns to remain
            // unfinished even when title and author were returned authoritatively.
            if (code != 0 || !Regex.IsMatch(source, "\\\"data\\\"\\s*:\\s*\\{", RegexOptions.IgnoreCase)) return false;
            string currentTitle = ExtractJsonStringLong(source, "title", 1000);
            string currentAuthor = ExtractJsonString(source, "author_name");
            bool titleMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + source);
            bool authorMatch = String.IsNullOrWhiteSpace(expectedAuthor) ||
                MatchesExpectedAuthor(expectedAuthor, currentAuthor + " " + source);
            // The API binds the numeric article id to its current title/author.
            // Supplier titles are sometimes a first sentence or stale headline,
            // so a non-empty authoritative title plus the expected author is enough
            // to confirm availability when the id is exact.
            return authorMatch && (!String.IsNullOrWhiteSpace(currentTitle)) &&
                (titleMatch || !String.IsNullOrWhiteSpace(expectedAuthor));
        }

        internal static bool TryMatchBilibiliDynamicInfo(string json, string id,
            string expectedTitle, string expectedExcerpt, string expectedAuthor)
        {
            string source = json ?? "";
            if (String.IsNullOrWhiteSpace(id) || source.Length == 0 ||
                ExtractJsonInt(source, "code", Int32.MinValue) != 0) return false;
            bool idMatch = source.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
            string visible = ExtractVisibleText(source);
            string title = ExtractJsonStringLong(source, "title", 1200);
            string content = ExtractJsonStringLong(source, "description", 16000) + " " +
                ExtractJsonStringLong(source, "content", 16000) + " " + visible;
            bool contentMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, title + " " + content) ||
                MatchesExpectedTitleByFragments(expectedTitle, title + " " + content);
            bool authorMatch = String.IsNullOrWhiteSpace(expectedAuthor) ||
                MatchesExpectedAuthor(expectedAuthor, source);
            bool visibleState = Regex.IsMatch(source, "\\\"visible\\\"\\s*:\\s*true", RegexOptions.IgnoreCase);
            bool hasPublishedPayload =
                !String.IsNullOrWhiteSpace(ExtractJsonStringLong(source, "description", 16000)) ||
                !String.IsNullOrWhiteSpace(ExtractJsonStringLong(source, "content", 16000)) ||
                Regex.IsMatch(source, "\\\"(?:item|opus|modules|pictures|draw)\\\"\\s*:", RegexOptions.IgnoreCase);
            // Supplier tables frequently store the first sentence of a video as
            // the title. The official endpoint may expose a different video title.
            // Exact id + visible=true + author + a published payload is still
            // target-level availability evidence; an id-only shell is not.
            return idMatch && authorMatch && visibleState && hasPublishedPayload &&
                (contentMatch || !String.IsNullOrWhiteSpace(expectedAuthor));
        }

        internal static bool TryMatchWeiboLongArticle(string html, string id, string expectedTitle,
            string expectedExcerpt, string expectedAuthor)
        {
            string source = html ?? "";
            if (String.IsNullOrWhiteSpace(id) || source.IndexOf(id, StringComparison.OrdinalIgnoreCase) < 0) return false;
            string combined = ExtractTitle(source) + " " + ExtractProbableMainContentText(source) + " " + ExtractVisibleText(source);
            if (!MatchesExpectedContent(expectedTitle, expectedExcerpt, combined) &&
                !MatchesExpectedTitleByFragments(expectedTitle, combined)) return false;
            return String.IsNullOrWhiteSpace(expectedAuthor) || MatchesExpectedAuthor(expectedAuthor, combined);
        }

        private static bool IsWeiboLongArticleIdentityPresent(string html, string id,
            string expectedTitle, string expectedAuthor)
        {
            string source = html ?? "";
            if (String.IsNullOrWhiteSpace(id) || source.IndexOf(id, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            string pageTitle = ExtractTitle(source);
            bool titleMatch = !String.IsNullOrWhiteSpace(expectedTitle) &&
                (MatchesExpectedTitle(expectedTitle, pageTitle) || MatchesExpectedTitleByFragments(expectedTitle, pageTitle));
            bool authorMatch = String.IsNullOrWhiteSpace(expectedAuthor) ||
                MatchesExpectedAuthor(expectedAuthor, source);
            bool articleMarker = source.IndexOf("articleTitle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                source.IndexOf("article_list_url", StringComparison.OrdinalIgnoreCase) >= 0;
            // A paywall/follower-only notice is access restriction, not deletion;
            // the article title, id and owner still establish that it exists.
            return articleMarker && titleMatch && authorMatch;
        }

        private static bool IsWeiboLongArticleRemovalPage(string html, string id)
        {
            string source = html ?? "";
            return !String.IsNullOrWhiteSpace(id) && source.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(ExtractVisibleText(source),
                    "寰崥涓嶅瓨鍦ㄦ垨鏆傛棤鏌ョ湅鏉冮檺|鎶辨瓑[锛?]?姝ゅ井鍗氬凡琚垹闄?|璇ユ枃绔犲凡琚垹闄?",
                    RegexOptions.IgnoreCase);
        }

        private static bool MatchesExpectedTitleByFragments(string expectedTitle, string pageText)
        {
            string expected = NormalizeForMatch(expectedTitle);
            string page = NormalizeForMatch(pageText);
            if (expected.Length < 12 || page.Length < 12) return false;
            int fragmentLength = expected.Length >= 28 ? 6 : 5;
            int matches = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int offset = 0; offset + fragmentLength <= expected.Length; offset += fragmentLength)
            {
                string fragment = expected.Substring(offset, fragmentLength);
                if (seen.Add(fragment) && page.Contains(fragment)) matches++;
                if (matches >= 2) return true;
            }
            return false;
        }

        internal static bool TryMatchQcttVideoPage(string html, string id, string expectedTitle,
            string expectedExcerpt, string expectedAuthor)
        {
            string source = html ?? "";
            if (String.IsNullOrWhiteSpace(id) || source.IndexOf(id, StringComparison.OrdinalIgnoreCase) < 0) return false;
            string combined = ExtractTitle(source) + " " + ExtractProbableMainContentText(source) + " " + ExtractVisibleText(source);
            if (!MatchesExpectedContent(expectedTitle, expectedExcerpt, combined)) return false;
            return String.IsNullOrWhiteSpace(expectedAuthor) || MatchesExpectedAuthor(expectedAuthor, combined);
        }

        internal static bool TryMatchHexunNewsResponse(string json, string id, string expectedTitle, string expectedExcerpt)
        {
            string source = json ?? "";
            if (String.IsNullOrWhiteSpace(id) || source.Length == 0) return false;
            string currentTitle = ExtractJsonStringLong(source, "title", 1200);
            string currentContent = ExtractJsonStringLong(source, "content", 20000);
            if (String.IsNullOrWhiteSpace(currentTitle) || String.IsNullOrWhiteSpace(currentContent)) return false;
            return (MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + ExtractVisibleText(currentContent)) ||
                MatchesExpectedTitleByFragments(expectedTitle, currentTitle + " " + ExtractVisibleText(currentContent)));
        }

        internal static bool IsBilibiliArticleApiSuccess(string json)
        {
            string source = json ?? "";
            return Regex.IsMatch(source, "\\\"code\\\"\\s*:\\s*0\\b", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(source, "\\\"data\\\"\\s*:\\s*\\{", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(source, "\\\"title\\\"\\s*:\\s*\\\"[^\\\"]+", RegexOptions.IgnoreCase);
        }

        internal static bool TryMatchIqiyiCrawlerPage(string html, string id, string expectedTitle,
            string expectedExcerpt, string expectedAuthor)
        {
            string source = html ?? "";
            if (String.IsNullOrWhiteSpace(id) || source.IndexOf(id, StringComparison.OrdinalIgnoreCase) < 0) return false;
            string combined = ExtractTitle(source) + " " + ExtractProbableMainContentText(source) + " " + ExtractVisibleText(source);
            return MatchesExpectedContent(expectedTitle, expectedExcerpt, combined) &&
                (String.IsNullOrWhiteSpace(expectedAuthor) || MatchesExpectedAuthor(expectedAuthor, combined + " " + source));
        }

        internal static bool TryMatchDzhArticlePage(string body, string id, string expectedTitle,
            string expectedExcerpt, out string currentTitle)
        {
            currentTitle = "";
            string source = body ?? "";
            if (String.IsNullOrWhiteSpace(id) || ExtractJsonString(source, "RequestDocId") != id ||
                ExtractJsonInt(source, "Found", 0) != 1) return false;
            currentTitle = ExtractJsonStringLong(source, "Title", 1000);
            string summary = ExtractJsonStringLong(source, "Summary", 12000);
            return MatchesExpectedContent(expectedTitle, expectedExcerpt, currentTitle + " " + summary);
        }

        internal static bool IsUcMissingArticlePage(string body, string finalUrl, string id)
        {
            Uri final;
            if (String.IsNullOrWhiteSpace(id) || !Uri.TryCreate(finalUrl, UriKind.Absolute, out final) ||
                !final.Host.EndsWith("uczzd.cn", StringComparison.OrdinalIgnoreCase)) return false;
            Match aid = Regex.Match(final.Query ?? "", @"(?:^|[?&])aid=([0-9]{8,})(?:&|$)", RegexOptions.IgnoreCase);
            if (!aid.Success || aid.Groups[1].Value != id) return false;
            string visible = ExtractVisibleText(body ?? "");
            return Regex.IsMatch(visible, "(?:^|\\s)文章不存在(?:\\s|$)", RegexOptions.IgnoreCase);
        }

        internal static bool IsDingxinwenMissingTopicResponse(string body)
        {
            string source = body ?? "";
            return ExtractJsonInt(source, "code", Int32.MinValue) == 500 &&
                Regex.IsMatch(ExtractJsonString(source, "msg"), "帖子不存在", RegexOptions.IgnoreCase);
        }

        private static string Md5Hex(string value)
        {
            using (MD5 md5 = MD5.Create())
                return String.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")).Select(item => item.ToString("x2")));
        }

        private static string ExtractJsonObject(string json, string property)
        {
            Match match = Regex.Match(json ?? "", "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*(\\{.*\\})",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value : "";
        }

        private static string BuildTopnewsApiUrl(string id)
        {
            const string appId = "210710201732916889";
            const string appSecret = "594578ce51b456787f2e97ec76e2fff4";
            string deviceId = "";
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            // The share-page client signs sorted query keys, without separators.
            string signing = "app_id=" + appId + "deviceId=" + deviceId + "id=" + id +
                "is_html_decode=0timestamp=" + timestamp + appSecret;
            string sign = Md5Hex(signing);
            return "https://swnews.dingxinwen.com/api/news/getNewsInfo?id=" + Uri.EscapeDataString(id) +
                "&is_html_decode=0&timestamp=" + timestamp + "&app_id=" + appId +
                "&deviceId=&sign=" + sign;
        }

        private static bool IsWeiboRetryableVisitorResponse(string body)
        {
            string source = body ?? "";
            return String.IsNullOrWhiteSpace(source) || Regex.IsMatch(source,
                "Sina Visitor System|请求频繁|访问频繁|error_code\\\"\\s*:\\s*(100005|100006|20003)", RegexOptions.IgnoreCase);
        }

        private static void InvalidateWeiboVisitorCookie()
        {
            _weiboVisitorCookie = "";
            _weiboVisitorCookieCreatedUtc = DateTime.MinValue;
        }

        private async Task<string> GetWeiboVisitorCookieAsync(CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(_weiboVisitorCookie) &&
                DateTime.UtcNow - _weiboVisitorCookieCreatedUtc < TimeSpan.FromMinutes(20)) return _weiboVisitorCookie;
            await WeiboVisitorGate.WaitAsync(token);
            try
            {
                if (!String.IsNullOrWhiteSpace(_weiboVisitorCookie) &&
                    DateTime.UtcNow - _weiboVisitorCookieCreatedUtc < TimeSpan.FromMinutes(20)) return _weiboVisitorCookie;
                string fingerprint = Uri.EscapeDataString("{\"os\":\"1\",\"browser\":\"Chrome126,0,0,0\",\"fonts\":\"undefined\",\"screenInfo\":\"1920*1080*24\",\"plugins\":\"\"}");
                string genUrl = "https://passport.weibo.com/visitor/genvisitor?cb=gen_callback&fp=" + fingerprint;
                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126 Safari/537.36" },
                    { "Referer", "https://passport.weibo.com/" }
                };
                ProbeResponse generated = await TryReadProbeAsync(genUrl, headers, token);
                string tid = ExtractJsonString(generated == null ? "" : generated.Body, "tid");
                if (String.IsNullOrWhiteSpace(tid)) return "";
                string incarnateUrl = "https://passport.weibo.com/visitor/visitor?a=incarnate&t=" + Uri.EscapeDataString(tid) +
                    "&w=2&c=095&gc=&cb=cross_domain&from=weibo";
                ProbeResponse incarnated = await TryReadProbeAsync(incarnateUrl, headers, token);
                string sub = ExtractJsonString(incarnated == null ? "" : incarnated.Body, "sub");
                string subp = ExtractJsonString(incarnated == null ? "" : incarnated.Body, "subp");
                if (String.IsNullOrWhiteSpace(sub) || String.IsNullOrWhiteSpace(subp)) return "";
                _weiboVisitorCookie = "SUB=" + sub + "; SUBP=" + subp;
                _weiboVisitorCookieCreatedUtc = DateTime.UtcNow;
                return _weiboVisitorCookie;
            }
            finally { WeiboVisitorGate.Release(); }
        }

        private async Task<PlatformProbeOutcome> ProbeDouyinContentAsync(string originalUrl, string id, string expectedTitle,
            string expectedExcerpt, string expectedAuthor, CancellationToken token)
        {
            Uri original;
            if (Uri.TryCreate(originalUrl, UriKind.Absolute, out original))
            {
                string publicUrl = "https://www.douyin.com" + original.AbsolutePath;
                var publicHeaders = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (compatible; Baiduspider/2.0; +http://www.baidu.com/search/spider.html)" },
                    { "Accept-Language", "zh-CN,zh;q=0.9" }
                };
                ProbeResponse publicPage = await TryReadProbeAsync(publicUrl, publicHeaders, token);
                if (publicPage != null && (publicPage.Status == 404 || publicPage.Status == 410))
                    return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                        "official-public-page", "抖音", id, "抖音官方公开作品页返回 HTTP " + publicPage.Status + "，目标作品不存在", publicPage.FinalUrl, true);
                if (publicPage != null && publicPage.Status == 200)
                {
                    string currentTitle = ExtractTitle(publicPage.Body);
                    string visible = ExtractVisibleText(publicPage.Body);
                    string combined = currentTitle + " " + visible + " " + (publicPage.Body ?? "");
                    bool idMatch = combined.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool contentMatch = MatchesExpectedContent(expectedTitle, expectedExcerpt, combined);
                    bool authorMatch = MatchesExpectedAuthor(expectedAuthor, combined);
                    if (Regex.IsMatch(combined, "你要观看的(?:图文|视频|作品)不存在|作品不存在|视频已下线", RegexOptions.IgnoreCase))
                        return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                            "official-public-page", "抖音", id, "抖音官方公开作品页明确提示目标作品不存在", publicPage.FinalUrl, true);
                    if (idMatch && contentMatch && (String.IsNullOrWhiteSpace(expectedAuthor) || authorMatch))
                        return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                            "official-public-page", "抖音", id, "抖音官方公开作品页返回目标编号、匹配文案" +
                                (authorMatch ? "和作者“" + expectedAuthor.Trim() + "”" : ""), publicPage.FinalUrl, true);
                }
            }

            string probeUrl = "https://www.iesdouyin.com/share/video/" + id + "/";
            var headers = new Dictionary<string, string>
            {
                { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                { "Accept-Language", "zh-CN,zh;q=0.9" },
                { "Referer", "https://www.douyin.com/" }
            };
            ProbeResponse probe = await TryReadProbeAsync(probeUrl, headers, token);
            if (probe == null || probe.Status != 200) return null;
            string body = (probe.Body ?? "").Replace("\\\"", "\"");
            bool targetItem = body.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
            bool itemListEmpty = Regex.IsMatch(body, "\\\"item_list\\\"\\s*:\\s*\\[\\s*\\]", RegexOptions.IgnoreCase);
            Match filter = Regex.Match(body, "\\\"filter_reason\\\"\\s*:\\s*\\\"([^\\\"]+)", RegexOptions.IgnoreCase);
            bool targetDescription = targetItem && !itemListEmpty && Regex.IsMatch(body,
                "\\\"(?:aweme_id|item_id)\\\"\\s*:\\s*\\\"?" + Regex.Escape(id) + "\\\"?", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(body, "\\\"desc\\\"\\s*:\\s*\\\"[^\\\"]{4,}", RegexOptions.IgnoreCase);
            if (targetDescription)
                return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                    "official-share-page", "抖音", id, "抖音官方分享页返回目标作品编号、视频文案和非空作品数据", probeUrl, true);
            if (targetItem && itemListEmpty && filter.Success)
                return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                    "official-share-page", "抖音", id, "抖音官方分享页确认目标作品当前不可见（" + filter.Groups[1].Value + "）", probeUrl, true);
            return null;
        }

        internal static string ExtractBaiduVideoId(Uri uri)
        {
            if (uri == null) return "";
            string host = uri.Host.ToLowerInvariant();
            if (!(host.EndsWith("baidu.com", StringComparison.Ordinal) || host.EndsWith("yoojia.com", StringComparison.Ordinal))) return "";
            Match match = Regex.Match(uri.Query ?? "", @"(?:^|[?&])(?:vid|nid)=(?:sv_)?([0-9]{8,})", RegexOptions.IgnoreCase);
            if (match.Success && ((uri.AbsolutePath ?? "").IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (uri.Query ?? "").IndexOf("sv_", StringComparison.OrdinalIgnoreCase) >= 0)) return match.Groups[1].Value;
            match = Regex.Match(uri.AbsolutePath ?? "", @"/video/([0-9]{8,})(?:\.html)?", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "";
        }

        internal static string ExtractBaiduArticleId(Uri uri)
        {
            if (uri == null) return "";
            string host = uri.Host.ToLowerInvariant();
            if (!(host.EndsWith("baidu.com", StringComparison.Ordinal) || host.EndsWith("yoojia.com", StringComparison.Ordinal))) return "";
            string path = uri.AbsolutePath ?? "";
            if (path.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0) return "";
            Match match = Regex.Match(uri.Query ?? "", @"(?:^|[?&])nid=(?:(?:news|dt)_)?([0-9]{8,})", RegexOptions.IgnoreCase);
            if (match.Success && Regex.IsMatch(path, @"landing|tuwen|article", RegexOptions.IgnoreCase)) return match.Groups[1].Value;
            match = Regex.Match(uri.Query ?? "", @"(?:^|[?&])id=([0-9]{8,})", RegexOptions.IgnoreCase);
            if (match.Success && (host == "baijiahao.baidu.com" || Regex.IsMatch(path, @"/s$", RegexOptions.IgnoreCase))) return match.Groups[1].Value;
            match = Regex.Match(path, @"/article/([0-9]{8,})(?:\.html)?", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "";
        }

        internal static string ExtractBaiduArticleNid(Uri uri)
        {
            if (uri == null) return "";
            string path = uri.AbsolutePath ?? "";
            Match dt = Regex.Match(uri.Query ?? "", @"(?:^|[?&])nid=dt_([0-9]{8,})", RegexOptions.IgnoreCase);
            if (dt.Success && path.IndexOf("dtlanding", StringComparison.OrdinalIgnoreCase) >= 0)
                return "dt_" + dt.Groups[1].Value;
            string id = ExtractBaiduArticleId(uri);
            return String.IsNullOrWhiteSpace(id) ? "" : "news_" + id;
        }

        internal static string BuildBaiduPublicArticleUrl(string articleId)
        {
            if (String.IsNullOrWhiteSpace(articleId)) return "";
            string context = Uri.EscapeDataString("{\"nid\":\"news_" + articleId + "\"}");
            return "https://mbd.baidu.com/newspage/data/landingsuper?context=" + context + "&n_type=-1&p_from=-1";
        }

        private static bool IsBaiduDtArticle(Uri uri)
        {
            return uri != null && uri.Host.EndsWith("baidu.com", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(uri.AbsolutePath ?? "", "dtlanding", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(uri.Query ?? "", @"(?:^|[?&])nid=dt_[0-9]{8,}", RegexOptions.IgnoreCase);
        }

        private async Task<PlatformProbeOutcome> ProbeBaiduDtArticleAsync(Uri original, string expectedTitle,
            string expectedExcerpt, CancellationToken token)
        {
            Match idMatch = Regex.Match(original == null ? "" : original.Query, @"(?:^|[?&])nid=dt_([0-9]{8,})", RegexOptions.IgnoreCase);
            if (!idMatch.Success) return null;
            string id = idMatch.Groups[1].Value;
            string probeUrl = "https://mbd.baidu.com/newspage/data/landingreact?nid=news_" + id;
            var headers = new Dictionary<string, string>
            {
                { "User-Agent", "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36" },
                { "Accept-Language", "zh-CN,zh;q=0.9" },
                { "Referer", "https://mbd.baidu.com/" }
            };
            ProbeResponse probe = await TryReadCleanPublicProbeAsync(probeUrl, headers, token);
            if (probe == null || probe.Status != 200) return null;
            string body = probe.Body ?? "";
            string visible = ExtractVisibleText(body);
            if ((probe.FinalUrl ?? "").IndexOf("/newspage/data/error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                body.IndexOf("这里空空如也", StringComparison.OrdinalIgnoreCase) >= 0)
                return ProbeOutcome(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                    "official-public-page", "百度新闻", id, "百度新闻官方公开页确认目标内容不存在", probe.FinalUrl, true);
            bool targetIdentity = body.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
            if (targetIdentity && MatchesExpectedContent(expectedTitle, expectedExcerpt, ExtractTitle(body) + " " + visible))
                return ProbeOutcome(EvidenceKind.TargetContentPresent, EvidenceStrength.Conclusive,
                    "official-public-page", "百度新闻", id, "百度新闻官方公开页返回目标编号和匹配正文", probe.FinalUrl, true);
            return null;
        }

        internal static bool IsHaokanErrorResponse(string html, string videoId)
        {
            string source = html ?? "";
            return !String.IsNullOrEmpty(videoId) && source.IndexOf(videoId, StringComparison.OrdinalIgnoreCase) < 0 &&
                source.IndexOf("runtime~error", StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(source, @"/js/error\.[a-z0-9]+", RegexOptions.IgnoreCase);
        }

        internal static bool HasBaiduVideoIdentity(string html, string videoId, string expectedTitle)
        {
            string source = html ?? "";
            if (String.IsNullOrWhiteSpace(videoId) || source.IndexOf(videoId, StringComparison.OrdinalIgnoreCase) < 0) return false;
            // A generic Haokan error shell can include unrelated recommendation IDs. Require
            // target-specific metadata or a matching imported title before calling it accessible.
            bool targetMetadata = Regex.IsMatch(source,
                "(?:videoId|vid|video_id|play_url|playUrl)\\D{0,32}" + Regex.Escape(videoId), RegexOptions.IgnoreCase);
            string title = ExtractTitle(source);
            string visible = ExtractVisibleText(source);
            return targetMetadata && (MatchesExpectedTitle(expectedTitle, title + " " + visible) ||
                Regex.IsMatch(source, "(?:title|short_title|description|desc)\\D{0,32}[^\\\"']{8,}", RegexOptions.IgnoreCase));
        }

        private async Task<ProbeResponse> TryReadProbeAsync(string url, IDictionary<string, string> headers, CancellationToken token)
        {
            try
            {
                ProbeResponse proxyResult = await ReadProbeWithClientAsync(_client, url, headers, token);
                if (proxyResult != null && proxyResult.Status == 429) return proxyResult;
                if (proxyResult != null && proxyResult.Status != 403 && proxyResult.Status != 407)
                    return proxyResult;

                // A company proxy may block an otherwise public platform API. Try direct once, but never
                // treat either route's access denial as proof that the target content was removed.
                ProbeResponse directResult = await ReadProbeWithClientAsync(_directClient, url, headers, token);
                return directResult ?? proxyResult;
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                return null;
            }
            catch { return null; }
        }

        private async Task<ProbeResponse> TryReadCleanPublicProbeAsync(string url, IDictionary<string, string> headers, CancellationToken token)
        {
            await BaiduPublicProbeGate.WaitAsync(token);
            try
            {
                Uri pacingUri;
                if (Uri.TryCreate(url, UriKind.Absolute, out pacingUri)) await WaitForRequestSlotAsync(pacingUri, token);
                return await Task.Run(delegate
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var request = (HttpWebRequest)WebRequest.Create(url);
                        request.Method = "GET";
                        request.AllowAutoRedirect = true;
                        request.MaximumAutomaticRedirections = 8;
                        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                        request.Timeout = 18000;
                        request.ReadWriteTimeout = 18000;
                        request.Proxy = WebRequest.GetSystemWebProxy();
                        if (request.Proxy != null) request.Proxy.Credentials = CredentialCache.DefaultCredentials;
                        if (headers != null)
                            foreach (var header in headers)
                            {
                                if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) request.UserAgent = header.Value;
                                else if (header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) request.Referer = header.Value;
                                else if (header.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) request.Accept = header.Value;
                                else if (header.Key.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase)) request.Headers[HttpRequestHeader.AcceptLanguage] = header.Value;
                            }
                        using (var response = (HttpWebResponse)request.GetResponse())
                        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8, true))
                        {
                            char[] buffer = new char[Math.Min(_bodyBytes, 700000)];
                            int read = reader.ReadBlock(buffer, 0, buffer.Length);
                            return new ProbeResponse
                            {
                                Status = (int)response.StatusCode,
                                Body = new string(buffer, 0, read),
                                FinalUrl = response.ResponseUri == null ? url : response.ResponseUri.AbsoluteUri
                            };
                        }
                    }
                    catch (WebException ex)
                    {
                        var response = ex.Response as HttpWebResponse;
                        return response == null ? null : new ProbeResponse
                        {
                            Status = (int)response.StatusCode,
                            Body = "",
                            FinalUrl = response.ResponseUri == null ? url : response.ResponseUri.AbsoluteUri
                        };
                    }
                    catch { return null; }
                }, token);
            }
            finally { BaiduPublicProbeGate.Release(); }
        }

        private async Task<ProbeResponse> ReadProbeWithClientAsync(HttpClient client, string url, IDictionary<string, string> headers, CancellationToken token)
        {
            try
            {
                Uri pacingUri;
                if (Uri.TryCreate(url, UriKind.Absolute, out pacingUri)) await WaitForRequestSlotAsync(pacingUri, token);
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (headers != null)
                        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        return new ProbeResponse
                        {
                            Status = (int)response.StatusCode,
                            Body = await ReadLimitedBodyAsync(response.Content, Math.Min(_bodyBytes, 700000), token),
                            FinalUrl = response.RequestMessage != null && response.RequestMessage.RequestUri != null
                                ? response.RequestMessage.RequestUri.AbsoluteUri : url
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                return null;
            }
            catch { return null; }
        }

        private async Task<ProbeResponse> TryPostProbeAsync(string url, string form, IDictionary<string, string> headers, CancellationToken token)
        {
            try
            {
                Uri pacingUri;
                if (Uri.TryCreate(url, UriKind.Absolute, out pacingUri)) await WaitForRequestSlotAsync(pacingUri, token);
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(form ?? "", Encoding.UTF8, "application/x-www-form-urlencoded");
                    if (headers != null)
                        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    using (HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        return new ProbeResponse
                        {
                            Status = (int)response.StatusCode,
                            Body = await ReadLimitedBodyAsync(response.Content, Math.Min(_bodyBytes, 700000), token),
                            FinalUrl = response.RequestMessage != null && response.RequestMessage.RequestUri != null
                                ? response.RequestMessage.RequestUri.AbsoluteUri : url
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                return null;
            }
            catch { return null; }
        }

        internal static string ExtractJsonString(string json, string property)
        {
            Match match = Regex.Match(json ?? "", "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase);
            return match.Success ? CleanText(WebUtility.HtmlDecode(DecodeJsonUnicode(Regex.Unescape(match.Groups[1].Value))), 180) : "";
        }

        private static string ExtractJsonStringLong(string json, string property, int limit)
        {
            Match match = Regex.Match(json ?? "", "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase);
            return match.Success
                ? CleanText(WebUtility.HtmlDecode(DecodeJsonUnicode(Regex.Unescape(match.Groups[1].Value))), Math.Max(180, limit))
                : "";
        }

        private static int ExtractJsonInt(string json, string property, int fallback)
        {
            Match match = Regex.Match(json ?? "", "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*(-?[0-9]+)", RegexOptions.IgnoreCase);
            int value;
            return match.Success && Int32.TryParse(match.Groups[1].Value, out value) ? value : fallback;
        }

        private static bool IsTencentStockNewsApiResponse(string body)
        {
            return Regex.IsMatch(body ?? "", "\\\"code\\\"\\s*:\\s*0") &&
                Regex.IsMatch(body ?? "", "\\\"data\\\"\\s*:\\s*\\{");
        }

        private static bool IsTencentStockNewsEmpty(string body)
        {
            return Regex.IsMatch(body ?? "", "\\\"data\\\"\\s*:\\s*\\{\\s*\\\"data\\\"\\s*:\\s*\\[\\s*\\]\\s*\\}", RegexOptions.IgnoreCase);
        }

        private static bool IsTencentStockNewsIdMatch(string expected, string actual)
        {
            if (String.IsNullOrWhiteSpace(expected) || String.IsNullOrWhiteSpace(actual) ||
                !actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) return false;
            return actual.Substring(expected.Length).All(character => character == '0');
        }

        private static void ApplyBrowserResult(CheckResult result, string expectedTitle, BrowserSnapshot snapshot, int httpCode)
        {
            if (snapshot == null || String.IsNullOrWhiteSpace(snapshot.Html))
            {
                result.Verdict = "人工复核";
                if (String.IsNullOrEmpty(BrowserPath)) result.Evidence = "普通请求未找到原内容，且未检测到受支持的 Chromium 浏览器（Edge/Chrome）用于补充核验";
                else if (snapshot != null && snapshot.TimedOut) result.Evidence = "浏览器深度核验超时，请稍后复核";
                else result.Evidence = "普通请求未找到原内容，浏览器深度核验未取得有效页面";
                return;
            }

            string browserTitle = ExtractTitle(snapshot.Html);
            string browserVisible = ExtractVisibleText(snapshot.Html);
            result.AnalysisContext = AiReviewPolicy.BuildObservedContext(browserTitle,
                ExtractProbableMainContentText(snapshot.Html), browserVisible);
            string combined = (browserTitle + " " + browserVisible).ToLowerInvariant();
            string signal = FindSignal(combined, RemovedSignals);
            string restriction = FindSignal(combined, RestrictedSignals);

            bool expectedMatch = MatchesExpectedContent(expectedTitle, result.ExpectedExcerpt, browserTitle + " " + browserVisible);
            bool strongContentIdentity = HasStrongRenderedContentIdentity(result, new RenderedPageData
            {
                Title = browserTitle,
                Text = browserVisible,
                Html = snapshot.Html,
                MainText = ExtractProbableMainContentText(snapshot.Html),
                MainHtml = ExtractProbableMainContentHtml(snapshot.Html),
                Url = result.FinalUrl
            }, expectedMatch);
            if (strongContentIdentity)
            {
                result.Verdict = "仍可访问";
                result.Evidence = !String.IsNullOrEmpty(signal)
                    ? "浏览器补充核验确认目标正文仍存在；“" + signal + "”来自页面其他区域，不作为下架证据"
                    : "浏览器补充核验确认目标摘要、内容编号及正文结构仍存在（HTTP " + httpCode + "）";
                if (String.IsNullOrEmpty(result.Title) && !String.IsNullOrEmpty(browserTitle)) result.Title = browserTitle;
            }
            else if (!String.IsNullOrEmpty(signal) || LooksLikeErrorPage(result.FinalUrl, browserTitle, browserVisible))
            {
                bool explicitError = LooksLikeErrorPage(result.FinalUrl, browserTitle, browserVisible);
                string browserMainText = ExtractProbableMainContentText(snapshot.Html);
                string browserMainHtml = ExtractProbableMainContentHtml(snapshot.Html);
                bool explicitRemoval = IsExplicitTargetRemovalPage(signal, result.FinalUrl, browserTitle, browserVisible, snapshot.Html,
                    browserMainText, browserMainHtml);
                result.Verdict = explicitError || explicitRemoval ? "已失效" : "疑似已处置";
                result.Evidence = explicitError
                    ? "浏览器补充核验后进入已验证的错误页"
                    : explicitRemoval
                        ? "浏览器页面主体明确提示目标内容“" + signal + "”"
                        : "浏览器页面出现“" + signal + "”，但缺少目标正文身份依据，已保留待复核";
            }
            else if (!String.IsNullOrEmpty(restriction) || LooksLikeLogin(result.FinalUrl))
            {
                result.Verdict = NetworkRestrictionCircuitBreaker.IsSecurityOrRateLimitText(restriction)
                    ? "暂时异常" : "人工复核";
                result.Evidence = !String.IsNullOrEmpty(restriction)
                    ? "浏览器补充核验遇到验证/风控提示“" + restriction + "”"
                    : "浏览器补充核验跳转到登录页";
            }
            else if (IsStrongPlatformEmptyState(result.OriginalUrl, result.FinalUrl, expectedTitle, browserTitle, browserVisible, snapshot.Html))
            {
                result.Verdict = "已失效";
                result.Evidence = "平台失效页特征已确认，且 Excel 原标题/正文已不存在";
            }
            else if (CanInferRemovalFromRenderedPage(result.FinalUrl, browserVisible))
            {
                result.Verdict = "疑似已处置";
                result.Evidence = "浏览器只看到平台推荐流，未确认目标正文；页面改版或加载异常也可能造成此现象";
            }
            else
            {
                result.Verdict = "人工复核";
                result.Evidence = "浏览器补充核验后仍未找到 Excel 中的原标题，不做“未失效”猜测";
            }
        }

        internal static DeepDecision ClassifyRenderedPage(CheckResult result, RenderedPageData page)
        {
            var decision = new DeepDecision { Resolved = false, NeedsVerification = false, Verdict = "人工复核", Evidence = "持久浏览器仍无法确认" };
            if (result == null || page == null)
            {
                decision.Evidence = "未获取到可分析的浏览器页面";
                return decision;
            }

            string title = page.Title ?? "";
            string visible = page.Text ?? "";
            string html = page.Html ?? "";
            string currentUrl = page.Url ?? result.FinalUrl ?? result.OriginalUrl;
            string combined = (title + " " + visible).ToLowerInvariant();
            Uri currentRuleUri;
            Uri.TryCreate(result.OriginalUrl ?? currentUrl, UriKind.Absolute, out currentRuleUri);
            string signal = FirstNonEmpty(FindSignal(combined, RemovedSignals), PlatformRules.FindRemovedSignal(combined, currentRuleUri));
            string restriction = FirstNonEmpty(FindSignal(combined, RestrictedSignals), PlatformRules.FindRestrictedSignal(combined, currentRuleUri));

            int transportStatus;
            if (TryRecognizeRenderedTransportError(title, visible, html, out transportStatus))
            {
                return new DeepDecision
                {
                    Resolved = true,
                    NeedsVerification = false,
                    Verdict = "暂时异常",
                    Evidence = "浏览器显示 HTTP " + transportStatus +
                        " 错误页，未取得目标内容；可能来自本机代理、访问线路或目标服务器，不等同于内容已失效或触发风控"
                };
            }

            // Zhihu's "no knowledge exists" page is a target-specific empty state,
            // not the generic security/403 page. Keep this check before the generic
            // login/restriction branch so the known removal page is still resolved.
            if (IsZhihuRemovedEmptyState(result, currentUrl, title, visible))
            {
                return DecideEvidence(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                    "rendered-page", "知乎", "", "知乎页面主体明确提示目标回答不存在（没有知识存在的荒原）", currentUrl, true);
            }
            if (IsTiebaRemovedEmptyState(result, currentUrl, title, visible, html))
            {
                return DecideEvidence(EvidenceKind.TargetRemovalExplicit, EvidenceStrength.Conclusive,
                    "rendered-page", "百度贴吧", "", "百度贴吧目标帖子进入官方404/帖子不存在页面", currentUrl, true);
            }

            bool expectedMatch = MatchesExpectedContent(result.ExpectedTitle, result.ExpectedExcerpt, title + " " + visible);
            bool authorMatch = MatchesExpectedAuthor(result.ExpectedAuthor, title + " " + visible);
            bool strongContentIdentity = HasStrongRenderedContentIdentity(result, page, expectedMatch);
            Uri originalIdentityUri;
            Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out originalIdentityUri);
            bool reliableTitleIdentity = String.IsNullOrEmpty(signal) && String.IsNullOrEmpty(restriction) &&
                HasReliablePageTitleIdentity(result.ExpectedTitle, title, visible, originalIdentityUri, currentUrl);
            if (strongContentIdentity || reliableTitleIdentity)
            {
                string message = reliableTitleIdentity && !strongContentIdentity
                    ? "持久浏览器确认最终地址仍保留目标内容编号，且页面标题可靠匹配采集标题"
                    : authorMatch && expectedMatch
                    ? "持久浏览器确认目标内容片段及发文作者“" + result.ExpectedAuthor.Trim() + "”仍存在"
                    : expectedMatch
                    ? "持久浏览器确认原文标题、摘要或正文片段仍存在"
                    : "持久浏览器确认目标内容编号及正文结构仍存在";
                return DecideEvidence(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                    "rendered-page", result.Platform, "", message, currentUrl, true);
            }

            // Some article/video hosts expose only a small client-rendered shell
            // to ordinary HTTP clients, while the authoritative <title> and
            // target route remain public. Treat that combination as positive
            // identity evidence, but only on the same host and never on login,
            // error, or homepage redirects.
            if (HasSparseSameHostArticleIdentity(result, title, visible, currentUrl))
            {
                return DecideEvidence(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                    "rendered-page", result.Platform, "", "同站文章/视频页标题匹配采集记录，且最终地址保留目标路径", currentUrl, true);
            }

            if (HasCrossDomainArticleIdentity(result, title, visible, html, currentUrl))
            {
                return DecideEvidence(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                    "rendered-page", result.Platform, "", "原链接跳转到其他站点，但最终页面标题、正文结构和目标内容仍然匹配", currentUrl, true);
            }

            if (IsDouyinAccessibleShell(result, page))
            {
                return DecideEvidence(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                    "rendered-page", "抖音", "", "抖音页面保留目标作品编号及可用作品数据", currentUrl, true);
            }

            // Baidu shared video pages often expose the target video ID and title in
            // embedded JSON while the visible player is client-rendered. That is
            // sufficient identity evidence for these video links; requiring a full
            // article DOM here incorrectly sends an otherwise available video to
            // manual review.
            if (IsBaiduSharedVideoAccessible(result, currentUrl, title, visible, html))
            {
                return DecideEvidence(EvidenceKind.TargetContentPresent, EvidenceStrength.Strong,
                    "rendered-page", "百度系视频", "", "持久浏览器确认百度系共享视频编号和标题/播放数据仍存在", currentUrl, true);
            }
            if (!String.IsNullOrEmpty(signal) || LooksLikeErrorPage(currentUrl, title, visible))
            {
                bool explicitError = LooksLikeErrorPage(currentUrl, title, visible);
                bool explicitRemoval = IsExplicitTargetRemovalPage(signal, currentUrl, title, visible, html, page.MainText, page.MainHtml);
                string message = explicitError
                    ? "持久浏览器确认进入已验证的错误页"
                    : explicitRemoval
                        ? "页面主体明确提示目标内容“" + signal + "”"
                        : "页面出现“" + signal + "”，但未确认该提示属于目标正文，已保留待复核";
                return DecideEvidence(explicitError || explicitRemoval ? EvidenceKind.TargetRemovalExplicit : EvidenceKind.GenericPage,
                    explicitError || explicitRemoval ? EvidenceStrength.Conclusive : EvidenceStrength.Supporting,
                    "rendered-page", result.Platform, "", message, currentUrl, true);
            }

            Match zhihuAnswer = Regex.Match(result.OriginalUrl ?? "", @"/answer/([0-9]+)", RegexOptions.IgnoreCase);
            if (zhihuAnswer.Success)
            {
                string answerId = zhihuAnswer.Groups[1].Value;
                Match originalQuestion = Regex.Match(result.OriginalUrl ?? "", @"/question/([0-9]+)", RegexOptions.IgnoreCase);
                Match currentQuestion = Regex.Match(currentUrl ?? "", @"/question/([0-9]+)", RegexOptions.IgnoreCase);
                bool sameQuestion = originalQuestion.Success && currentQuestion.Success &&
                    String.Equals(originalQuestion.Groups[1].Value, currentQuestion.Groups[1].Value, StringComparison.Ordinal);
                bool answerGone = (currentUrl ?? "").IndexOf("/answer/" + answerId, StringComparison.OrdinalIgnoreCase) < 0;
                if (sameQuestion && answerGone)
                {
                    return DecideEvidence(EvidenceKind.TargetRedirectedAway, EvidenceStrength.Conclusive,
                        "rendered-page", "知乎", answerId, "目标知乎回答已跳回同一问题页，最终地址中回答编号已消失", currentUrl, true);
                }
            }

            // Many content sites render a generic login or SMS-code dialog on top of a
            // completely readable page. Strong content identity is more reliable than
            // that overlay; only pause for verification after content checks fail.
            Uri verificationUri;
            if (Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out verificationUri) && IsXiaohongshu(verificationUri) &&
                (!String.IsNullOrEmpty(restriction) || LooksLikeLogin(currentUrl)))
            {
                decision.NeedsVerification = false;
                decision.Evidence = "小红书需要扫码、登录或在手机 App 内查看，已自动转人工复核并继续下一条";
                return decision;
            }
            if (!String.IsNullOrEmpty(restriction) || LooksLikeLogin(currentUrl))
            {
                string message = !String.IsNullOrEmpty(restriction)
                    ? "当前平台需要登录/验证：" + restriction
                    : "当前页面需要登录";
                return DecideEvidence(EvidenceKind.AccessRestricted, EvidenceStrength.Supporting,
                    "rendered-page", result.Platform, "", message, currentUrl, true);
            }

            Uri originalUri;
            if (Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out originalUri) && LooksLikePlatformRemovalRedirect(originalUri, currentUrl))
            {
                decision.Resolved = true;
                decision.Verdict = "疑似已处置";
                decision.Evidence = "原内容跳转到首页或其他内容页，但登录状态、设备适配或页面改版也可能导致跳转";
                return decision;
            }

            if (IsStrongPlatformEmptyState(result.OriginalUrl, currentUrl, result.ExpectedTitle, title, visible, html))
            {
                decision.Resolved = true;
                decision.Verdict = "已失效";
                decision.Evidence = "持久浏览器确认平台失效页特征，Excel 原标题/正文已不存在";
                return decision;
            }

            if (Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out originalUri) && LooksLikeHomepageRedirect(originalUri, currentUrl))
            {
                decision.Resolved = true;
                decision.Verdict = "疑似已处置";
                decision.Evidence = "持久浏览器确认原内容链接跳回站点首页";
                return decision;
            }

            if (CanInferRemovalFromRenderedPage(currentUrl, visible))
            {
                return DecideEvidence(EvidenceKind.GenericPage, EvidenceStrength.Supporting,
                    "rendered-page", result.Platform, "", "页面只展示平台推荐流，尚无明确删除证据，保留待复核", currentUrl, true);
            }

            decision.Evidence = "持久浏览器已加载页面，但仍没有足够证据判定是否失效";
            return decision;
        }

        internal static bool TryRecognizeRenderedTransportError(string title, string visible, string html, out int statusCode)
        {
            statusCode = 0;
            string text = ((title ?? "") + " " + (visible ?? "") + " " +
                ((html ?? "").Length > 3000 ? (html ?? "").Substring(0, 3000) : (html ?? ""))).ToLowerInvariant();
            Match status = Regex.Match(text, @"(?:http\s*(?:error\s*)?|status(?:\s+code)?\s*[:：]?\s*)(408|500|501|502|503|504|505|520|521|522|523|524)\b",
                RegexOptions.IgnoreCase);
            Match browserCode = Regex.Match(text, @"err_(?:connection_[a-z_]+|timed_out|name_not_resolved|address_unreachable|proxy_connection_failed|tunnel_connection_failed|bad_gateway)",
                RegexOptions.IgnoreCase);
            bool errorShell = Regex.IsMatch(text,
                @"当前无法使用此页面|当前无法处理此请求|无法访问此网站|无法连接到此页面|this page isn.?t working|this site can.?t be reached|bad gateway|gateway timeout|proxy error",
                RegexOptions.IgnoreCase);
            if (!status.Success && !browserCode.Success) return false;
            if (!errorShell && browserCode.Success == false) return false;
            if (status.Success) Int32.TryParse(status.Groups[1].Value, out statusCode);
            if (statusCode == 0) statusCode = 502;
            return true;
        }

        internal static RenderedPageData BuildRenderedPageData(string html, string url)
        {
            string source = html ?? "";
            return new RenderedPageData
            {
                Url = url ?? "",
                Title = ExtractTitle(source),
                Text = ExtractVisibleText(source),
                Html = source,
                MainText = ExtractProbableMainContentText(source),
                MainHtml = ExtractProbableMainContentHtml(source)
            };
        }

        private static bool IsZhihuRemovedEmptyState(CheckResult result, string currentUrl, string title, string visible)
        {
            Uri original;
            if (result == null || !Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out original) ||
                !original.Host.EndsWith("zhihu.com", StringComparison.OrdinalIgnoreCase)) return false;
            string combined = (title ?? "") + " " + (visible ?? "");
            if (!Regex.IsMatch(combined, "没有知识存在的荒原|资源不存在|该回答不存在|回答不存在", RegexOptions.IgnoreCase)) return false;
            // A security page can contain the word "不存在" in a redirect parameter;
            // require the visible empty-state text and a real answer/pin target.
            return (original.AbsolutePath ?? "").IndexOf("/answer/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (original.AbsolutePath ?? "").IndexOf("/pin/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (currentUrl ?? "").IndexOf("/answer/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTiebaRemovedEmptyState(CheckResult result, string currentUrl, string title,
            string visible, string html)
        {
            Uri original;
            if (result == null || !Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out original) ||
                !original.Host.EndsWith("tieba.baidu.com", StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(original.AbsolutePath ?? "", @"^/p/[0-9]+/?$", RegexOptions.IgnoreCase)) return false;
            string combined = (title ?? "") + " " + (visible ?? "") + " " + (html ?? "");
            if (Regex.IsMatch(combined, "贴子可能已被删除|帖子可能已被删除|该贴子不存在|该帖子不存在", RegexOptions.IgnoreCase)) return true;
            return Regex.IsMatch(title ?? "", @"^贴吧\s*404$", RegexOptions.IgnoreCase) &&
                !LooksLikeLogin(currentUrl);
        }

        private static bool IsBaiduSharedVideoAccessible(CheckResult result, string currentUrl, string title, string visible, string html)
        {
            Uri original;
            if (result == null || !Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out original)) return false;
            string host = original.Host.ToLowerInvariant();
            if (!(host.EndsWith("baidu.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("yoojia.com", StringComparison.OrdinalIgnoreCase))) return false;
            string id = ExtractBaiduVideoId(original);
            if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(html) ||
                html.IndexOf(id, StringComparison.OrdinalIgnoreCase) < 0 || IsHaokanErrorResponse(html, id)) return false;
            string combined = (title ?? "") + " " + (visible ?? "") + " " + html;
            return MatchesExpectedTitle(result.ExpectedTitle, combined) ||
                Regex.IsMatch(html, "(?:videoId|video_id|vid|aweme_id)\\D{0,16}" + Regex.Escape(id), RegexOptions.IgnoreCase);
        }

        private static bool IsDouyinAccessibleShell(CheckResult result, RenderedPageData page)
        {
            Uri original;
            if (result == null || page == null || !Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out original)) return false;
            string host = original.Host.ToLowerInvariant();
            if (!(host.EndsWith("douyin.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("iesdouyin.com", StringComparison.OrdinalIgnoreCase))) return false;
            Match idMatch = Regex.Match(original.AbsolutePath ?? "", @"/video/([0-9]{12,})", RegexOptions.IgnoreCase);
            if (!idMatch.Success) return false;
            string id = idMatch.Groups[1].Value;
            string html = page.Html ?? "";
            string visible = page.Text ?? "";
            if (html.IndexOf(id, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (Regex.IsMatch((page.Title ?? "") + " " + visible, "作品不存在|视频已下线|内容不可见|安全验证|登录后查看", RegexOptions.IgnoreCase)) return false;
            return Regex.IsMatch(html, "(?:aweme_id|item_id|video_id|itemId)\\D{0,16}" + Regex.Escape(id), RegexOptions.IgnoreCase) ||
                Regex.IsMatch(html, "(?:desc|description|share_title)\\D{0,16}[^\\\"']{4,}", RegexOptions.IgnoreCase);
        }

        internal async Task<CheckResult> EscalateEvidenceAsync(CheckResult result, CancellationToken token)
        {
            if (result == null || !NetworkRestrictionCircuitBreaker.IsTransientRestriction(result)) return result;
            Uri original;
            if (!Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out original)) return result;

            var attempts = new List<string>();
            var trail = result.EvidenceTrail ?? new List<VerificationEvidence>();
            bool chinaProbeCapacityLimited = false;
            result.EvidenceTrail = trail;
            result.InfrastructureKey = RequestPacingKey(original);
            trail.Add(new VerificationEvidence
            {
                Kind = EvidenceKind.TemporaryFailure,
                Strength = EvidenceStrength.Supporting,
                Source = "initial-request",
                Platform = result.Platform,
                Message = "首次访问未取得正文：" + (result.Evidence ?? ""),
                FinalUrl = result.FinalUrl,
                IsCurrentResponse = true
            });

            Uri clean = BuildCleanEvidenceUri(original);
            ProbeResponse cleanRemovalCandidate = null;
            if (clean != null && !String.Equals(clean.AbsoluteUri, original.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                attempts.Add("去除分享/统计参数");
                ProbeResponse cleanProbe = await TryReadProbeAsync(clean.AbsoluteUri, null, token);
                if (cleanProbe != null && (cleanProbe.Status == 404 || cleanProbe.Status == 410))
                    cleanRemovalCandidate = cleanProbe;
                else if (ApplyProbeEvidence(result, cleanProbe, "规范化链接", clean, trail))
                {
                    result.AcquisitionAttempts = String.Join(" → ", attempts);
                    result.EvidenceStage = "自动追证已确认";
                    return result;
                }
            }

            attempts.Add("同站健康对照");
            Uri siteRoot = new UriBuilder(original.Scheme, original.Host,
                original.IsDefaultPort ? -1 : original.Port, "/").Uri;
            ProbeResponse rootProbe = await TryReadProbeAsync(siteRoot.AbsoluteUri, null, token);
            if (rootProbe != null)
            {
                if (rootProbe.Status >= 200 && rootProbe.Status < 400)
                {
                    result.SiteHealth = "站点首页可访问";
                    trail.Add(new VerificationEvidence
                    {
                        Kind = EvidenceKind.IdentityOnly,
                        Strength = EvidenceStrength.Supporting,
                        Source = "site-control",
                        Platform = result.Platform,
                        Message = "同站首页返回 HTTP " + rootProbe.Status + "，目标链接异常不是整站完全离线",
                        FinalUrl = rootProbe.FinalUrl,
                        IsCurrentResponse = true
                    });
                    if (cleanRemovalCandidate != null &&
                        ApplyProbeEvidence(result, cleanRemovalCandidate, "规范化链接", clean, trail))
                    {
                        result.AcquisitionAttempts = String.Join(" → ", attempts);
                        result.EvidenceStage = "自动追证已确认";
                        return result;
                    }
                }
                else if (rootProbe.Status >= 500)
                {
                    result.SiteHealth = "站点整体异常";
                    trail.Add(new VerificationEvidence
                    {
                        Kind = EvidenceKind.TemporaryFailure,
                        Strength = EvidenceStrength.Strong,
                        Source = "site-control",
                        Platform = result.Platform,
                        Message = "同站首页也返回 HTTP " + rootProbe.Status + "，当前更符合整站或共享基础设施异常",
                        FinalUrl = rootProbe.FinalUrl,
                        IsCurrentResponse = true
                    });
                }
                else result.SiteHealth = "站点首页 HTTP " + rootProbe.Status;
            }
            else result.SiteHealth = "同站首页未响应";

            if (ShouldTryChinaEyeballEvidence(result, original))
            {
                attempts.Add("中国普通宽带两步取证");
                RemoteEvidenceResponse chinaEyeball = await TryChinaEyeballEvidenceAsync(original, token);
                string chinaSource = chinaEyeball == null || String.IsNullOrWhiteSpace(chinaEyeball.Source)
                    ? "中国普通宽带公开探针" : chinaEyeball.Source;
                if (ApplyRemoteEvidence(result, chinaEyeball, chinaSource, trail))
                {
                    result.AcquisitionAttempts = String.Join(" → ", attempts);
                    result.EvidenceStage = "中国普通宽带两步取证已确认";
                    return result;
                }
                if (chinaEyeball != null && !String.IsNullOrWhiteSpace(chinaEyeball.Error))
                {
                    chinaProbeCapacityLimited = IsChinaProbeCapacityFailure(chinaEyeball.Error);
                    trail.Add(new VerificationEvidence
                    {
                        Kind = EvidenceKind.TemporaryFailure,
                        Strength = EvidenceStrength.Supporting,
                        Source = "china-eyeball",
                        Platform = result.Platform,
                        Message = "中国普通宽带两步取证本次未取得结论：" +
                            ExecutionLogWriter.Safe(chinaEyeball.Error, 260),
                        FinalUrl = result.OriginalUrl,
                        IsCurrentResponse = true
                    });
                }
                else if (chinaEyeball != null)
                {
                    string chinaBody = chinaEyeball.Html ?? chinaEyeball.Text ?? "";
                    RenderedPageData diagnosticPage = BuildRenderedPageData(chinaBody, result.OriginalUrl);
                    trail.Add(new VerificationEvidence
                    {
                        Kind = EvidenceKind.IdentityOnly,
                        Strength = EvidenceStrength.Supporting,
                        Source = chinaSource,
                        Platform = result.Platform,
                        Message = "中国普通宽带探针返回 HTTP " + chinaEyeball.Status +
                            "，但当前响应尚未通过正文身份规则（页面字节 " +
                            chinaBody.Length + "，标题“" + ExecutionLogWriter.Safe(diagnosticPage.Title, 80) +
                            "”，主内容字符 " + (diagnosticPage.MainText ?? "").Length + "）",
                        FinalUrl = result.OriginalUrl,
                        IsCurrentResponse = true
                    });
                }
            }

            attempts.Add("公开云取证");
            RemoteEvidenceResponse publicCloud = await TryPublicCloudEvidenceAsync(original, token);
            if (ApplyRemoteEvidence(result, publicCloud, "public-cloud-reader", trail))
            {
                result.AcquisitionAttempts = String.Join(" → ", attempts);
                result.EvidenceStage = "公开云取证已确认";
                return result;
            }
            if (publicCloud != null && publicCloud.TargetUnreachable)
            {
                trail.Add(new VerificationEvidence
                {
                    Kind = EvidenceKind.TemporaryFailure,
                    Strength = EvidenceStrength.Strong,
                    Source = "public-cloud-reader",
                    Platform = result.Platform,
                    Message = "独立公网云浏览器也无法连接目标地址：" +
                        ExecutionLogWriter.Safe(publicCloud.Error, 260),
                    FinalUrl = result.OriginalUrl,
                    IsCurrentResponse = true
                });
            }

            List<string> remoteEndpoints = RemoteEvidenceStore.LoadEndpoints();
            if (remoteEndpoints.Count > 0)
            {
                int endpointNumber = 0;
                foreach (string endpoint in remoteEndpoints)
                {
                    endpointNumber++;
                    attempts.Add("远程节点" + endpointNumber);
                    RemoteEvidenceResponse remote = await TryRemoteEvidenceAsync(endpoint, original, token);
                    if (ApplyRemoteEvidence(result, remote, "remote-" + endpointNumber, trail))
                    {
                        result.AcquisitionAttempts = String.Join(" → ", attempts);
                        result.EvidenceStage = "远程追证已确认";
                        return result;
                    }
                }
                result.EvidenceStage = "本地与远程追证均未确认";
            }
            else
            {
                attempts.Add("远程节点未配置");
                result.EvidenceStage = "本地追证完成，远程节点未配置";
            }

            result.AcquisitionAttempts = String.Join(" → ", attempts);
            string comparison = String.IsNullOrWhiteSpace(result.SiteHealth) ? "" : "；" + result.SiteHealth;
            if (!chinaProbeCapacityLimited && ShouldMarkPubliclyUnavailable(result, publicCloud))
            {
                result.Verdict = "公网不可访问";
                result.EvidenceStage = "自动多线路不可访问已记录";
                result.Evidence = "自动多线路不可访问（内容状态未知）：本机系统代理、直连、同站健康对照和独立公网云浏览器均未取得目标正文或正常页面" +
                    comparison + "；这只能确认当前自动取证环境未取得内容，仍需独立普通网络复核";
            }
            else
            {
                result.Verdict = "暂时异常";
                result.EvidenceStage = chinaProbeCapacityLimited ? "外部探针额度不足，保留自动续检" : result.EvidenceStage;
                result.Evidence = chinaProbeCapacityLimited
                    ? "检测未完成：外部中国宽带探针达到本小时额度；链接已保留在“继续未完成”队列，不代表链接失效"
                    : "自动追证仍未取得目标正文或明确删除页" + comparison +
                      (remoteEndpoints.Count == 0 ? "；已尝试公开云取证，未配置自有远程节点" : "；已尝试 " + remoteEndpoints.Count + " 个自有远程节点");
            }
            return result;
        }

        internal static bool IsChinaProbeCapacityFailure(string error)
        {
            string text = (error ?? "").ToLowerInvariant();
            return text.Contains("globalping") &&
                (text.Contains("429") || text.Contains("rate_limit") ||
                 text.Contains("rate limit") || text.Contains("额度") || text.Contains("限流"));
        }

        private static Uri BuildCleanEvidenceUri(Uri original)
        {
            if (original == null) return null;
            try
            {
                var builder = new UriBuilder(original) { Fragment = "" };
                if (String.IsNullOrWhiteSpace(builder.Query)) return builder.Uri;
                var kept = new List<string>();
                foreach (string pair in builder.Query.TrimStart('?').Split('&'))
                {
                    if (String.IsNullOrWhiteSpace(pair)) continue;
                    string key = pair.Split('=')[0].Trim().ToLowerInvariant();
                    if (key.StartsWith("utm_", StringComparison.Ordinal) ||
                        new[] { "share_token", "share_uid", "share_did", "timestamp", "tt_from",
                            "module_name", "category_new", "upstream_biz", "use_new_style",
                            "isappinstalled", "sharefrom", "share_source" }.Contains(key))
                        continue;
                    kept.Add(pair);
                }
                builder.Query = String.Join("&", kept);
                return builder.Uri;
            }
            catch { return original; }
        }

        private static bool ApplyProbeEvidence(CheckResult result, ProbeResponse probe, string source,
            Uri requested, List<VerificationEvidence> trail)
        {
            if (result == null || probe == null) return false;
            if (probe.Status == 404 || probe.Status == 410)
            {
                result.Verdict = "已失效";
                result.StatusCode = probe.Status.ToString();
                result.FinalUrl = String.IsNullOrWhiteSpace(probe.FinalUrl) ? requested.AbsoluteUri : probe.FinalUrl;
                result.Evidence = source + "返回 HTTP " + probe.Status + "，确认目标地址不存在";
                trail.Add(new VerificationEvidence
                {
                    Kind = EvidenceKind.TargetRemovalExplicit,
                    Strength = EvidenceStrength.Conclusive,
                    Source = source,
                    Platform = result.Platform,
                    Message = result.Evidence,
                    FinalUrl = result.FinalUrl,
                    IsCurrentResponse = true
                });
                return true;
            }
            if (probe.Status < 200 || probe.Status >= 400 || String.IsNullOrWhiteSpace(probe.Body)) return false;
            RenderedPageData page = BuildRenderedPageData(probe.Body,
                String.IsNullOrWhiteSpace(probe.FinalUrl) ? requested.AbsoluteUri : probe.FinalUrl);
            // Public readers often strip the target id from their markdown,
            // while preserving a platform-specific empty-state message.  The
            // message is still target-level evidence when it comes from the
            // requested platform page, so accept it before generic rendering
            // rules require an id echo.
            string remoteVisible = ExtractVisibleText(probe.Body ?? "");
            if (IsRemoteTargetSpecificRemoval(result, remoteVisible, page.Title, probe.FinalUrl))
            {
                result.Verdict = "已失效";
                result.StatusCode = probe.Status.ToString();
                result.FinalUrl = String.IsNullOrWhiteSpace(probe.FinalUrl) ? requested.AbsoluteUri : probe.FinalUrl;
                result.Title = page.Title;
                result.Evidence = source + "确认平台目标页明确提示内容不存在或已删除";
                trail.Add(new VerificationEvidence
                {
                    Kind = EvidenceKind.TargetRemovalExplicit,
                    Strength = EvidenceStrength.Conclusive,
                    Source = source,
                    Platform = result.Platform,
                    Message = result.Evidence,
                    FinalUrl = result.FinalUrl,
                    IsCurrentResponse = true
                });
                return true;
            }
            if (IsIndependentGenericArticleProof(result, page, source, requested))
            {
                result.Verdict = "仍可访问";
                result.StatusCode = probe.Status.ToString();
                result.FinalUrl = page.Url;
                result.Title = page.Title;
                string articleSummary = ExtractMetaDescription(page.Html);
                result.AnalysisContext = AiReviewPolicy.BuildObservedContext(page.Title,
                    !String.IsNullOrWhiteSpace(page.MainText) ? page.MainText : articleSummary, page.Text);
                result.Evidence = source + "确认：完成站点防火墙会话后，目标原地址返回 HTTP " +
                    probe.Status + "、有效文章标题和正文摘要/正文结构";
                trail.Add(new VerificationEvidence
                {
                    Kind = EvidenceKind.TargetContentPresent,
                    Strength = EvidenceStrength.Strong,
                    Source = source,
                    Platform = result.Platform,
                    Message = result.Evidence,
                    FinalUrl = result.FinalUrl,
                    IsCurrentResponse = true
                });
                return true;
            }
            DeepDecision decision = ClassifyRenderedPage(result, page);
            if (!decision.Resolved || (decision.Verdict != "已失效" && decision.Verdict != "仍可访问")) return false;
            result.Verdict = decision.Verdict;
            result.StatusCode = probe.Status.ToString();
            result.FinalUrl = page.Url;
            result.Title = page.Title;
            result.AnalysisContext = AiReviewPolicy.BuildObservedContext(page.Title, page.MainText, page.Text);
            result.Evidence = source + "确认：" + decision.Evidence;
            trail.Add(new VerificationEvidence
            {
                Kind = decision.Verdict == "已失效" ? EvidenceKind.TargetRemovalExplicit : EvidenceKind.TargetContentPresent,
                Strength = EvidenceStrength.Strong,
                Source = source,
                Platform = result.Platform,
                Message = result.Evidence,
                FinalUrl = result.FinalUrl,
                IsCurrentResponse = true
            });
            return true;
        }

        private static bool IsIndependentGenericArticleProof(CheckResult result, RenderedPageData page,
            string source, Uri requested)
        {
            if (result == null || page == null || requested == null ||
                String.IsNullOrWhiteSpace(source) ||
                (source.IndexOf("Globalping 中国普通宽带", StringComparison.OrdinalIgnoreCase) < 0 &&
                 source.IndexOf("public-cloud-reader", StringComparison.OrdinalIgnoreCase) < 0))
                return false;
            Uri current;
            if (!Uri.TryCreate(page.Url, UriKind.Absolute, out current) ||
                !String.Equals(current.Host, requested.Host, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(current.AbsolutePath.TrimEnd('/'), requested.AbsolutePath.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            string title = CleanText(page.Title, 220);
            string mainText = CleanText(!String.IsNullOrWhiteSpace(page.MainText) ? page.MainText : page.Text, 12000);
            string description = ExtractMetaDescription(page.Html);
            string html = page.Html ?? "";
            if (title.Length < 4) return false;
            if (Regex.IsMatch(title + " " + CleanText(page.Text, 2400),
                "安全验证|请完成.*验证|验证码|登录后查看更多|环境异常|百度安全验证|captcha|verify you are human",
                RegexOptions.IgnoreCase))
                return false;
            if (Regex.IsMatch(title, @"网站防火墙|403|404|502|bad gateway|页面不存在|文章不存在|内容不存在|已删除|已下线",
                RegexOptions.IgnoreCase))
                return false;
            int transportStatus;
            if (TryRecognizeRenderedTransportError(title, page.Text, html, out transportStatus)) return false;
            bool articleMarkup = Regex.IsMatch(html,
                @"<(?:article|h1)\b|(?:class|id)\s*=\s*[""'][^""']*(?:article|content|news|detail|post)[^""']*[""']",
                RegexOptions.IgnoreCase);
            bool paragraphBody = Regex.Matches(html, @"<p(?:\s|>)", RegexOptions.IgnoreCase).Count >= 2;
            bool visibleBody = mainText.Length >= 120 && articleMarkup && paragraphBody;
            // Jina/public readers return normalized Markdown rather than the
            // source HTML.  In that case the synthetic probe body has no
            // article tags, but an authoritative title plus substantial body
            // text from the same host/path is still strong availability proof.
            bool publicReaderIdentity = MatchesExpectedContent(result.ExpectedTitle, result.ExpectedExcerpt,
                title + " " + mainText + " " + description) &&
                (String.IsNullOrWhiteSpace(result.ExpectedAuthor) ||
                 MatchesExpectedAuthor(result.ExpectedAuthor, title + " " + mainText));
            bool publicReaderBody = source.IndexOf("public-cloud-reader", StringComparison.OrdinalIgnoreCase) >= 0 &&
                mainText.Length >= 180 && publicReaderIdentity;
            bool embeddedSummary = description.Length >= 40 &&
                !Regex.IsMatch(description, @"网站防火墙|页面不存在|文章不存在|内容不存在|已删除|已下线",
                    RegexOptions.IgnoreCase);
            return visibleBody || publicReaderBody || embeddedSummary;
        }

        private static bool IsRemoteTargetSpecificRemoval(CheckResult result, string visible,
            string title, string finalUrl)
        {
            string platform = (result.Platform ?? "").Trim();
            string text = (title ?? "") + " " + (visible ?? "");
            if (platform.IndexOf("贴吧", StringComparison.OrdinalIgnoreCase) >= 0)
                return Regex.IsMatch(text, "贴子可能已被删除|帖子可能已被删除|该贴子不存在|该帖子不存在",
                    RegexOptions.IgnoreCase);
            if (platform.IndexOf("知乎", StringComparison.OrdinalIgnoreCase) >= 0)
                return Regex.IsMatch(text, "没有知识存在的荒原|该问题不存在|该回答不存在|回答不存在",
                    RegexOptions.IgnoreCase);
            return false;
        }

        internal static bool ShouldTryPublicCloudForUnresolved(Uri uri, CheckResult result)
        {
            if (uri == null || result == null) return false;
            string host = (uri.Host ?? "").ToLowerInvariant();
            string platform = (result.Platform ?? "").Trim();
            return host.EndsWith("tieba.baidu.com", StringComparison.Ordinal) ||
                host.EndsWith("aikahao.xcar.com.cn", StringComparison.Ordinal) ||
                host.EndsWith("360doc.cn", StringComparison.Ordinal) ||
                host.EndsWith("zhihu.com", StringComparison.Ordinal) ||
                host.EndsWith("weibo.com", StringComparison.Ordinal) ||
                host.EndsWith("douyin.com", StringComparison.Ordinal) ||
                host.EndsWith("iesdouyin.com", StringComparison.Ordinal) ||
                host.EndsWith("mbd.baidu.com", StringComparison.Ordinal) ||
                host.EndsWith("baijiahao.baidu.com", StringComparison.Ordinal) ||
                host.EndsWith("mp.weixin.qq.com", StringComparison.Ordinal) ||
                host.EndsWith("xueqiu.com", StringComparison.Ordinal) ||
                host.EndsWith("dcdapp.com", StringComparison.Ordinal) ||
                host.EndsWith("dongchedi.com", StringComparison.Ordinal) ||
                host.EndsWith("jianshu.com", StringComparison.Ordinal) ||
                platform.IndexOf("贴吧", StringComparison.OrdinalIgnoreCase) >= 0 ||
                platform.IndexOf("爱咖", StringComparison.OrdinalIgnoreCase) >= 0 ||
                platform.IndexOf("雪球", StringComparison.OrdinalIgnoreCase) >= 0 ||
                platform.IndexOf("懂车帝", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsGenericWebMedia(string platform)
        {
            string value = (platform ?? "").Trim();
            return value.Length == 0 || value == "网媒" || value == "未知" || value == "未知平台";
        }

        private static bool TryBuildKnownShortLinkEvidenceUri(Uri original, string finalUrl, out Uri evidenceUri)
        {
            evidenceUri = null;
            if (original == null || !String.Equals(original.Host, "t.cn", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string decoded = WebUtility.UrlDecode(finalUrl ?? "");
                Match weiboVideo = Regex.Match(decoded,
                    @"https?://weibo\.com/tv/show/[0-9]+:[0-9]+", RegexOptions.IgnoreCase);
                if (weiboVideo.Success) return Uri.TryCreate(weiboVideo.Value, UriKind.Absolute, out evidenceUri);
                Match videoId = Regex.Match(decoded, @"(?:1034%3A|1034:)([0-9]{12,})", RegexOptions.IgnoreCase);
                if (videoId.Success)
                    return Uri.TryCreate("https://weibo.com/tv/show/1034:" + videoId.Groups[1].Value,
                        UriKind.Absolute, out evidenceUri);
            }
            catch { }
            return false;
        }

        internal static bool TryExtractWeiboVideoEvidenceUri(string text, out Uri evidenceUri)
        {
            evidenceUri = null;
            string decoded = WebUtility.UrlDecode(text ?? "");
            Match weiboVideo = Regex.Match(decoded,
                @"https?://weibo\.com/tv/show/[0-9]+:[0-9]+", RegexOptions.IgnoreCase);
            if (weiboVideo.Success) return Uri.TryCreate(weiboVideo.Value, UriKind.Absolute, out evidenceUri);
            Match videoId = Regex.Match(text ?? "", @"(?:1034%3A|1034:)([0-9]{12,})", RegexOptions.IgnoreCase);
            return videoId.Success && Uri.TryCreate("https://weibo.com/tv/show/1034:" + videoId.Groups[1].Value,
                UriKind.Absolute, out evidenceUri);
        }

        internal static string ExtractMetaDescription(string html)
        {
            string source = html ?? "";
            Match match = Regex.Match(source,
                @"<meta\b(?=[^>]*\bname\s*=\s*[""']description[""'])(?=[^>]*\bcontent\s*=\s*[""'](?<value>[^""']*)[""'])[^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success
                ? CleanText(WebUtility.HtmlDecode(match.Groups["value"].Value), 1200)
                : "";
        }

        private async Task<RemoteEvidenceResponse> TryRemoteEvidenceAsync(string endpoint, Uri target,
            CancellationToken token)
        {
            try
            {
                HttpResponseMessage response;
                if (endpoint.IndexOf("{url}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string requestUrl = Regex.Replace(endpoint, "\\{url\\}",
                        Uri.EscapeDataString(target.AbsoluteUri), RegexOptions.IgnoreCase);
                    response = await _remoteEvidenceClient.GetAsync(requestUrl, token);
                }
                else
                {
                    string payload = new JavaScriptSerializer().Serialize(new { url = target.AbsoluteUri });
                    response = await _remoteEvidenceClient.PostAsync(endpoint,
                        new StringContent(payload, Encoding.UTF8, "application/json"), token);
                }
                using (response)
                {
                    if (!response.IsSuccessStatusCode) return new RemoteEvidenceResponse
                    {
                        Error = "远程节点返回 HTTP " + (int)response.StatusCode,
                        Source = endpoint
                    };
                    string body = await ReadLimitedBodyAsync(response.Content, Math.Min(_bodyBytes, 1000000), token);
                    RemoteEvidenceResponse parsed = new JavaScriptSerializer { MaxJsonLength = 1200000 }
                        .Deserialize<RemoteEvidenceResponse>(body);
                    if (parsed == null) parsed = new RemoteEvidenceResponse { Error = "远程节点响应无法解析" };
                    if (String.IsNullOrWhiteSpace(parsed.Source)) parsed.Source = endpoint;
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                return new RemoteEvidenceResponse { Error = FriendlyError(ex), Source = endpoint };
            }
        }

        private async Task<RemoteEvidenceResponse> TryPublicCloudEvidenceAsync(Uri target,
            CancellationToken token)
        {
            await PublicCloudProbeGate.WaitAsync(token);
            try
            {
                int delay;
                lock (PublicCloudProbeTimingSync)
                {
                    delay = Math.Max(0,
                        (int)(_lastPublicCloudProbeStartedUtc.AddMilliseconds(3200) - DateTime.UtcNow).TotalMilliseconds);
                }
                if (delay > 0) await Task.Delay(delay, token);
                lock (PublicCloudProbeTimingSync) _lastPublicCloudProbeStartedUtc = DateTime.UtcNow;

                Uri readerUrl = new Uri("https://r.jina.ai/" + target.AbsoluteUri);
                using (var request = new HttpRequestMessage(HttpMethod.Get, readerUrl))
                {
                    request.Headers.TryAddWithoutValidation("x-no-cache", "true");
                    request.Headers.TryAddWithoutValidation("x-engine", "browser");
                    request.Headers.TryAddWithoutValidation("x-timeout", "18");
                    request.Headers.TryAddWithoutValidation("x-max-tokens", "12000");
                    using (HttpResponseMessage response = await _remoteEvidenceClient.SendAsync(request, token))
                {
                    string body = await ReadLimitedBodyAsync(response.Content,
                        Math.Min(_bodyBytes, 1000000), token);
                    if (response.IsSuccessStatusCode)
                    {
                        Match title = Regex.Match(body ?? "", @"(?im)^Title:\s*(.+?)\s*$");
                        Match source = Regex.Match(body ?? "", @"(?im)^URL Source:\s*(.+?)\s*$");
                        return new RemoteEvidenceResponse
                        {
                            Status = 200,
                            FinalUrl = source.Success ? source.Groups[1].Value.Trim() : target.AbsoluteUri,
                            Title = title.Success ? title.Groups[1].Value.Trim() : "",
                            Text = body ?? "",
                            Source = "Jina Reader 公开云取证"
                        };
                    }

                    bool targetUnreachable = (int)response.StatusCode == 422 &&
                        Regex.IsMatch(body ?? "",
                            "ERR_(?:EMPTY_RESPONSE|CONNECTION|TIMED_OUT|CONNECTION_RESET)|Failed to goto|RemoteDisconnected|urlopen error timed out",
                            RegexOptions.IgnoreCase);
                    return new RemoteEvidenceResponse
                    {
                        Error = targetUnreachable
                            ? "云浏览器连接目标失败（HTTP " + (int)response.StatusCode + "）：" +
                                ExecutionLogWriter.Safe(body, 300)
                            : "公开云取证服务返回 HTTP " + (int)response.StatusCode,
                        Source = "Jina Reader 公开云取证",
                        TargetUnreachable = targetUnreachable
                    };
                }
                }
            }
            catch (Exception ex)
            {
                return new RemoteEvidenceResponse
                {
                    Error = "公开云取证服务调用失败：" + FriendlyError(ex),
                    Source = "Jina Reader 公开云取证",
                    TargetUnreachable = false
                };
            }
            finally { PublicCloudProbeGate.Release(); }
        }

        internal static bool ShouldMarkPubliclyUnavailable(CheckResult result,
            RemoteEvidenceResponse publicCloud)
        {
            if (result == null || publicCloud == null || !publicCloud.TargetUnreachable) return false;
            if (!NetworkRestrictionCircuitBreaker.IsTransientRestriction(result)) return false;
            return !String.IsNullOrWhiteSpace(result.SiteHealth) &&
                !String.Equals(result.SiteHealth, "站点首页可访问", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ApplyRemoteEvidence(CheckResult result, RemoteEvidenceResponse remote,
            string source, List<VerificationEvidence> trail)
        {
            Uri requested;
            if (result == null || !Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out requested)) return false;
            return ApplyRemoteEvidence(result, remote, source, trail, requested);
        }

        private static bool ApplyRemoteEvidence(CheckResult result, RemoteEvidenceResponse remote,
            string source, List<VerificationEvidence> trail, Uri requested)
        {
            if (result == null || remote == null || !String.IsNullOrWhiteSpace(remote.Error)) return false;
            string body = !String.IsNullOrWhiteSpace(remote.Html) ? remote.Html :
                "<title>" + WebUtility.HtmlEncode(remote.Title ?? "") + "</title><main>" +
                WebUtility.HtmlEncode(remote.Text ?? "") + "</main>";
            var probe = new ProbeResponse
            {
                Status = remote.Status,
                Body = body,
                FinalUrl = String.IsNullOrWhiteSpace(remote.FinalUrl) ? result.OriginalUrl : remote.FinalUrl
            };
            if (requested == null) return false;
            return ApplyProbeEvidence(result, probe, source, requested, trail);
        }

        private async Task<HttpResponseMessage> SendWithFallbackAsync(Uri uri, CancellationToken token)
        {
            using (var fallbackBudget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                fallbackBudget.CancelAfter(TimeSpan.FromSeconds(18));
                return await SendWithFallbackWithinBudgetAsync(uri, token, fallbackBudget.Token);
            }
        }

        private async Task<HttpResponseMessage> SendWithFallbackWithinBudgetAsync(Uri uri, CancellationToken callerToken, CancellationToken fallbackToken)
        {
            var candidates = new List<Uri> { uri };
            if (uri != null && uri.Scheme == Uri.UriSchemeHttp)
            {
                try
                {
                    var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps };
                    if (uri.IsDefaultPort || uri.Port == 80) builder.Port = -1;
                    candidates.Add(builder.Uri);
                }
                catch { }
            }

            HttpResponseMessage retainedResponse = null;
            var errors = new List<Exception>();
            try
            {
                foreach (HttpClient client in new[] { _client, _directClient })
                {
                    foreach (Uri candidate in candidates)
                    {
                        SendAttempt attempt = await TrySendClientAsync(client, candidate, fallbackToken);
                        if (attempt.Response != null)
                        {
                            if (!IsRetryableTransportStatus(attempt.Response, candidate))
                            {
                                if (retainedResponse != null) retainedResponse.Dispose();
                                return attempt.Response;
                            }
                            if (retainedResponse == null) retainedResponse = attempt.Response;
                            else attempt.Response.Dispose();
                        }
                        if (attempt.Error != null) errors.Add(attempt.Error);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (callerToken.IsCancellationRequested)
                {
                    if (retainedResponse != null) retainedResponse.Dispose();
                    throw;
                }
                if (retainedResponse != null) return retainedResponse;
                throw new TaskCanceledException("代理和直连重试超过 18 秒");
            }

            if (retainedResponse != null) return retainedResponse;
            Exception error = errors.LastOrDefault();
            if (fallbackToken.IsCancellationRequested && !callerToken.IsCancellationRequested)
                throw new TaskCanceledException("代理和直连重试超过 18 秒");
            if (error is TaskCanceledException) throw (TaskCanceledException)error;
            if (error is HttpRequestException) throw (HttpRequestException)error;
            throw new HttpRequestException("HTTP/HTTPS 的系统代理和直连均无法建立连接", error);
        }

        private static async Task<SendAttempt> TrySendClientAsync(HttpClient client, Uri uri, CancellationToken token)
        {
            try
            {
                await WaitForRequestSlotAsync(uri, token);
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                    return new SendAttempt { Response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token) };
            }
            catch (OperationCanceledException ex)
            {
                if (token.IsCancellationRequested) throw;
                return new SendAttempt { Error = ex };
            }
            catch (Exception ex) { return new SendAttempt { Error = ex }; }
        }

        private static bool IsRetryableTransportStatus(HttpResponseMessage response, Uri candidate)
        {
            if (response == null) return true;
            int code = (int)response.StatusCode;
            if (code == 407 || code == 408 || code == 426 || code == 502 || code == 503 || code == 504) return true;
            return candidate != null && candidate.Scheme == Uri.UriSchemeHttp && (code == 400 || code == 403);
        }

        private static async Task<string> ReadLimitedBodyAsync(HttpContent content, int maxBytes, CancellationToken token)
        {
            using (var stream = await content.ReadAsStreamAsync())
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[16384];
                int total = 0;
                while (total < maxBytes)
                {
                    int wanted = Math.Min(buffer.Length, maxBytes - total);
                    int read = await stream.ReadAsync(buffer, 0, wanted, token);
                    if (read <= 0) break;
                    memory.Write(buffer, 0, read);
                    total += read;
                }
                byte[] bytes = memory.ToArray();
                Encoding encoding = DetectEncoding(content, bytes);
                return encoding.GetString(bytes);
            }
        }

        private static Encoding DetectEncoding(HttpContent content, byte[] bytes)
        {
            try
            {
                string charset = content.Headers.ContentType == null ? null : content.Headers.ContentType.CharSet;
                if (!String.IsNullOrWhiteSpace(charset)) return Encoding.GetEncoding(charset.Trim('"', '\'', ' '));
            }
            catch { }
            string preview = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            var match = Regex.Match(preview, "charset\\s*=\\s*[\\\"']?([a-zA-Z0-9_\\-]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                try { return Encoding.GetEncoding(match.Groups[1].Value); } catch { }
            }
            return new UTF8Encoding(false, false);
        }

        internal static string ExtractTitle(string html)
        {
            if (String.IsNullOrEmpty(html)) return "";
            var match = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            string title = match.Success ? CleanText(WebUtility.HtmlDecode(match.Groups[1].Value), 160) : "";
            if (!LooksGenericTitle(title)) return title;

            foreach (string pattern in new[]
            {
                @"<meta[^>]+(?:property|name)\s*=\s*[""'](?:og:title|twitter:title)[""'][^>]+content\s*=\s*[""']([^""']+)[""']",
                @"<meta[^>]+content\s*=\s*[""']([^""']+)[""'][^>]+(?:property|name)\s*=\s*[""'](?:og:title|twitter:title)[""']",
                @"(?:msg_title|article_title)\s*=\s*[""']([^""']+)[""']"
            })
            {
                match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!match.Success) continue;
                string candidate = CleanText(WebUtility.HtmlDecode(DecodeJsonUnicode(match.Groups[1].Value)), 160);
                if (!LooksGenericTitle(candidate)) return candidate;
            }
            return title;
        }

        internal static string ExtractVisibleText(string html)
        {
            if (String.IsNullOrEmpty(html)) return "";
            string text = Regex.Replace(html, "<(script|style|svg|noscript)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = Regex.Replace(text, "<!--.*?-->", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, "<[^>]+>", " ");
            text = WebUtility.HtmlDecode(text);
            return CleanText(text, 120000);
        }

        private static string ExtractProbableMainContentHtml(string html)
        {
            if (String.IsNullOrEmpty(html)) return "";
            var candidates = new List<string>();
            foreach (string pattern in new[]
            {
                @"<article\b[^>]*>.*?</article>",
                @"<main\b[^>]*>.*?</main>",
                "<[^>]+(?:class|id)\\s*=\\s*[\\\"'][^\\\"']*(?:article|post-body|article-content|detail-content|main-content|正文|error|empty|not-found)[^\\\"']*[\\\"'][^>]*>.*?</[^>]+>"
            })
            {
                foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
                    if (match.Value.Length >= 30) candidates.Add(match.Value);
            }
            if (candidates.Count == 0) return "";
            string best = candidates.OrderByDescending(candidate => ScoreMainContentCandidate(candidate)).First();
            return best.Substring(0, Math.Min(220000, best.Length));
        }

        private static string ExtractProbableMainContentText(string html)
        {
            string mainHtml = ExtractProbableMainContentHtml(html);
            return String.IsNullOrEmpty(mainHtml) ? "" : ExtractVisibleText(mainHtml);
        }

        private static int ScoreMainContentCandidate(string html)
        {
            string lower = (html ?? "").ToLowerInvariant();
            int textLength = ExtractVisibleText(html).Length;
            int score = Math.Min(textLength, 12000);
            if (lower.StartsWith("<article", StringComparison.Ordinal)) score += 8000;
            if (lower.StartsWith("<main", StringComparison.Ordinal)) score += 5000;
            if (lower.Contains("article-content") || lower.Contains("post-body") || lower.Contains("正文")) score += 5000;
            if (lower.Contains("comment") || lower.Contains("评论") || lower.Contains("recommend") || lower.Contains("推荐") ||
                lower.Contains("sidebar") || lower.Contains("footer") || lower.Contains("nav")) score -= 9000;
            return score;
        }

        private static string CleanText(string text, int max)
        {
            text = Regex.Replace(text ?? "", "\\s+", " ").Trim();
            return text.Length > max ? text.Substring(0, max) : text;
        }

        private static string FindSignal(string text, string[] signals)
        {
            foreach (string signal in signals)
                if (text.IndexOf(signal.ToLowerInvariant(), StringComparison.Ordinal) >= 0) return signal;
            return "";
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return !String.IsNullOrEmpty(first) ? first : (second ?? "");
        }

        private static bool LooksLikeLogin(string finalUrl)
        {
            if (String.IsNullOrWhiteSpace(finalUrl)) return false;
            string lower = finalUrl.ToLowerInvariant();
            return lower.Contains("/login") || lower.Contains("/signin") || lower.Contains("passport.") || lower.Contains("verify") || lower.Contains("captcha");
        }

        private static bool LooksLikeHomepageRedirect(Uri original, string finalUrl)
        {
            Uri finalUri;
            if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out finalUri)) return false;
            string originalPath = original.AbsolutePath.Trim('/');
            string finalPath = finalUri.AbsolutePath.Trim('/');
            bool sameHost = SamePlatformHost(original.Host, finalUri.Host);
            return sameHost && originalPath.Length > 5 && finalPath.Length == 0;
        }

        private static bool LooksLikePlatformRemovalRedirect(Uri original, string finalUrl)
        {
            Uri finalUri;
            if (original == null || !Uri.TryCreate(finalUrl, UriKind.Absolute, out finalUri)) return false;
            if (!SamePlatformHost(original.Host, finalUri.Host) || LooksLikeLogin(finalUrl)) return false;

            string host = original.Host.ToLowerInvariant();
            if ((host == "toutiao.com" || host.EndsWith(".toutiao.com", StringComparison.Ordinal)) &&
                LooksLikeHomepageRedirect(original, finalUrl)) return true;

            if (host == "yoojia.com" || host.EndsWith(".yoojia.com", StringComparison.Ordinal))
            {
                Match id = Regex.Match(original.AbsolutePath ?? "", @"([0-9]{8,})");
                if (id.Success && finalUri.AbsoluteUri.IndexOf(id.Groups[1].Value, StringComparison.OrdinalIgnoreCase) < 0)
                    return true;
            }
            return false;
        }

        internal static Uri SelectPlatformProbeUri(Uri original, string finalUrl)
        {
            Uri redirected;
            if (original == null || !Uri.TryCreate(finalUrl, UriKind.Absolute, out redirected)) return original;
            if (redirected.Scheme != Uri.UriSchemeHttp && redirected.Scheme != Uri.UriSchemeHttps) return original;
            if (!SamePlatformHost(original.Host, redirected.Host)) return original;
            return redirected;
        }

        private static bool SamePlatformHost(string firstHost, string secondHost)
        {
            string first = NormalizePlatformHost(firstHost);
            string second = NormalizePlatformHost(secondHost);
            if (first.Length > 0 && String.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
            return PlatformRules.AreSamePlatform(firstHost, secondHost);
        }

        internal static bool IsAuthoritativeTargetHttpRemoval(Uri original, Uri final)
        {
            if (original == null || final == null || LooksLikeLogin(final.AbsoluteUri)) return false;
            return SamePlatformHost(original.Host, final.Host);
        }

        private static string NormalizePlatformHost(string host)
        {
            string value = (host ?? "").Trim().Trim('.').ToLowerInvariant();
            foreach (string prefix in new[] { "www.", "m.", "wap.", "mobile." })
                if (value.StartsWith(prefix, StringComparison.Ordinal)) { value = value.Substring(prefix.Length); break; }
            return value;
        }

        private static bool LooksLikeErrorPage(string finalUrl, string title, string visible)
        {
            string lowerUrl = (finalUrl ?? "").ToLowerInvariant();
            string lowerTitle = (title ?? "").ToLowerInvariant();
            string start = CleanText(visible, 260).ToLowerInvariant();
            if (lowerUrl.Contains("babyhome.htm") || lowerUrl.Contains("/404") || lowerUrl.Contains("404.html") ||
                lowerUrl.Contains("/newspage/data/error") || lowerUrl.Contains("notfound") ||
                lowerUrl.Contains("not-found") || lowerUrl.Contains("errorpage") || lowerUrl.Contains("hotnewsshare404") ||
                (lowerUrl.Contains("eastmoney.com/error") && lowerUrl.Contains("type=2"))) return true;
            if (Regex.IsMatch(lowerTitle, @"(^|\s)404(\s|$)") || lowerTitle.Contains("page not found") ||
                lowerTitle.Contains("页面找不到") || lowerTitle.Contains("页面不存在")) return true;
            return (start.StartsWith("404") || start.Contains(" 404 ")) &&
                (start.Contains("找不到") || start.Contains("不存在") || start.Contains("not found"));
        }

        private static bool IsStrongPlatformEmptyState(string originalUrl, string finalUrl, string expectedTitle, string title, string visible, string html)
        {
            Uri original;
            if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out original)) return false;
            string all = (title ?? "") + " " + (visible ?? "");
            if (MatchesExpectedTitle(expectedTitle, all)) return false;

            string host = original.Host.ToLowerInvariant();
            string compactStart = CleanText(visible, 800);
            if ((host == "mbd.baidu.com" || host.EndsWith(".mbd.baidu.com", StringComparison.Ordinal)) &&
                ((finalUrl ?? "").IndexOf("/newspage/data/error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 compactStart.IndexOf("这里空空如也", StringComparison.Ordinal) >= 0)) return true;

            if ((host == "haokan.baidu.com" || host.EndsWith(".haokan.baidu.com", StringComparison.Ordinal)) &&
                Regex.IsMatch(original.Query ?? "", @"(?:^|[?&])vid=([0-9]{8,})", RegexOptions.IgnoreCase))
            {
                string videoId = Regex.Match(original.Query ?? "", @"(?:^|[?&])vid=([0-9]{8,})", RegexOptions.IgnoreCase).Groups[1].Value;
                if (IsHaokanErrorResponse(html, videoId)) return true;
            }

            if (host == "caifuhao.eastmoney.com" || host.EndsWith(".caifuhao.eastmoney.com", StringComparison.Ordinal))
            {
                bool explicitDeletedArticle = Regex.IsMatch(html ?? "",
                    "class\\s*=\\s*[\\\"']empty[\\\"'][^>]*>.*?抱歉，?该文章已被删除.*?页面将自动返回",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (explicitDeletedArticle) return true;
            }

            return false;
        }

        private static bool IsExplicitTargetRemovalPage(string signal, string finalUrl, string title, string visible, string html,
            string mainText, string mainHtml)
        {
            if (String.IsNullOrEmpty(signal)) return false;
            Uri finalUri;
            if (Uri.TryCreate(finalUrl, UriKind.Absolute, out finalUri) &&
                (finalUri.Host.Equals("caifuhao.eastmoney.com", StringComparison.OrdinalIgnoreCase) ||
                 finalUri.Host.EndsWith(".caifuhao.eastmoney.com", StringComparison.OrdinalIgnoreCase)) &&
                Regex.IsMatch(html ?? "",
                    "class\\s*=\\s*[\\\"']empty[\\\"'][^>]*>.*?抱歉，?该文章已被删除.*?页面将自动返回",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)) return true;
            string normalizedTitle = NormalizeForMatch(title);
            string normalizedSignal = NormalizeForMatch(signal);
            string start = NormalizeForMatch(CleanText(visible, 500));
            string normalizedMain = NormalizeForMatch(mainText);
            bool titleSignal = normalizedTitle.Contains(normalizedSignal);
            bool mainSignal = normalizedMain.Contains(normalizedSignal);
            bool signalIsProminent = titleSignal || start.StartsWith(normalizedSignal, StringComparison.Ordinal) ||
                (start.IndexOf(normalizedSignal, StringComparison.Ordinal) >= 0 && start.IndexOf(normalizedSignal, StringComparison.Ordinal) <= 35);
            bool mainHasArticleBody = HasArticleBodyStructure(mainHtml, mainText) && normalizedMain.Length >= normalizedSignal.Length + 90;

            // A deletion prompt in the selected article/main region is target-specific. A prompt found only
            // in comments, recommendations or overlays must not overrule a readable article body.
            if (mainSignal && !mainHasArticleBody) return true;
            if (!signalIsProminent) return false;

            string lowerUrl = (finalUrl ?? "").ToLowerInvariant();
            string[] provenPhrases =
            {
                "该文章已不存在", "出错了文章没有找到哦", "您访问的文章走失了", "您访问的页面已经不存在",
                "微博不存在或暂无查看权限", "抱歉此微博已被删除", "抱歉该文章已被删除", "没有知识存在的荒原", "这里空空如也"
            };
            bool provenPhrase = provenPhrases.Any(item => normalizedSignal.Contains(NormalizeForMatch(item)) ||
                start.Contains(NormalizeForMatch(item)) || normalizedTitle.Contains(NormalizeForMatch(item)));
            if (provenPhrase || lowerUrl.Contains("/404") || lowerUrl.Contains("hotnewsshare404") ||
                (lowerUrl.Contains("eastmoney.com/error") && lowerUrl.Contains("type=2"))) return true;

            // Many platforms use a sparse empty-state page with a generic deletion phrase. It remains a
            // direct removal result when the whole page is short and no article body is rendered.
            return visible.Length <= 1600 && !HasArticleBodyStructure(html, visible);
        }

        internal static bool MatchesExpectedTitle(string expectedTitle, string pageText)
        {
            string expected = NormalizeForMatch(expectedTitle);
            string page = NormalizeForMatch(pageText);
            if (expected.Length < 5 || page.Length == 0) return false;
            if (page.Contains(expected)) return true;

            int window = expected.Length >= 24 ? 12 : (expected.Length >= 14 ? 10 : Math.Max(6, expected.Length - 2));
            if (expected.Length < window) return false;
            int matches = 0;
            int tested = 0;
            int limit = Math.Min(expected.Length - window, 80);
            for (int position = 0; position <= limit; position += Math.Max(3, window / 2))
            {
                tested++;
                if (page.Contains(expected.Substring(position, window))) matches++;
                if (matches >= (expected.Length >= 24 ? 2 : 1)) return true;
            }
            return tested > 0 && matches >= Math.Min(2, tested);
        }

        internal static bool MatchesExpectedAuthor(string expectedAuthor, string pageText)
        {
            string expected = NormalizeForMatch(expectedAuthor);
            string page = NormalizeForMatch(pageText);
            if (expected.Length < 2 || page.Length == 0) return false;
            return page.Contains(expected);
        }

        internal static string NormalizeVisibleVerdict(string verdict)
        {
            return verdict == "已失效" || verdict == "仍可访问" || verdict == "人工复核" ||
                verdict == "暂时异常" || verdict == "疑似已处置" || verdict == "公网不可访问"
                ? verdict : "人工复核";
        }

        private static bool MatchesExpectedContent(string expectedTitle, string expectedExcerpt, string pageText)
        {
            if (MatchesExpectedExcerpt(expectedExcerpt, pageText)) return true;
            return MatchesExpectedTitle(expectedTitle, pageText);
        }

        private static bool MatchesExpectedShortContent(string expectedTitle, string expectedExcerpt, string pageText)
        {
            if (MatchesExpectedExcerpt(expectedExcerpt, pageText)) return true;
            string expected = NormalizeForMatch(expectedTitle);
            string page = NormalizeForMatch(pageText);
            if (expected.Length < 5 || page.Length == 0) return false;
            if (page.Contains(expected)) return true;
            return expected.Length >= 24 && MatchesExpectedTitle(expectedTitle, pageText);
        }

        private static bool HasReliablePageTitleIdentity(string expectedTitle, string pageTitle, string visible, Uri original, string finalUrl)
        {
            string expected = NormalizeForMatch(expectedTitle);
            string actual = NormalizeForMatch(pageTitle);
            bool exactTitleMatch = actual.Contains(expected);
            bool fuzzyTitleMatch = expected.Length >= 6 && MatchesExpectedTitle(expectedTitle, pageTitle);
            if (expected.Length < 3 || actual.Length < 3 || (!exactTitleMatch && !fuzzyTitleMatch) || LooksGenericTitle(pageTitle)) return false;
            int minimumVisible = exactTitleMatch ? 25 : 60;
            if (CleanText(visible, 120000).Length < minimumVisible || original == null || LooksLikeLogin(finalUrl)) return false;
            Uri final;
            if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out final) || !SamePlatformHost(original.Host, final.Host)) return false;
            if (LooksLikeHomepageRedirect(original, finalUrl) || LooksLikeErrorPage(finalUrl, pageTitle, visible)) return false;

            List<string> identities = ExtractContentIdentityTokens(original.AbsoluteUri);
            if (identities.Any(token => final.AbsoluteUri.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return original.AbsolutePath.Trim('/').Length >= 5 && final.AbsolutePath.Trim('/').Length >= 5;
        }

        private static bool MatchesExpectedExcerpt(string expectedExcerpt, string pageText)
        {
            string expected = NormalizeForMatch(expectedExcerpt);
            string page = NormalizeForMatch(pageText);
            if (expected.Length < 12 || page.Length < 20) return false;
            if (expected.Length <= 80 && page.Contains(expected)) return true;

            int window = expected.Length >= 80 ? 18 : (expected.Length >= 40 ? 16 : 12);
            int required = expected.Length >= 55 ? 2 : 1;
            int matches = 0;
            var tested = new HashSet<string>(StringComparer.Ordinal);
            int usableLength = Math.Min(expected.Length, 360);
            int step = Math.Max(window, usableLength / 5);
            for (int position = 0; position + window <= usableLength; position += step)
            {
                string fragment = expected.Substring(position, window);
                if (!IsUsefulExcerptFragment(fragment) || !tested.Add(fragment)) continue;
                if (page.Contains(fragment)) matches++;
                if (matches >= required) return true;
            }
            return false;
        }

        private static bool IsUsefulExcerptFragment(string fragment)
        {
            if (String.IsNullOrEmpty(fragment)) return false;
            string[] boilerplate = { "点击查看", "打开客户端", "下载app", "登录后查看", "更多精彩内容", "免责声明", "本文来源", "责任编辑" };
            return !boilerplate.Any(item => fragment.IndexOf(NormalizeForMatch(item), StringComparison.Ordinal) >= 0);
        }

        private static bool HasStrongRenderedContentIdentity(CheckResult result, RenderedPageData page, bool expectedMatch)
        {
            if (result == null || page == null) return false;
            string visible = CleanText(page.Text, 120000);
            string html = page.Html ?? "";
            string title = page.Title ?? "";
            string mainText = String.IsNullOrWhiteSpace(page.MainText) ? visible : CleanText(page.MainText, 120000);
            string mainHtml = String.IsNullOrWhiteSpace(page.MainHtml) ? html : page.MainHtml;
            if (visible.Length < 20 || LooksLikeErrorPage(page.Url, title, visible)) return false;

            bool excerptMatch = MatchesExpectedExcerpt(result.ExpectedExcerpt, title + " " + mainText);
            bool titleMatch = MatchesExpectedTitle(result.ExpectedTitle, title + " " + mainText);
            bool shortContentMatch = MatchesExpectedShortContent(result.ExpectedTitle, result.ExpectedExcerpt, title + " " + mainText);
            bool authorMatch = MatchesExpectedAuthor(result.ExpectedAuthor, title + " " + mainText);
            bool structure = HasArticleBodyStructure(mainHtml, mainText);
            bool socialPost = IsSocialPostPlatform(result.OriginalUrl);
            bool meaningfulBody = mainText.Length >= 80 && (!LooksGenericTitle(title) || socialPost);
            List<string> identities = ExtractContentIdentityTokens(result.OriginalUrl);
            bool identityInPage = identities.Any(token =>
                mainHtml.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                mainText.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
            bool identityPreserved = identities.Any(token =>
                (page.Url ?? "").IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
            bool socialBodyIdentity = socialPost && identityPreserved && HasSocialPostBodyStructure(mainHtml, mainText);

            // 摘要中的多个正文片段比标题更可靠，即使页面同时叠加登录框或推荐区也优先确认原文仍在。
            if (excerptMatch && mainText.Length >= 60) return true;
            // 帖子、评论和视频常没有网页标题；内容编号、作者和采集正文片段同时命中时可确认目标内容。
            if (authorMatch && shortContentMatch && (identityInPage || identityPreserved) && mainText.Length >= 20) return true;
            if (titleMatch && meaningfulBody && (identityInPage || identityPreserved)) return true;
            if (!meaningfulBody || !structure) return false;
            if (titleMatch && mainText.Length >= 120) return true;
            if (identityInPage) return true;
            // 微博、雪球等帖子常把正文首句采集为“标题”，网页本身却只有平台通用标题。
            // 原帖子地址未跳转且主体正文结构完整时，不能因采集标题未命中而判为失效。
            if (socialBodyIdentity) return true;
            return expectedMatch && identityPreserved;
        }

        private static bool HasSparseSameHostArticleIdentity(CheckResult result, string title, string visible, string currentUrl)
        {
            if (result == null || String.IsNullOrWhiteSpace(result.ExpectedTitle) ||
                String.IsNullOrWhiteSpace(title) || LooksGenericTitle(title) ||
                !MatchesExpectedTitle(result.ExpectedTitle, title)) return false;
            Uri original;
            Uri current;
            if (!Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out original) ||
                !Uri.TryCreate(currentUrl, UriKind.Absolute, out current) ||
                !SamePlatformHost(original.Host, current.Host) || LooksLikeLogin(currentUrl) ||
                LooksLikeErrorPage(currentUrl, title, visible) || LooksLikeHomepageRedirect(original, currentUrl)) return false;
            string path = (original.AbsolutePath + " " + current.AbsolutePath).ToLowerInvariant();
            bool articleRoute = path.Contains("/article") || path.Contains("/news") ||
                path.Contains("/video") || path.Contains("/v/") || path.Contains("/detail");
            return articleRoute && original.AbsolutePath.Trim('/').Length >= 4 &&
                CleanText(visible, 120000).Length >= 20;
        }

        private static bool HasCrossDomainArticleIdentity(CheckResult result, string title,
            string visible, string html, string currentUrl)
        {
            if (result == null || String.IsNullOrWhiteSpace(result.ExpectedTitle)) return false;
            Uri original;
            Uri current;
            if (!Uri.TryCreate(result.OriginalUrl, UriKind.Absolute, out original) ||
                !Uri.TryCreate(currentUrl, UriKind.Absolute, out current)) return false;
            if (SamePlatformHost(original.Host, current.Host) || LooksLikeLogin(currentUrl) ||
                LooksLikeErrorPage(currentUrl, title, visible) || LooksLikeHomepageRedirect(original, currentUrl)) return false;

            string mainText = ExtractProbableMainContentText(html ?? "");
            if (String.IsNullOrWhiteSpace(mainText) || mainText.Length < 180)
                mainText = CleanText(visible, 120000);
            string identityText = (title ?? "") + " " + mainText;
            if (!MatchesExpectedContent(result.ExpectedTitle, result.ExpectedExcerpt, identityText)) return false;
            if (mainText.Length < 180) return false;

            // A cross-site redirect is positive evidence only when the destination
            // has article-like structure. Footer text such as "扫描二维码" or a
            // generic landing page must not turn a redirect into an available item.
            bool articleStructure = HasArticleBodyStructure(html ?? "", mainText) ||
                Regex.IsMatch(html ?? "", @"<(?:article|main)\b", RegexOptions.IgnoreCase) ||
                Regex.Matches(html ?? "", @"<p(?:\s|>)", RegexOptions.IgnoreCase).Count >= 2;
            bool articleMetadata = Regex.IsMatch(html ?? "",
                @"<meta\b[^>]*\b(?:property|name)\s*=\s*[""']og:type[""'][^>]*\bcontent\s*=\s*[""']article[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline) ||
                Regex.IsMatch(html ?? "",
                    @"<meta\b[^>]*\bcontent\s*=\s*[""']article[""'][^>]*\b(?:property|name)\s*=\s*[""']og:type[""']",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return articleStructure || (articleMetadata &&
                (ExtractMetaDescription(html ?? "").Length >= 120 || CleanText(visible, 120000).Length >= 260));
        }

        private static bool HasSocialPostBodyStructure(string html, string text)
        {
            string lowerHtml = (html ?? "").ToLowerInvariant();
            string visible = text ?? "";
            bool postRegion = lowerHtml.Contains("<article") || lowerHtml.Contains("status") ||
                lowerHtml.Contains("post") || lowerHtml.Contains("detail") || lowerHtml.Contains("content");
            int markers = 0;
            foreach (string marker in new[] { "作者", "发布于", "发布时间", "编辑于", "评论", "转发", "收藏", "分享" })
                if (visible.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) markers++;
            return postRegion && visible.Length >= 80 && markers >= 2;
        }

        private static bool IsSocialPostPlatform(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            string host = (uri.Host ?? "").ToLowerInvariant();
            return host == "xueqiu.com" || host.EndsWith(".xueqiu.com", StringComparison.Ordinal) ||
                host == "weibo.com" || host.EndsWith(".weibo.com", StringComparison.Ordinal) ||
                host == "weibo.cn" || host.EndsWith(".weibo.cn", StringComparison.Ordinal);
        }

        internal static string InferContentType(string platform, string url, string title)
        {
            string hint = ((platform ?? "") + " " + (url ?? "") + " " + (title ?? "")).ToLowerInvariant();
            if (hint.Contains("视频号") || hint.Contains("短视频") || hint.Contains("视频") ||
                hint.Contains("/video/") || hint.Contains("/short-video/") || hint.Contains("video/")) return "视频";
            if (hint.Contains("回答") || hint.Contains("/answer/") || hint.Contains("answer/")) return "回答";
            if (hint.Contains("评论") || hint.Contains("comment") || hint.Contains("reply")) return "评论";
            if (hint.Contains("帖子") || hint.Contains("动态") || hint.Contains("post") || hint.Contains("status") ||
                hint.Contains("雪球") || hint.Contains("股吧") || hint.Contains("微博")) return "帖子";
            if (hint.Contains("文章") || hint.Contains("新闻") || hint.Contains("article") || hint.Contains("news") ||
                hint.Contains("微信")) return "文章";
            return "未知";
        }

        private static bool HasContentStructure(string html, string visible)
        {
            string lowerHtml = (html ?? "").ToLowerInvariant();
            string text = visible ?? "";
            if (lowerHtml.Contains("<article") || lowerHtml.Contains("role=\"main\"") || lowerHtml.Contains("role='main'") ||
                Regex.IsMatch(lowerHtml, "(?:class|id)\\s*=\\s*[\\\"'][^\\\"']*(?:article|content|detail|正文|post-body|video-info)[^\\\"']*[\\\"']")) return true;
            int markers = 0;
            foreach (string marker in new[] { "作者", "发布于", "发布时间", "编辑于", "原创", "阅读", "点赞", "评论", "收藏", "分享" })
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) markers++;
            return text.Length >= 260 && markers >= 2;
        }

        private static bool HasArticleBodyStructure(string html, string visible)
        {
            string lowerHtml = (html ?? "").ToLowerInvariant();
            string text = visible ?? "";
            bool articleMarkup = lowerHtml.Contains("<article") || lowerHtml.Contains("article-content") ||
                lowerHtml.Contains("post-body") || lowerHtml.Contains("detail-content") || lowerHtml.Contains("正文");
            int markers = 0;
            foreach (string marker in new[] { "作者", "发布于", "发布时间", "编辑于", "原创", "阅读", "点赞", "评论", "收藏", "分享" })
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) markers++;
            return (articleMarkup && text.Length >= 90) || (text.Length >= 260 && markers >= 2);
        }

        private static List<string> ExtractContentIdentityTokens(string url)
        {
            var tokens = new List<string>();
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return tokens;
            var candidates = new List<string>();
            candidates.AddRange((uri.AbsolutePath ?? "").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (Match queryValue in Regex.Matches(uri.Query ?? "", @"(?:^|[?&])(?:id|aid|nid|mid|vid|contentid|articleid|answerid|docid)=([^&#]+)", RegexOptions.IgnoreCase))
                candidates.Add(Uri.UnescapeDataString(queryValue.Groups[1].Value));
            foreach (Match fragmentValue in Regex.Matches((uri.Fragment ?? "").TrimStart('#'), @"(?:^|[?&])(?:id|aid|nid|mid|vid|contentid|articleid|answerid|docid)=([^&#]+)", RegexOptions.IgnoreCase))
                candidates.Add(Uri.UnescapeDataString(fragmentValue.Groups[1].Value));
            foreach (string candidate in candidates)
            {
                Match match = Regex.Match(candidate ?? "", @"^([a-z0-9_-]{8,})(?:\.[a-z0-9]+)?$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string token = match.Groups[1].Value;
                    if (!(token.All(Char.IsDigit) && token.Length < 8) &&
                        !new[] { "article", "articles", "content", "detail", "video-detail", "short-video", "index.html" }
                            .Contains(token, StringComparer.OrdinalIgnoreCase) &&
                        !tokens.Contains(token, StringComparer.OrdinalIgnoreCase)) tokens.Add(token);
                }
                // 股吧等页面把正文编号放在 news,股票代码,正文编号.html 这类文件名中。
                foreach (Match embedded in Regex.Matches(candidate ?? "", @"(?<![0-9])([0-9]{8,})(?![0-9])"))
                {
                    string token = embedded.Groups[1].Value;
                    if (!tokens.Contains(token, StringComparer.OrdinalIgnoreCase)) tokens.Add(token);
                }
            }
            return tokens.Take(8).ToList();
        }

        private static string NormalizeForMatch(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            value = WebUtility.HtmlDecode(DecodeJsonUnicode(value)).ToLowerInvariant();
            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
                if (Char.IsLetterOrDigit(character)) builder.Append(character);
            return builder.ToString();
        }

        private static string DecodeJsonUnicode(string value)
        {
            if (String.IsNullOrEmpty(value) || value.IndexOf("\\u", StringComparison.OrdinalIgnoreCase) < 0) return value ?? "";
            return Regex.Replace(value, @"\\u([0-9a-fA-F]{4})", delegate(Match match)
            {
                int code;
                return Int32.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out code) ? ((char)code).ToString() : match.Value;
            });
        }

        internal static bool LooksGenericTitle(string title)
        {
            string normalized = (title ?? "").Trim().ToLowerInvariant();
            return normalized.Length == 0 || normalized == "好看视频-轻松有收获" || normalized.Contains("【一点资讯】") ||
                normalized == "今日头条" || normalized == "腾讯网" || normalized == "网易" || normalized == "搜狐" ||
                normalized == "uc头条" || normalized == "同花顺社区" || normalized == "短视频-快手" || normalized == "页面加载中...";
        }

        private static bool IsDynamicShellHost(string host)
        {
            string lower = (host ?? "").ToLowerInvariant();
            Uri uri;
            PlatformRule externalRule = Uri.TryCreate("https://" + lower.TrimStart('.'), UriKind.Absolute, out uri) ? PlatformRules.Find(uri) : null;
            return (externalRule != null && externalRule.DynamicShell) || lower.Contains("toutiao.com") || lower.Contains("haokan.baidu.com") || lower.Contains("yoojia.com") ||
                lower.Contains("yoojia.baidu.com") ||
                lower.Contains("xueqiu.com") || lower.Contains("yidianzixun.com") || lower.Contains("weishi.qq.com") ||
                lower.Contains("v.qq.com") || lower.Contains("xhslink.com") || lower.Contains("xiaohongshu.com");
        }

        private static bool IsDongchedi(Uri uri)
        {
            string host = uri == null ? "" : uri.Host.ToLowerInvariant();
            return host == "dongchedi.com" || host.EndsWith(".dongchedi.com", StringComparison.Ordinal) ||
                host == "dcdapp.com" || host.EndsWith(".dcdapp.com", StringComparison.Ordinal);
        }

        private static bool IsDongchediJavascriptShell(string html)
        {
            if (String.IsNullOrWhiteSpace(html)) return true;
            // DCD's anti-bot response is a large obfuscated script with an empty
            // body. Treating that script text as visible content prevents the
            // public-reader fallback from ever running.
            if (Regex.IsMatch(html, @"<body(?:\s[^>]*)?>\s*</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                return true;
            string withoutScripts = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
            withoutScripts = Regex.Replace(withoutScripts, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            withoutScripts = Regex.Replace(withoutScripts, @"<[^>]+>", " ");
            return String.IsNullOrWhiteSpace(WebUtility.HtmlDecode(withoutScripts));
        }

        private static bool IsWechatChannel(Uri uri)
        {
            string host = uri == null ? "" : uri.Host.ToLowerInvariant();
            return host.Contains("channels.weixin.qq.com") || host.Contains("finder.video.qq.com") ||
                host.Contains("wxapp.tc.qq.com");
        }

        private static bool IsXiaohongshu(Uri uri)
        {
            string host = uri == null ? "" : uri.Host.ToLowerInvariant();
            return host == "xhslink.com" || host.EndsWith(".xhslink.com", StringComparison.Ordinal) ||
                host == "xiaohongshu.com" || host.EndsWith(".xiaohongshu.com", StringComparison.Ordinal);
        }

        private static bool IsXiaohongshuUnavailableRedirect(string finalUrl)
        {
            if (String.IsNullOrWhiteSpace(finalUrl)) return false;
            string decoded = WebUtility.UrlDecode(finalUrl);
            return decoded.IndexOf("undertake_note_error=该内容暂时无法查看", StringComparison.OrdinalIgnoreCase) >= 0 ||
                decoded.IndexOf("undertake_note_error=%E8%AF%A5%E5%86%85%E5%AE%B9%E6%9A%82%E6%97%B6%E6%97%A0%E6%B3%95%E6%9F%A5%E7%9C%8B", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool CanInferRemovalFromRenderedPage(string finalUrl, string visible)
        {
            Uri uri;
            if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            // Toutiao's rendered article page contains the article title/body when it exists.
            // A fully rendered navigation shell with neither is therefore strong removal evidence.
            bool isContentAddress = Regex.IsMatch(uri.AbsolutePath ?? "", @"^/(?:article|video|w)/[0-9]+/?$", RegexOptions.IgnoreCase);
            return host.EndsWith("toutiao.com") && isContentAddress && visible.Length > 600 &&
                (visible.Contains("下载头条APP") || visible.Contains("发布作品"));
        }

        private static string FindBrowserPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")
            };
            string direct = candidates.FirstOrDefault(File.Exists);
            if (!String.IsNullOrEmpty(direct)) return direct;
            foreach (string registryPath in new[]
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe",
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"
            })
            {
                try
                {
                    string registered = Convert.ToString(Registry.GetValue(registryPath, "", ""));
                    if (File.Exists(registered)) return registered;
                }
                catch { }
            }
            try
            {
                foreach (string edgeRoot in new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeCore"),
                    Path.Combine(local, "Microsoft", "Edge")
                })
                {
                    if (!Directory.Exists(edgeRoot)) continue;
                    string discovered = Directory.GetFiles(edgeRoot, "msedge.exe", SearchOption.AllDirectories).FirstOrDefault(File.Exists);
                    if (!String.IsNullOrEmpty(discovered)) return discovered;
                }
            }
            catch { }
            return "";
        }

        private static async Task<BrowserSnapshot> RenderWithBrowserAsync(string url, CancellationToken token)
        {
            if (String.IsNullOrEmpty(BrowserPath)) return new BrowserSnapshot { Error = "browser-not-found" };
            await BrowserSemaphore.WaitAsync(token);
            string profile = Path.Combine(Path.GetTempPath(), "LinkDispositionChecker", "edge-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(profile);
                var start = new ProcessStartInfo
                {
                    FileName = BrowserPath,
                    Arguments = "--headless=new --disable-gpu --no-first-run --no-default-browser-check --disable-extensions " +
                        "--disable-background-networking --disable-blink-features=AutomationControlled --window-size=1280,900 --lang=zh-CN " +
                        "--user-agent=" + QuoteArgument("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36") +
                        " --virtual-time-budget=9000 --user-data-dir=" + QuoteArgument(profile) +
                        " --dump-dom " + QuoteArgument(url),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var process = new Process { StartInfo = start })
                {
                    if (!process.Start()) return new BrowserSnapshot { Error = "browser-start-failed" };
                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    Task<bool> waitTask = Task.Run(delegate { return process.WaitForExit(22000); }, token);
                    bool exited = await waitTask;
                    if (!exited)
                    {
                        try { process.Kill(); } catch { }
                        return new BrowserSnapshot { TimedOut = true, Error = "browser-timeout" };
                    }
                    string html = await outputTask;
                    string error = await errorTask;
                    return new BrowserSnapshot { Html = html ?? "", Error = ShortMessage(error) };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new BrowserSnapshot { Error = ShortMessage(ExceptionMessages(ex)) }; }
            finally
            {
                BrowserSemaphore.Release();
                try
                {
                    if (Directory.Exists(profile) && profile.StartsWith(Path.Combine(Path.GetTempPath(), "LinkDispositionChecker"), StringComparison.OrdinalIgnoreCase))
                        Directory.Delete(profile, true);
                }
                catch { }
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string FriendlyError(Exception exception)
        {
            string message = ExceptionMessages(exception);
            if (String.IsNullOrWhiteSpace(message)) return "无法建立连接，请人工复核";
            string lower = message.ToLowerInvariant();
            if (lower.Contains("name could not be resolved") || lower.Contains("不知道这样的主机") || lower.Contains("no such host")) return "域名无法解析，链接可能已失效";
            if (lower.Contains("proxy") || lower.Contains("代理")) return "系统代理连接失败，已同时尝试直连：" + ShortMessage(message);
            if (lower.Contains("ssl") || lower.Contains("certificate") || lower.Contains("证书")) return "HTTPS 证书异常，不能据此判定已处置";
            if (lower.Contains("actively refused") || lower.Contains("积极拒绝")) return "目标服务器拒绝连接，建议稍后复核";
            if (lower.Contains("timed out") || lower.Contains("超时")) return "系统代理和直连均超时，建议稍后重试";
            return "系统代理和直连均失败：" + ShortMessage(message);
        }

        private static string ExceptionMessages(Exception exception)
        {
            var messages = new List<string>();
            for (Exception current = exception; current != null && messages.Count < 4; current = current.InnerException)
                if (!String.IsNullOrWhiteSpace(current.Message)) messages.Add(current.Message.Replace("\r", " ").Replace("\n", " ").Trim());
            return String.Join(" | ", messages.Distinct());
        }

        private static string ShortMessage(string message)
        {
            message = Regex.Replace(message ?? "", "\\s+", " ").Trim();
            return message.Length > 180 ? message.Substring(0, 180) : message;
        }
    }

    internal sealed class DeepReviewForm : Form
    {
        private sealed class DeepPlatformProfile
        {
            public string Tier;
            public int MinimumWaitMilliseconds;
            public int MaximumWaitMilliseconds;
            public int NavigationTimeoutMilliseconds;
            public string Limitation;
        }

        private readonly List<CheckResult> _items;
        private readonly Action<CheckResult> _onProgress;
        private readonly WebView2 _browser = new WebView2();
        private readonly Panel _browserHost = new Panel();
        private CoreWebView2Environment _webViewEnvironment;
        private readonly Label _status = new Label();
        private readonly Label _counter = new Label();
        private readonly Button _continue = new Button();
        private readonly Button _stop = new Button();
        private readonly ComboBox _loginPlatforms = new ComboBox();
        private readonly Button _openLoginPlatform = new Button();
        private readonly Dictionary<string, string> _loginTargets = new Dictionary<string, string>();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 800000 };
        private CancellationTokenSource _cancellation = new CancellationTokenSource();
        private int _index;
        private bool _processing;
        private bool _alternateAttemptedForCurrent;
        private bool _loginPreparation = true;
        private bool _completionShown;
        private readonly bool _fastMode;
        private readonly bool _autoStart;
        private readonly HashSet<string> _pausedPlatformKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int ResolvedCount { get; private set; }

        public DeepReviewForm(IEnumerable<CheckResult> items, Action<CheckResult> onProgress,
            bool fastMode = false, bool autoStart = false)
        {
            _fastMode = fastMode;
            _autoStart = autoStart;
            IEnumerable<CheckResult> sourceItems = items ?? Enumerable.Empty<CheckResult>();
            _items = fastMode ? sourceItems.ToList() : sourceItems
                .OrderBy(GetReviewBatchOrder)
                .ThenBy(item => VerificationPlatformKey(item.OriginalUrl), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Number)
                .ToList();
            _onProgress = onProgress;
            Text = fastMode ? "内置浏览器快速复核" : "内置浏览器深度复核";
            StartPosition = FormStartPosition.CenterParent;
            if (_autoStart)
            {
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
            }
            MinimumSize = new Size(980, 680);
            Size = new Size(1180, 820);
            Font = new Font("微软雅黑", 9.5f);
            BackColor = Color.FromArgb(244, 247, 251);

            var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Color.FromArgb(27, 62, 111), Padding = new Padding(18, 10, 18, 10) };
            var title = new Label { Text = fastMode ? "内置浏览器快速复核" : "内置浏览器深度复核", ForeColor = Color.White, Font = new Font("微软雅黑", 16, FontStyle.Bold), AutoSize = true, Location = new Point(18, 8) };
            _counter.ForeColor = Color.FromArgb(206, 220, 239); _counter.AutoSize = true; _counter.Location = new Point(20, 46);
            header.Controls.Add(title); header.Controls.Add(_counter);

            var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 72, BackColor = Color.White, Padding = new Padding(14, 10, 14, 10), ColumnCount = 2, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 440));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _status.AutoEllipsis = true; _status.ForeColor = Color.FromArgb(55, 65, 81); _status.Dock = DockStyle.Fill; _status.Margin = new Padding(0, 0, 8, 0); _status.TextAlign = ContentAlignment.MiddleLeft;
            StyleDeepButton(_continue, "准备后台复核", true); StyleDeepButton(_stop, "停止", false);
            _continue.Enabled = false;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 4, 0, 0), Margin = new Padding(0) };
            buttons.Controls.Add(_continue); buttons.Controls.Add(_stop);
            footer.Controls.Add(_status, 0, 0); footer.Controls.Add(buttons, 1, 0);

            var loginBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, BackColor = Color.FromArgb(232, 240, 250), Padding = new Padding(16, 8, 16, 6), WrapContents = false };
            loginBar.Controls.Add(new Label { Text = "登录准备：", AutoSize = true, Margin = new Padding(0, 8, 6, 0), ForeColor = Color.FromArgb(55, 65, 81) });
            _loginPlatforms.DropDownStyle = ComboBoxStyle.DropDownList; _loginPlatforms.Width = 280; _loginPlatforms.Margin = new Padding(0, 3, 8, 0);
            StyleDeepButton(_openLoginPlatform, "打开所选平台", false); _openLoginPlatform.Width = 140;
            loginBar.Controls.Add(_loginPlatforms); loginBar.Controls.Add(_openLoginPlatform);

            _browserHost.Dock = DockStyle.Fill;
            _browser.Dock = DockStyle.Fill;
            _browserHost.Controls.Add(_browser);
            Controls.Add(_browserHost); Controls.Add(footer); Controls.Add(loginBar); Controls.Add(header);

            Shown += async delegate { await InitializeAndStartAsync(); };
            _continue.Click += async delegate { await ContinueAfterVerificationAsync(); };
            _openLoginPlatform.Click += async delegate { await OpenSelectedLoginPlatformAsync(); };
            _stop.Click += delegate { _cancellation.Cancel(); Close(); };
            FormClosing += delegate { if (!_cancellation.IsCancellationRequested) _cancellation.Cancel(); };
        }

        private static void StyleDeepButton(Button button, string text, bool primary)
        {
            button.Text = text; button.AutoSize = false; button.Size = new Size(primary ? 150 : 125, 36); button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.BackColor = primary ? Color.FromArgb(32, 92, 154) : Color.White;
            button.ForeColor = primary ? Color.White : Color.FromArgb(55, 65, 81);
            button.Margin = new Padding(6, 0, 0, 0);
        }

        private async Task InitializeAndStartAsync()
        {
            if (_items.Count == 0) { _status.Text = _fastMode ? "没有需要内置浏览器快速复核的链接。" : "没有需要深度复核的链接。"; return; }
            try
            {
                string userData = Environment.GetEnvironmentVariable("LINK_CHECKER_WEBVIEW_PROFILE");
                if (String.IsNullOrWhiteSpace(userData))
                    userData = Path.Combine(StoragePaths.UserDataDirectory, "WebView2Profile");
                Directory.CreateDirectory(userData);
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userData);
                _webViewEnvironment = environment;
                await _browser.EnsureCoreWebView2Async(environment);
                _browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                _browser.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
                PrepareLoginTargets();
                _continue.Text = _fastMode ? "开始内置浏览器复核（登录可选）" : "开始后台复核（登录可选）";
                _continue.Width = _fastMode ? 270 : 220;
                _continue.Enabled = true;
                _counter.Text = "准备开始，共 " + _items.Count + (_fastMode ? " 条待快速核验" : " 条待后台复核候选");
                _status.Text = _loginTargets.Count == 0
                    ? (_fastMode ? "可直接开始内置浏览器快速复核。" : "可直接点击“开始后台复核（登录可选）”。")
                    : (_fastMode ? "登录是可选项；也可直接开始，工具会先检查各平台公开页面状态。" : BuildReviewBatchSummary() + "。这些是后台复核候选，不是要求你逐条手动查看的数量；登录是可选项，开始后会自动继续下一条。");
                if (_loginPlatforms.Items.Count > 0) _loginPlatforms.SelectedIndex = 0;
                if (_autoStart) await ContinueAfterVerificationAsync();
            }
            catch (Exception ex)
            {
                _status.Text = "无法启动持久浏览器：" + ex.Message;
                if (_autoStart)
                {
                    foreach (CheckResult item in _items.Where(item =>
                        item.Verdict != "已失效" && item.Verdict != "仍可访问"))
                    {
                        item.EdgeFastReviewed = false;
                        item.DeepReviewed = false;
                        item.Evidence = "自动浏览器快速追证不可用：" + ex.Message;
                        NotifyProgress(item);
                    }
                    Close();
                }
                else
                {
                    MessageBox.Show(_status.Text + "\n\n请确认 Microsoft Edge WebView2 Runtime 已安装。", "深度核验不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private async Task ProcessCurrentAsync(bool navigate)
        {
            if (_processing || _cancellation.IsCancellationRequested) return;
            while (_index < _items.Count && _pausedPlatformKeys.Contains(VerificationPlatformKey(_items[_index].OriginalUrl)))
                _index++;
            if (_index >= _items.Count)
            {
                _counter.Text = "已完成 " + _items.Count + " / " + _items.Count;
            string completion = "后台复核完成，新增自动确认 " + ResolvedCount + " 条。" + BuildUnresolvedSummary();
                _status.Text = completion;
                _stop.Text = "关闭";
                _continue.Enabled = false;
                if (_autoStart)
                {
                    _completionShown = true;
                    Close();
                }
                else if (!_fastMode && !_completionShown)
                {
                    _completionShown = true;
                    WindowState = FormWindowState.Normal;
                    Activate();
                    MessageBox.Show(completion, "后台深度复核完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            _processing = true;
            _continue.Enabled = false;
            CheckResult item = _items[_index];
            _counter.Text = "正在复核 " + (_index + 1) + " / " + _items.Count + "    已新增确认 " + ResolvedCount + " 条";
            DeepPlatformProfile profile = GetDeepPlatformProfile(item.OriginalUrl);
            _status.Text = profile.Tier + "平台后台核验：" + item.OriginalUrl;

            try
            {
                if (navigate) await NavigateAsync(item.OriginalUrl, profile.NavigationTimeoutMilliseconds);
                RenderedPageData page = await ReadStablePageAsync(item);
                if (String.Equals(item.StatusCode, "Edge待核验", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(item.StatusCode, "浏览器待核验", StringComparison.OrdinalIgnoreCase)) item.StatusCode = "浏览器";
                DeepDecision decision = Checker.ClassifyRenderedPage(item, page);

                string alternateUrl;
                if (!decision.Resolved && !_alternateAttemptedForCurrent && TryBuildYoojiaAlternateUrl(item.OriginalUrl, out alternateUrl))
                {
                    _alternateAttemptedForCurrent = true;
                    _status.Text = "原页面无法确认，正在核验有驾的另一端页面……";
                    await NavigateAsync(alternateUrl, profile.NavigationTimeoutMilliseconds);
                    page = await ReadStablePageAsync(item);
                    decision = Checker.ClassifyRenderedPage(item, page);
                    if (decision.Resolved) decision.Evidence = "通过有驾备用页面确认：" + decision.Evidence;
                }

                if (decision.NeedsVerification)
                {
                    item.Verdict = "人工复核";
                    // A verification page is local to this item/session. Do not
                    // skip the remaining links from the same platform; each row
                    // may still resolve through its own public page or API.
                    bool pausePlatform = false;
                    item.Evidence = decision.Evidence + "；后台复核未停留等待，已自动继续下一条" +
                        (String.IsNullOrWhiteSpace(profile.Limitation) ? "" : "；平台限制：" + profile.Limitation);
                    item.CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    item.DeepReviewed = !pausePlatform;
                    _alternateAttemptedForCurrent = false;
                    _index++;
                    NotifyProgress(item);
                }
                else
                {
                    item.FinalUrl = String.IsNullOrEmpty(page.Url) ? item.FinalUrl : page.Url;
                    if (!String.IsNullOrEmpty(page.Title)) item.Title = page.Title;
                    if (decision.Resolved)
                    {
                        item.Verdict = Checker.NormalizeVisibleVerdict(decision.Verdict);
                        item.Evidence = decision.Evidence;
                        item.CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        if (item.Verdict == "已失效" || item.Verdict == "仍可访问") ResolvedCount++;
                    }
                    else
                    {
                        item.Verdict = "人工复核";
                        item.Evidence = decision.Evidence + "；平台分类：" + profile.Tier +
                            (String.IsNullOrWhiteSpace(profile.Limitation) ? "" : "；当前无法自动解决：" + profile.Limitation);
                    }
                    item.DeepReviewed = true;
                    _alternateAttemptedForCurrent = false;
                    _index++;
                    NotifyProgress(item);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                item.Verdict = "人工复核";
                if (String.Equals(item.StatusCode, "Edge待核验", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(item.StatusCode, "浏览器待核验", StringComparison.OrdinalIgnoreCase)) item.StatusCode = "浏览器失败";
                item.Evidence = "持久浏览器复核失败：" + ex.Message;
                item.DeepReviewed = true;
                _alternateAttemptedForCurrent = false;
                _index++;
                NotifyProgress(item);
            }
            finally { _processing = false; }

            if (!_cancellation.IsCancellationRequested) await ProcessCurrentAsync(true);
        }

        private void NotifyProgress(CheckResult item)
        {
            if (_onProgress == null) return;
            try { _onProgress(item); } catch { }
        }

        private static bool IsSecurityVerificationDecision(DeepDecision decision)
        {
            string evidence = decision == null ? "" : (decision.Evidence ?? "");
            return evidence.IndexOf("安全验证", StringComparison.OrdinalIgnoreCase) >= 0 ||
                evidence.IndexOf("验证码", StringComparison.OrdinalIgnoreCase) >= 0 ||
                evidence.IndexOf("访问过于频繁", StringComparison.OrdinalIgnoreCase) >= 0 ||
                evidence.IndexOf("操作频繁", StringComparison.OrdinalIgnoreCase) >= 0 ||
                evidence.IndexOf("captcha", StringComparison.OrdinalIgnoreCase) >= 0 ||
                evidence.IndexOf("verify", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<RenderedPageData> ReadStablePageAsync(CheckResult item)
        {
            DeepPlatformProfile profile = GetDeepPlatformProfile(item == null ? "" : item.OriginalUrl);
            int minimumWaitMs = profile.MinimumWaitMilliseconds;
            int maximumWaitMs = profile.MaximumWaitMilliseconds;
            var observed = new List<string>();
            var watch = Stopwatch.StartNew();
            RenderedPageData latest = new RenderedPageData();
            string lastSignature = "";
            int stableSamples = 0;

            while (watch.ElapsedMilliseconds < maximumWaitMs)
            {
                await Task.Delay(400, _cancellation.Token);
                latest = await ReadPageAsync();
                string currentUrl = latest.Url ?? "";
                if (!String.IsNullOrWhiteSpace(currentUrl) && !observed.Contains(currentUrl, StringComparer.OrdinalIgnoreCase)) observed.Add(currentUrl);
                string text = latest.Text ?? "";
                string signature = currentUrl + "\n" + (latest.Title ?? "") + "\n" + text.Length + "\n" +
                    (text.Length > 500 ? text.Substring(0, 250) + text.Substring(text.Length - 250) : text);
                if (String.Equals(signature, lastSignature, StringComparison.Ordinal)) stableSamples++;
                else { stableSamples = 0; lastSignature = signature; }

                DeepDecision currentDecision = Checker.ClassifyRenderedPage(item, latest);
                if (currentDecision.Resolved && currentDecision.Verdict == "仍可访问" && watch.ElapsedMilliseconds >= 700) break;
                if (watch.ElapsedMilliseconds >= minimumWaitMs && stableSamples >= 1 &&
                    (currentDecision.Resolved || currentDecision.NeedsVerification || (latest.Text ?? "").Length >= 80)) break;
            }
            latest.ObservedUrls = String.Join(" -> ", observed);
            return latest;
        }

        private async Task ContinueAfterVerificationAsync()
        {
            if (_loginPreparation)
            {
                _loginPreparation = false;
                _continue.Text = _fastMode ? "内置浏览器复核进行中" : "后台复核进行中";
                _continue.Width = 150;
                _continue.Enabled = false;
                _openLoginPlatform.Enabled = false;
                _loginPlatforms.Enabled = false;
                _status.Text = _fastMode
                    ? "正在通过内置 WebView2 浏览器获取页面；动态页面将保留给后续深度复核。"
                    : "正在按平台分批后台复核；先检查公开正文和作品状态，受限项会写明原因并自动继续。";
                WindowState = FormWindowState.Minimized;
                if (_fastMode) await ProcessFastItemsAsync();
                else await ProcessCurrentAsync(true);
                return;
            }
        }

        private async Task ProcessFastItemsAsync()
        {
            if (_processing || _cancellation.IsCancellationRequested) return;
            _processing = true;
            int next = -1;
            int completed = 0;
            int workers = 1;
            var probeQueue = new ConcurrentQueue<CheckResult>();
            var renderQueue = new Queue<CheckResult>();
            try
            {
                Task[] tasks = Enumerable.Range(0, Math.Min(workers, Math.Max(1, _items.Count))).Select(async workerNumber =>
                {
                    while (!_cancellation.IsCancellationRequested)
                    {
                        int index = Interlocked.Increment(ref next);
                        if (index >= _items.Count) break;
                        CheckResult item = _items[index];
                        try
                        {
                            EdgeFetchedResponse response = await LoadEdgeResourceAsync(_browser.CoreWebView2, item.OriginalUrl, 700000, _cancellation.Token);
                            if (_cancellation.IsCancellationRequested) break;
                            bool resolved = ApplyFastResponse(item, response);
                            if (!resolved && !IsResolvedVerdict(item.Verdict)) probeQueue.Enqueue(item);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            if (_cancellation.IsCancellationRequested) break;
                            item.Verdict = "人工复核";
                            item.StatusCode = "浏览器失败";
                            item.Evidence = "内置浏览器快速复核失败，已保留给深度复核：" + ex.Message;
                            item.EdgeFastReviewed = true;
                            item.DeepReviewed = false;
                            item.CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        }
                        NotifyProgress(item);
                        int done = Interlocked.Increment(ref completed);
                        if (!_cancellation.IsCancellationRequested && !IsDisposed)
                        {
                            _counter.Text = "内置浏览器快速复核 " + done + " / " + _items.Count + "    已确认 " + ResolvedCount + " 条";
                            _status.Text = "并发 " + workers + " 路，动态页面和证据不足项将留给深度复核";
                        }
                    }
                }).ToArray();
                await Task.WhenAll(tasks);
                if (_cancellation.IsCancellationRequested || IsDisposed) return;
                _status.Text = "原始响应阶段完成，正在用平台公开接口补充 " + probeQueue.Count + " 条";
                CheckResult pendingItem;
                while (probeQueue.TryDequeue(out pendingItem) && !_cancellation.IsCancellationRequested)
                {
                    try
                    {
                        if (await TryApplyEdgePlatformProbeAsync(_browser.CoreWebView2, pendingItem, _cancellation.Token)) ResolvedCount++;
                        else if (ShouldFastRenderPlatform(pendingItem)) renderQueue.Enqueue(pendingItem);
                        else pendingItem.Evidence = "内置浏览器网络与平台接口未取得足够证据；该平台不适合短渲染，保留给按需深度复核";
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        if (ShouldFastRenderPlatform(pendingItem)) renderQueue.Enqueue(pendingItem);
                    }
                    NotifyProgress(pendingItem);
                }
                _status.Text = "网络/接口阶段完成，正在短渲染剩余 " + renderQueue.Count + " 条";
                while (renderQueue.Count > 0 && !_cancellation.IsCancellationRequested)
                {
                    pendingItem = renderQueue.Dequeue();
                    try
                    {
                        RenderedPageData rendered = await ReadFastRenderedPageAsync(_browser, pendingItem.OriginalUrl, _cancellation.Token);
                        bool resolved = ApplyFastRenderedPage(pendingItem, rendered);
                        if (resolved) ResolvedCount++;
                        else if (IsPlatformSecurityPage(rendered))
                        {
                            pendingItem.Verdict = "人工复核";
                            pendingItem.EdgeFastReviewed = true;
                            pendingItem.DeepReviewed = false;
                            pendingItem.Evidence = "平台出现安全验证或访问频繁提示；仅保留当前链接待复核，快速核验继续检查同平台其他链接";
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        pendingItem.Verdict = "人工复核";
                        pendingItem.StatusCode = "浏览器失败";
                        pendingItem.Evidence = "内置浏览器短渲染失败，保留人工复核：" + ex.Message;
                    }
                    NotifyProgress(pendingItem);
                }
                _counter.Text = "内置浏览器快速复核完成 " + completed + " / " + _items.Count;
                string completion = "分平台快速复核完成，新增自动确认 " + ResolvedCount + " 条。" + BuildUnresolvedSummary();
                _status.Text = completion;
                _stop.Text = "关闭";
                if (_autoStart)
                {
                    _completionShown = true;
                    Close();
                }
                else if (!_completionShown)
                {
                    _completionShown = true;
                    WindowState = FormWindowState.Normal;
                    Activate();
                    MessageBox.Show(completion, "后台快速复核完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            finally { _processing = false; }
        }

        private bool ApplyFastResponse(CheckResult item, EdgeFetchedResponse response)
        {
            bool resolved = ClassifyFastResponse(item, response);
            if (resolved) ResolvedCount++;
            return resolved;
        }

        private static bool IsResolvedVerdict(string verdict)
        {
            return verdict == "已失效" || verdict == "仍可访问";
        }

        internal static bool ShouldFastRenderPlatform(CheckResult item)
        {
            Uri uri;
            Uri.TryCreate(item == null ? "" : item.OriginalUrl, UriKind.Absolute, out uri);
            string host = uri == null ? "" : uri.Host.ToLowerInvariant();
            string platform = item == null ? "" : (item.Platform ?? "").ToLowerInvariant();
            return host.Contains("toutiao.com") || host.Contains("baidu.com") || host.Contains("yoojia.com") ||
                host.Contains("weibo.com") || host.Contains("weibo.cn") || host.Contains("xueqiu.com") ||
                host.Contains("bilibili.com") || host.Contains("b23.tv") || host.Contains("zhihu.com") ||
                host.Contains("douyin.com") || host.Contains("iesdouyin.com") || host.Contains("dongchedi.com") || host.Contains("dcdapp.com") ||
                host.Contains("jianshu.com") || host.Contains("360kuai.com") || host.Contains("ahnews.com.cn") ||
                host.Contains("ciccwm.com") || host.Contains("shangyexinzhi.com") || host.Contains("10jqka.com.cn") ||
                host.Contains("mp.weixin.qq.com") || host.Contains("weixin.qq.com") || host.Contains("kuaishou.com") ||
                host.Contains("ishare.ifeng.com") || host.Contains("finance.sina.com.cn") || host.Contains("guba.sina.com.cn") ||
                host.Contains("news.m.yiche.com") || host.Contains("kandianshare.html5.qq.com") || host.Contains("v.qq.com") ||
                host.Contains("wap.eastmoney.com") || host.Contains("3g.k.sohu.com") ||
                platform.Contains("头条") || platform.Contains("百家号") || platform.Contains("有驾") ||
                platform.Contains("微博") || platform.Contains("雪球") || platform.Contains("哔哩") || platform.Contains("抖音") || platform.Contains("懂车帝") ||
                platform.Contains("b站") || platform.Contains("知乎") || platform.Contains("简书") || platform.Contains("快资讯") ||
                platform.Contains("安徽日报") || platform.Contains("微信") || platform.Contains("商业新知") || platform.Contains("中金财富");
        }

        private static bool IsPlatformSecurityPage(RenderedPageData page)
        {
            string evidence = ((page == null ? "" : page.Title) + " " + (page == null ? "" : page.Text) + " " +
                (page == null ? "" : page.Url)).ToLowerInvariant();
            return evidence.Contains("安全验证") || evidence.Contains("访问过于频繁") ||
                evidence.Contains("操作频繁") || evidence.Contains("captcha") ||
                evidence.Contains("verify you are human") || evidence.Contains("unusual traffic") ||
                evidence.Contains("too many requests");
        }

        internal static bool IsFastSecurityPage(RenderedPageData page)
        {
            return IsPlatformSecurityPage(page);
        }

        internal static async Task<RenderedPageData> ReadFastRenderedPageAsync(WebView2 browser, string url, CancellationToken token)
        {
            Uri pacingUri;
            if (Uri.TryCreate(url, UriKind.Absolute, out pacingUri)) await Checker.WaitForRequestSlotAsync(pacingUri, token);
            var completion = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null;
            handler = delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                browser.CoreWebView2.NavigationCompleted -= handler;
                completion.TrySetResult(args.IsSuccess);
            };
            browser.CoreWebView2.NavigationCompleted += handler;
            browser.CoreWebView2.Navigate(url);
            Task finished = await Task.WhenAny(completion.Task, Task.Delay(6500, token));
            browser.CoreWebView2.NavigationCompleted -= handler;
            if (finished != completion.Task)
            {
                try { browser.CoreWebView2.Stop(); } catch { }
                return new RenderedPageData { Url = url };
            }

            int wait = FastRenderWaitMilliseconds(url);
            await Task.Delay(wait, token);
            RenderedPageData latest = await ReadPageAsync(browser, token);
            int retries = IsFastDynamicHost(url) ? 3 : 1;
            for (int attempt = 0; attempt < retries && (latest.Text ?? "").Length < 160; attempt++)
            {
                await Task.Delay(500, token);
                latest = await ReadPageAsync(browser, token);
            }
            return latest;
        }

        private static int FastRenderWaitMilliseconds(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return 700;
            string host = uri.Host.ToLowerInvariant();
            if (host.Contains("dongchedi.com") || host.Contains("dcdapp.com") ||
                host.Contains("xueqiu.com") || host.Contains("weibo.com") ||
                host.Contains("bilibili.com") || host.Contains("kuaishou.com") ||
                host.Contains("mp.weixin.qq.com")) return 2400;
            return IsFastDynamicHost(url) ? 1700 : 700;
        }

        private static bool IsFastDynamicHost(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            return host.Contains("zhihu.com") || host.Contains("toutiao.com") || host.Contains("weibo.com") ||
                host.Contains("xueqiu.com") || host.Contains("douyin.com") || host.Contains("dongchedi.com") ||
                host.Contains("yoojia.com") || host.Contains("baidu.com") || host.Contains("xiaohongshu.com") ||
                host.Contains("jianshu.com") || host.Contains("360kuai.com") || host.Contains("ahnews.com.cn") ||
                host.Contains("ciccwm.com") || host.Contains("shangyexinzhi.com") || host.Contains("10jqka.com.cn") ||
                host.Contains("weixin.qq.com") || host.Contains("kuaishou.com");
        }

        internal static bool ApplyFastRenderedPage(CheckResult item, RenderedPageData page)
        {
            if (item == null || page == null || String.IsNullOrWhiteSpace(page.Html)) return false;
            item.AnalysisContext = AiReviewPolicy.BuildObservedContext(page.Title, page.MainText, page.Text);
            DeepDecision decision = Checker.ClassifyRenderedPage(item, page);
            if (!String.IsNullOrWhiteSpace(page.Title)) item.Title = page.Title;
            if (!String.IsNullOrWhiteSpace(page.Url)) item.FinalUrl = page.Url;
            if (decision.Resolved && decision.Verdict == "暂时异常")
            {
                item.Verdict = "暂时异常";
                item.StatusCode = "浏览器错误页";
                item.Evidence = "内置浏览器短渲染：" + decision.Evidence;
                return false;
            }
            if (decision.Resolved && IsResolvedVerdict(decision.Verdict))
            {
                item.Verdict = decision.Verdict;
                item.Evidence = "内置浏览器短渲染：" + decision.Evidence;
                return true;
            }
            else
            {
                item.Verdict = "人工复核";
                item.Evidence = "内置浏览器抓取和短渲染均未取得足够目标身份证据，保留人工复核" +
                    (String.IsNullOrWhiteSpace(decision.Evidence) ? "" : "；具体原因：" + decision.Evidence);
                return false;
            }
        }

        internal static bool ClassifyFastResponse(CheckResult item, EdgeFetchedResponse response)
        {
            if (item == null) return false;
            item.EdgeFastReviewed = true;
            item.DeepReviewed = false;
            item.CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            item.FinalUrl = item.OriginalUrl;
            if (response == null || response.StatusCode <= 0)
            {
                item.Verdict = "人工复核";
                item.StatusCode = "浏览器失败";
                item.Evidence = "内置浏览器网络请求失败，已保留给深度复核" +
                    (response == null || String.IsNullOrWhiteSpace(response.Error) ? "" : "：" + response.Error);
                return false;
            }

            int code = response.StatusCode;
            item.StatusCode = code.ToString();
            if (code == 404 || code == 410)
            {
                item.Verdict = "人工复核";
                item.Evidence = "内置浏览器快速请求返回 HTTP " + code +
                    "，但该接口不提供最终跳转地址，需由平台接口或渲染页确认是否属于目标内容";
                return false;
            }
            if (code == 429 || code == 444)
            {
                item.Verdict = "暂时异常";
                item.Evidence = "内置浏览器请求受到站点限流（HTTP " + code + "），已保留稍后重试";
                return false;
            }
            if (code == 401 || code == 403 || code == 407)
            {
                item.Verdict = "人工复核";
                item.Evidence = "内置浏览器请求受到访问限制（HTTP " + code + "），不能据此判定失效";
                return false;
            }
            if (code >= 500 || code == 408)
            {
                item.Verdict = "暂时异常";
                item.Evidence = "内置浏览器请求返回 HTTP " + code + "，建议稍后重试";
                return false;
            }
            if (code >= 400)
            {
                item.Verdict = "人工复核";
                item.Evidence = "内置浏览器请求返回 HTTP " + code + "，证据不足";
                return false;
            }
            string mediaType = response.ContentType ?? "";
            if (mediaType.Length > 0 && mediaType.IndexOf("html", StringComparison.OrdinalIgnoreCase) < 0 &&
                mediaType.IndexOf("json", StringComparison.OrdinalIgnoreCase) < 0 &&
                mediaType.IndexOf("text", StringComparison.OrdinalIgnoreCase) < 0)
            {
                item.Verdict = "仍可访问";
                item.Evidence = "内置浏览器复核确认资源可正常获取（HTTP " + code + "，" + mediaType + "）";
                return true;
            }

            RenderedPageData page = Checker.BuildRenderedPageData(response.Body, item.OriginalUrl);
            item.AnalysisContext = AiReviewPolicy.BuildObservedContext(page.Title, page.MainText, page.Text);
            if (!String.IsNullOrWhiteSpace(page.Title)) item.Title = page.Title;
            DeepDecision decision = Checker.ClassifyRenderedPage(item, page);
            if (decision.Resolved && decision.Verdict == "暂时异常")
            {
                item.Verdict = "暂时异常";
                item.StatusCode = "浏览器错误页";
                item.Evidence = "内置浏览器复核：" + decision.Evidence;
                return false;
            }
            if (decision.Resolved && (decision.Verdict == "已失效" || decision.Verdict == "仍可访问"))
            {
                item.Verdict = decision.Verdict;
                item.Evidence = "内置浏览器复核：" + decision.Evidence;
                return true;
            }
            else
            {
                item.Verdict = "人工复核";
                item.Evidence = "内置浏览器复核已取得响应，但页面需要渲染或证据不足，保留给深度复核";
                return false;
            }
        }

        internal static async Task<EdgeFetchedResponse> LoadEdgeResourceAsync(CoreWebView2 core, string url, int maxBytes, CancellationToken token)
        {
            if (core == null) return new EdgeFetchedResponse { Error = "内置浏览器内核尚未初始化" };
            Uri pacingUri;
            if (Uri.TryCreate(url, UriKind.Absolute, out pacingUri)) await Checker.WaitForRequestSlotAsync(pacingUri, token);
            var serializer = new JavaScriptSerializer { MaxJsonLength = 2000000 };
            string frameJson = await AwaitCdpAsync(core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"), 8000, token);
            var frameRoot = serializer.DeserializeObject(frameJson) as Dictionary<string, object>;
            string frameId = DictionaryPathString(frameRoot, "frameTree", "frame", "id");
            if (String.IsNullOrWhiteSpace(frameId)) return new EdgeFetchedResponse { Error = "无法取得内置浏览器页面上下文" };

            string parameters = serializer.Serialize(new Dictionary<string, object>
            {
                { "frameId", frameId },
                { "url", url },
                { "options", new Dictionary<string, object> { { "disableCache", false }, { "includeCredentials", true } } }
            });
            token.ThrowIfCancellationRequested();
            string loadedJson = await AwaitCdpAsync(core.CallDevToolsProtocolMethodAsync("Network.loadNetworkResource", parameters), 18000, token);
            var loaded = serializer.DeserializeObject(loadedJson) as Dictionary<string, object>;
            var resource = DictionaryPath(loaded, "resource");
            if (resource == null) return new EdgeFetchedResponse { Error = "内置浏览器未返回网络结果" };
            int status = DictionaryInt(resource, "httpStatusCode");
            string error = DictionaryString(resource, "netErrorName");
            string stream = DictionaryString(resource, "stream");
            string contentType = HeaderValue(resource, "content-type");
            Encoding responseEncoding = Encoding.UTF8;
            Match charset = Regex.Match(contentType ?? "", @"charset\s*=\s*([a-zA-Z0-9_\-]+)", RegexOptions.IgnoreCase);
            if (charset.Success)
            {
                try { responseEncoding = Encoding.GetEncoding(charset.Groups[1].Value); } catch { }
            }
            var body = new StringBuilder();
            while (!String.IsNullOrWhiteSpace(stream) && body.Length < maxBytes)
            {
                token.ThrowIfCancellationRequested();
                string readParameters = serializer.Serialize(new Dictionary<string, object>
                {
                    { "handle", stream }, { "size", Math.Min(65536, maxBytes - body.Length) }
                });
                string readJson = await AwaitCdpAsync(core.CallDevToolsProtocolMethodAsync("IO.read", readParameters), 8000, token);
                var read = serializer.DeserializeObject(readJson) as Dictionary<string, object>;
                string data = DictionaryString(read, "data");
                bool base64 = DictionaryBool(read, "base64Encoded");
                if (base64 && !String.IsNullOrEmpty(data))
                {
                    try { data = responseEncoding.GetString(Convert.FromBase64String(data)); } catch { }
                }
                if (!String.IsNullOrEmpty(data)) body.Append(data, 0, Math.Min(data.Length, maxBytes - body.Length));
                if (DictionaryBool(read, "eof")) break;
            }
            if (!String.IsNullOrWhiteSpace(stream))
            {
                try { await AwaitCdpAsync(core.CallDevToolsProtocolMethodAsync("IO.close", serializer.Serialize(new Dictionary<string, object> { { "handle", stream } })), 3000, token); }
                catch { }
            }
            return new EdgeFetchedResponse { StatusCode = status, Body = body.ToString(), ContentType = contentType, Error = error };
        }

        private static async Task<string> AwaitCdpAsync(Task<string> operation, int timeoutMilliseconds, CancellationToken token)
        {
            Task finished = await Task.WhenAny(operation, Task.Delay(timeoutMilliseconds, token));
            if (finished == operation) return await operation;
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("内置浏览器网络请求超时");
        }

        internal static async Task<bool> TryApplyEdgePlatformProbeAsync(CoreWebView2 core, CheckResult item, CancellationToken token)
        {
            Uri original;
            if (item == null || !Uri.TryCreate(item.OriginalUrl, UriKind.Absolute, out original)) return false;
            string host = original.Host.ToLowerInvariant();
            Match identity;
            if (host.EndsWith("douyin.com", StringComparison.Ordinal) || host.EndsWith("iesdouyin.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/(?:share/)?video/([0-9]{12,})", RegexOptions.IgnoreCase);
                if (identity.Success)
                {
                    string id = identity.Groups[1].Value;
                    string probeUrl = "https://www.iesdouyin.com/share/video/" + id + "/";
                    RenderedPageData probe = await ReadEdgePageWithUserAgentAsync(core, probeUrl,
                        "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 Chrome/124 Mobile Safari/537.36", 4500, token);
                    string body = probe == null ? "" : (probe.Html ?? "");
                    string normalizedJson = body.Replace("\\\"", "\"");
                    bool targetItem = Regex.IsMatch(body, "\\\"itemId\\\"\\s*:\\s*\\\"?" + Regex.Escape(id) + "\\\"?", RegexOptions.IgnoreCase);
                    bool itemListEmpty = Regex.IsMatch(normalizedJson, "\\\"item_list\\\"\\s*:\\s*\\[\\s*\\]", RegexOptions.IgnoreCase);
                    Match filter = Regex.Match(normalizedJson, "\\\"filter_reason\\\"\\s*:\\s*\\\"([^\\\"]*)", RegexOptions.IgnoreCase);
                    bool hasTargetDescription = Regex.IsMatch(normalizedJson,
                        "\\\"(?:aweme_id|item_id)\\\"\\s*:\\s*\\\"?" + Regex.Escape(id) + "\\\"?", RegexOptions.IgnoreCase) &&
                        Regex.IsMatch(normalizedJson, "\\\"desc\\\"\\s*:\\s*\\\"[^\\\"]{4,}", RegexOptions.IgnoreCase) && !itemListEmpty;
                    if (probe != null && hasTargetDescription)
                    {
                        item.Verdict = "仍可访问";
                        item.StatusCode = "200";
                        item.FinalUrl = probeUrl;
                        item.Title = probe.Title;
                        item.Evidence = "内置浏览器的抖音官方分享页返回目标作品描述和非空作品数据";
                        return true;
                    }
                    if (probe != null && targetItem && itemListEmpty && filter.Success &&
                        !String.IsNullOrWhiteSpace(filter.Groups[1].Value))
                    {
                        item.Verdict = "已失效";
                        item.StatusCode = "200";
                        item.FinalUrl = probeUrl;
                        item.Evidence = "内置浏览器的抖音官方分享页确认目标作品不可见（" + filter.Groups[1].Value + "）";
                        return true;
                    }
                    item.Evidence = "抖音分享页补证未解析：标题“" + (probe == null ? "" : probe.Title) + "”，字节 " + body.Length +
                        "，目标编号=" + targetItem + "，空作品=" + itemListEmpty + "，过滤原因=" + (filter.Success ? filter.Groups[1].Value : "无");
                }
                return false;
            }
            if (host.EndsWith("toutiao.com", StringComparison.Ordinal))
            {
                identity = Regex.Match(original.AbsolutePath ?? "", @"/(?:item|article|video|w)/([0-9]{12,})", RegexOptions.IgnoreCase);
                if (!identity.Success) return false;
                string id = identity.Groups[1].Value;
                string probeUrl = "https://m.toutiao.com/i" + id + "/info/";
                EdgeFetchedResponse probe = await LoadEdgeResourceAsync(core, probeUrl, 700000, token);
                string body = probe == null ? "" : (probe.Body ?? "");
                if (probe != null && probe.StatusCode == 200 && Regex.IsMatch(body,
                    "\\\"(?:gid|group_id)\\\"\\s*:\\s*\\\"?" + Regex.Escape(id) + "\\\"?", RegexOptions.IgnoreCase))
                {
                    item.Evidence = "内置浏览器公开内容接口仍有今日头条目标编号记录，仅作辅助证据，继续核验当前网页";
                    return false;
                }
                if (probe != null && probe.StatusCode == 200 && Regex.IsMatch(body, "\\\"data\\\"\\s*:\\s*null") &&
                    Regex.IsMatch(body, "\\\"success\\\"\\s*:\\s*false"))
                {
                    item.Verdict = "已失效";
                    item.StatusCode = "200";
                    item.FinalUrl = probeUrl;
                    item.Evidence = "内置浏览器公开内容接口确认今日头条目标内容不存在";
                    return true;
                }
                return false;
            }

            if (host.EndsWith("zhihu.com", StringComparison.Ordinal))
            {
                // The API is often blocked by corporate policy and may retain removed answers.
                // The current rendered answer page is both faster and more reliable.
                return false;
            }

            string videoId = Checker.ExtractBaiduVideoId(original);
            if (!String.IsNullOrEmpty(videoId) && !host.EndsWith("haokan.baidu.com", StringComparison.Ordinal))
            {
                string probeUrl = "https://haokan.baidu.com/v?vid=" + videoId;
                EdgeFetchedResponse probe = await LoadEdgeResourceAsync(core, probeUrl, 700000, token);
                string body = probe == null ? "" : (probe.Body ?? "");
                if (probe != null && probe.StatusCode == 200 && Checker.IsHaokanErrorResponse(body, videoId))
                {
                    item.Verdict = "已失效";
                    item.StatusCode = "200";
                    item.FinalUrl = probeUrl;
                    item.Evidence = "内置浏览器的百度系共享视频页确认目标视频编号已进入专用错误页";
                    return true;
                }
                if (probe != null && probe.StatusCode == 200 &&
                    Checker.HasBaiduVideoIdentity(body, videoId, item.ExpectedTitle))
                {
                    item.Evidence = "内置浏览器的百度系共享页仍有目标视频编号记录，仅作辅助证据，继续核验当前网页";
                    return false;
                }
            }

            string articleId = Checker.ExtractBaiduArticleId(original);
            if (!String.IsNullOrEmpty(articleId))
            {
                string probeUrl = "https://mbd.baidu.com/newspage/data/landingreact?nid=news_" + articleId;
                EdgeFetchedResponse probe = await LoadEdgeResourceAsync(core, probeUrl, 700000, token);
                string body = probe == null ? "" : (probe.Body ?? "");
                if (probe != null && probe.StatusCode == 200)
                {
                    string title = Checker.ExtractTitle(body);
                    bool errorPage = body.IndexOf(articleId, StringComparison.OrdinalIgnoreCase) < 0 &&
                        body.IndexOf("这里空空如也", StringComparison.Ordinal) >= 0;
                    if (errorPage)
                    {
                        item.Verdict = "已失效";
                        item.StatusCode = "200";
                        item.FinalUrl = probeUrl;
                        item.Evidence = "内置浏览器的百度系共享图文页确认目标内容编号已进入专用错误页";
                        return true;
                    }
                    if (body.IndexOf(articleId, StringComparison.OrdinalIgnoreCase) >= 0 && !Checker.LooksGenericTitle(title) &&
                        (Checker.MatchesExpectedTitle(item.ExpectedTitle, title) || String.IsNullOrWhiteSpace(item.ExpectedTitle)))
                    {
                        item.Evidence = "内置浏览器的百度系共享页仍有目标图文编号记录，仅作辅助证据，继续核验当前网页";
                        return false;
                    }
                }
            }
            return false;
        }

        private static async Task<RenderedPageData> ReadEdgePageWithUserAgentAsync(CoreWebView2 core, string url,
            string userAgent, int waitMilliseconds, CancellationToken token)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 2000000 };
            string currentUa = "";
            try
            {
                string uaJson = await AwaitCdpAsync(core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                    "{\"expression\":\"navigator.userAgent\",\"returnByValue\":true}"), 5000, token);
                var uaRoot = serializer.DeserializeObject(uaJson) as Dictionary<string, object>;
                currentUa = DictionaryPathString(uaRoot, "result", "result", "value");
            }
            catch { }
            string setUa = serializer.Serialize(new Dictionary<string, object> { { "userAgent", userAgent } });
            await AwaitCdpAsync(core.CallDevToolsProtocolMethodAsync("Network.setUserAgentOverride", setUa), 5000, token);
            RenderedPageData page = null;
            Exception failure = null;
            try
            {
                await AwaitCdpAsync(core.CallDevToolsProtocolMethodAsync("Page.navigate",
                    serializer.Serialize(new Dictionary<string, object> { { "url", url } })), 8000, token);
                string expression = "(function(){return {Title:document.title||'',Url:location.href||'',Text:(document.body?document.body.innerText:'').substring(0,120000),Html:(document.documentElement?document.documentElement.outerHTML:'').substring(0,900000)};})()";
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < Math.Max(800, waitMilliseconds))
                {
                    await Task.Delay(400, token);
                    string pageJson = await AwaitCdpAsync(core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                        serializer.Serialize(new Dictionary<string, object> { { "expression", expression }, { "returnByValue", true } })), 8000, token);
                    var pageRoot = serializer.DeserializeObject(pageJson) as Dictionary<string, object>;
                    var value = DictionaryPath(pageRoot, "result", "result", "value");
                    page = new RenderedPageData
                    {
                        Title = DictionaryString(value, "Title"),
                        Url = DictionaryString(value, "Url"),
                        Text = DictionaryString(value, "Text"),
                        Html = DictionaryString(value, "Html")
                    };
                    if (!String.IsNullOrWhiteSpace(page.Html) && !String.IsNullOrWhiteSpace(page.Url) &&
                        page.Url.IndexOf("iesdouyin.com/share/video/", StringComparison.OrdinalIgnoreCase) >= 0) break;
                }
            }
            catch (Exception ex) { failure = ex; }
            try
            {
                if (String.IsNullOrWhiteSpace(currentUa)) currentUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36";
                await AwaitCdpAsync(core.CallDevToolsProtocolMethodAsync("Network.setUserAgentOverride",
                    serializer.Serialize(new Dictionary<string, object> { { "userAgent", currentUa } })), 5000, token);
            }
            catch { }
            if (failure != null) throw failure;
            return page;
        }

        private static Dictionary<string, object> DictionaryPath(Dictionary<string, object> source, params string[] keys)
        {
            Dictionary<string, object> current = source;
            foreach (string key in keys)
            {
                object value;
                if (current == null || !current.TryGetValue(key, out value)) return null;
                current = value as Dictionary<string, object>;
            }
            return current;
        }

        private static string DictionaryPathString(Dictionary<string, object> source, params string[] keys)
        {
            if (keys == null || keys.Length == 0) return "";
            Dictionary<string, object> parent = DictionaryPath(source, keys.Take(keys.Length - 1).ToArray());
            return DictionaryString(parent, keys[keys.Length - 1]);
        }

        private static string DictionaryString(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
        }

        private static int DictionaryInt(Dictionary<string, object> source, string key)
        {
            int value;
            return Int32.TryParse(DictionaryString(source, key), out value) ? value : 0;
        }

        private static bool DictionaryBool(Dictionary<string, object> source, string key)
        {
            bool value;
            return Boolean.TryParse(DictionaryString(source, key), out value) && value;
        }

        private static string HeaderValue(Dictionary<string, object> resource, string name)
        {
            var headers = DictionaryPath(resource, "headers");
            if (headers == null) return "";
            foreach (var pair in headers)
                if (String.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return Convert.ToString(pair.Value) ?? "";
            return "";
        }

        private void PrepareLoginTargets()
        {
            _loginTargets.Clear();
            foreach (CheckResult item in _items)
            {
                Uri uri;
                if (!Uri.TryCreate(item.OriginalUrl, UriKind.Absolute, out uri)) continue;
                string host = uri.Host.ToLowerInvariant();
                string name = null;
                string target = null;
                PlatformRule configured = PlatformRules.Find(uri);
                if (configured != null && !String.IsNullOrWhiteSpace(configured.LoginUrl))
                {
                    name = String.IsNullOrWhiteSpace(configured.Name) ? host : configured.Name;
                    target = configured.LoginUrl;
                }
                else if (host.Contains("weibo.com")) { name = "微博"; target = "https://weibo.com/"; }
                else if (host.Contains("zhihu.com")) { name = "知乎"; target = "https://www.zhihu.com/"; }
                else if (host.Contains("toutiao.com")) { name = "今日头条"; target = "https://www.toutiao.com/"; }
                else if (host.Contains("douyin.com")) { name = "抖音"; target = "https://www.douyin.com/"; }
                else if (host.Contains("dongchedi.com") || host.Contains("dcdapp.com")) { name = "懂车帝"; target = "https://www.dongchedi.com/"; }
                else if (host.Contains("baidu.com")) { name = "百度系平台"; target = "https://www.baidu.com/"; }
                else if (host.Contains("qq.com")) { name = "腾讯系平台"; target = "https://www.qq.com/"; }
                else if (host.Contains("xiaohongshu.com") || host.Contains("xhslink.com")) { name = "小红书（可能需扫码）"; target = "https://www.xiaohongshu.com/"; }
                else if ((item.Evidence ?? "").IndexOf("登录", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (item.Evidence ?? "").IndexOf("验证", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    name = host;
                    target = uri.GetLeftPart(UriPartial.Authority) + "/";
                }
                if (!String.IsNullOrEmpty(name) && !_loginTargets.ContainsKey(name)) _loginTargets[name] = target;
            }
            _loginPlatforms.Items.Clear();
            foreach (string name in _loginTargets.Keys) _loginPlatforms.Items.Add(name);
            if (_loginTargets.Count == 0) _loginPlatforms.Items.Add("未发现常见登录平台");
        }

        private async Task OpenSelectedLoginPlatformAsync()
        {
            string name = Convert.ToString(_loginPlatforms.SelectedItem);
            string target;
            if (String.IsNullOrEmpty(name) || !_loginTargets.TryGetValue(name, out target)) return;
            _status.Text = "请在上方页面完成“" + name + "”登录；完成后可继续选择其他平台。";
            try { await NavigateAsync(target); }
            catch (Exception ex) { _status.Text = "登录页打开失败：" + ex.Message; }
        }

        private static string VerificationPlatformKey(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return "";
            string host = (uri.Host ?? "").Trim().Trim('.').ToLowerInvariant();
            foreach (string prefix in new[] { "www.", "m.", "wap.", "mobile." })
                if (host.StartsWith(prefix, StringComparison.Ordinal)) { host = host.Substring(prefix.Length); break; }
            return host;
        }

        private static int GetReviewBatchOrder(CheckResult item)
        {
            string tier = GetDeepPlatformProfile(item == null ? "" : item.OriginalUrl).Tier;
            return tier == "高把握" ? 0 : (tier == "动态公开页" ? 1 : 2);
        }

        private string BuildReviewBatchSummary()
        {
            int reliable = _items.Count(item => GetDeepPlatformProfile(item.OriginalUrl).Tier == "高把握");
            int dynamic = _items.Count(item => GetDeepPlatformProfile(item.OriginalUrl).Tier == "动态公开页");
            int limited = _items.Count - reliable - dynamic;
            return "本批高把握 " + reliable + " 条、动态公开页 " + dynamic + " 条、平台受限 " + limited + " 条";
        }

        private string BuildUnresolvedSummary()
        {
            List<CheckResult> unresolved = _items.Where(item => item.Verdict != "已失效" && item.Verdict != "仍可访问").ToList();
            if (unresolved.Count == 0) return "全部项目均取得足够证据。";
            string platforms = String.Join("、", unresolved
                .GroupBy(item => VerificationPlatformKey(item.OriginalUrl))
                .OrderByDescending(group => group.Count())
                .Take(6)
                .Select(group => (String.IsNullOrWhiteSpace(group.Key) ? "未知平台" : group.Key) + " " + group.Count() + " 条"));
            return "仍有 " + unresolved.Count + " 条无法自动确认（" + platforms + "）；每条结果的“判定依据”已写明登录、风控、客户端限制或证据不足原因。";
        }

        private static DeepPlatformProfile GetDeepPlatformProfile(string url)
        {
            Uri uri;
            Uri.TryCreate(url, UriKind.Absolute, out uri);
            PlatformRule configured = PlatformRules.Find(uri);
            string host = uri == null ? "" : uri.Host.ToLowerInvariant();
            string tier = configured == null || String.IsNullOrWhiteSpace(configured.ReviewTier)
                ? (configured == null ? "平台受限" : (configured.DynamicShell ? "动态公开页" : "高把握"))
                : configured.ReviewTier;
            if (host.Contains("channels.weixin.qq.com") || host.Contains("xiaohongshu.com") || host.Contains("xhslink.com")) tier = "平台受限";

            int minimum = configured != null && configured.MinimumWaitMilliseconds > 0
                ? configured.MinimumWaitMilliseconds : (tier == "高把握" ? 700 : (tier == "动态公开页" ? 1400 : 1000));
            int maximum = configured != null && configured.MaximumWaitMilliseconds > 0
                ? configured.MaximumWaitMilliseconds : (tier == "高把握" ? 2400 : (tier == "动态公开页" ? 4800 : 3200));
            int navigation = configured != null && configured.NavigationTimeoutMilliseconds > 0
                ? configured.NavigationTimeoutMilliseconds : (tier == "高把握" ? 9000 : 13000);
            return new DeepPlatformProfile
            {
                Tier = tier,
                MinimumWaitMilliseconds = minimum,
                MaximumWaitMilliseconds = Math.Max(minimum, maximum),
                NavigationTimeoutMilliseconds = navigation,
                Limitation = configured == null ? "未知平台暂无专用规则，只有取得正文身份或明确错误页才会自动判定" : configured.Limitation
            };
        }

        private static bool TryBuildYoojiaAlternateUrl(string originalUrl, out string alternateUrl)
        {
            alternateUrl = "";
            Uri uri;
            if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            if (!(host == "yoojia.com" || host.EndsWith(".yoojia.com", StringComparison.Ordinal))) return false;
            if (host == "yoojia.baidu.com" || host.EndsWith(".yoojia.baidu.com", StringComparison.Ordinal)) return false;

            Match id = Regex.Match((uri.AbsolutePath ?? "") + "&" + (uri.Query ?? ""), @"(?:nid=|/)([0-9]{8,})(?:\.|/|&|$)", RegexOptions.IgnoreCase);
            if (!id.Success) return false;
            string path = uri.AbsolutePath ?? "";
            bool video = path.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0;
            alternateUrl = video
                ? "https://yoojia.baidu.com/app/video-detail/index?nid=" + id.Groups[1].Value
                : "https://yoojia.baidu.com/app/tuwen/index?nid=" + id.Groups[1].Value;
            return !String.Equals(alternateUrl, originalUrl, StringComparison.OrdinalIgnoreCase);
        }

        private async Task NavigateAsync(string url, int timeoutMilliseconds = 13000)
        {
            Uri pacingUri;
            if (Uri.TryCreate(url, UriKind.Absolute, out pacingUri)) await Checker.WaitForRequestSlotAsync(pacingUri, _cancellation.Token);
            var completion = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null;
            handler = delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                _browser.CoreWebView2.NavigationCompleted -= handler;
                completion.TrySetResult(args.IsSuccess);
            };
            _browser.CoreWebView2.NavigationCompleted += handler;
            _browser.CoreWebView2.Navigate(url);
            Task finished = await Task.WhenAny(completion.Task, Task.Delay(Math.Max(5000, timeoutMilliseconds), _cancellation.Token));
            _browser.CoreWebView2.NavigationCompleted -= handler;
            if (finished != completion.Task)
            {
                try { _browser.CoreWebView2.Stop(); } catch { }
                throw new TimeoutException("页面加载超时");
            }
        }

        private async Task<RenderedPageData> ReadPageAsync()
        {
            return await ReadPageAsync(_browser, _cancellation.Token);
        }

        internal static async Task<RenderedPageData> ReadPageAsync(WebView2 browser, CancellationToken token)
        {
            string script = "(function(){var texts=[document.body?document.body.innerText:''];var h=document.documentElement?document.documentElement.outerHTML:'';for(var i=0;i<window.frames.length&&i<12;i++){try{var d=window.frames[i].document;if(d&&d.body){texts.push(d.body.innerText||'');if(d.documentElement&&h.length<300000)h+='\\n<!-- SAME-ORIGIN FRAME -->\\n'+d.documentElement.outerHTML;}}catch(e){}}var b=texts.join('\\n');var selectors=['article','main','[role=main]','[class*=article-content]','[class*=post-body]','[class*=detail-content]','[class*=main-content]','[id*=article-content]','[id*=post-body]','[id*=main-content]','[class*=error]','[class*=empty]','[class*=not-found]'];var best=null,bestScore=-1;for(var s=0;s<selectors.length;s++){var ns=document.querySelectorAll(selectors[s]);for(var n=0;n<ns.length;n++){var e=ns[n],t=(e.innerText||'').trim(),cl=((e.className||'')+' '+(e.id||'')).toLowerCase(),score=Math.min(t.length,12000);if((e.tagName||'').toLowerCase()=='article')score+=8000;if((e.tagName||'').toLowerCase()=='main')score+=5000;if(/article-content|post-body|detail-content|main-content|正文/.test(cl))score+=5000;if(/comment|recommend|sidebar|footer|nav|评论|推荐/.test(cl))score-=9000;if(score>bestScore){best=e;bestScore=score;}}}var mt=best?(best.innerText||''):'';var mh=best?(best.outerHTML||''):'';return {Title:document.title||'',Url:location.href||'',Text:b.substring(0,120000),Html:h.substring(0,350000),MainText:mt.substring(0,120000),MainHtml:mh.substring(0,220000)};})()";
            Task<string> scriptTask = browser.CoreWebView2.ExecuteScriptAsync(script);
            Task finished = await Task.WhenAny(scriptTask, Task.Delay(8000, token));
            if (finished != scriptTask)
            {
                token.ThrowIfCancellationRequested();
                try { browser.CoreWebView2.Stop(); } catch { }
                throw new TimeoutException("页面脚本读取超时");
            }
            string encoded = await scriptTask;
            var serializer = new JavaScriptSerializer { MaxJsonLength = 800000 };
            RenderedPageData page = serializer.Deserialize<RenderedPageData>(encoded);
            return page ?? new RenderedPageData();
        }
    }

    internal sealed class BatchPreflightSummary
    {
        internal readonly List<string> SampledKeys = new List<string>();
        internal int Checked;
        internal int Resolved;
        internal int TransientRestrictions;
        internal int EvidenceInsufficient;

        internal bool RequiresDecision
        {
            get
            {
                return Checked >= 4 && TransientRestrictions >= 4 &&
                    TransientRestrictions * 100 >= Checked * 60;
            }
        }

        internal string Description
        {
            get
            {
                return "预检 " + Checked + " 条：可判定 " + Resolved +
                    "，暂时异常 " + TransientRestrictions +
                    "，证据不足 " + EvidenceInsufficient;
            }
        }
    }

    internal static class BatchPreflightPlanner
    {
        internal static List<CheckJob> SelectSamples(IEnumerable<CheckJob> jobs, int maximum, int maximumPerPlatform)
        {
            int limit = Math.Max(0, maximum);
            int platformLimit = Math.Max(1, maximumPerPlatform);
            var queues = (jobs ?? Enumerable.Empty<CheckJob>())
                .Where(item => item != null)
                .GroupBy(PlatformKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => new Queue<CheckJob>(group.Take(platformLimit)))
                .ToList();
            var selected = new List<CheckJob>();
            while (selected.Count < limit && queues.Any(queue => queue.Count > 0))
            {
                foreach (Queue<CheckJob> queue in queues)
                {
                    if (selected.Count >= limit) break;
                    if (queue.Count > 0) selected.Add(queue.Dequeue());
                }
            }
            return selected;
        }

        internal static BatchPreflightSummary Analyze(IEnumerable<KeyValuePair<CheckJob, CheckResult>> observations)
        {
            var summary = new BatchPreflightSummary();
            foreach (KeyValuePair<CheckJob, CheckResult> observation in observations ?? Enumerable.Empty<KeyValuePair<CheckJob, CheckResult>>())
            {
                CheckJob job = observation.Key;
                CheckResult result = observation.Value;
                if (job != null) summary.SampledKeys.Add(job.Key);
                if (result == null) continue;
                summary.Checked++;
                ContractAcceptanceView acceptance = ContractAcceptanceClassifier.Evaluate(result);
                if (acceptance.ContentResolved) summary.Resolved++;
                else if (acceptance.RequiresIndependentNetworkReview) summary.EvidenceInsufficient++;
                else if (NetworkRestrictionCircuitBreaker.IsTransientRestriction(result)) summary.TransientRestrictions++;
                else summary.EvidenceInsufficient++;
            }
            return summary;
        }

        internal static string PlatformKey(CheckJob job)
        {
            string platform = job == null ? "" : (job.Platform ?? "").Trim();
            bool genericPlatform = String.IsNullOrWhiteSpace(platform) ||
                platform == "网媒" || platform == "未知" || platform == "未知平台";
            if (genericPlatform && job != null && !String.IsNullOrWhiteSpace(job.InfrastructureKey))
            {
                string infrastructure = job.InfrastructureKey.Trim();
                if (String.Equals(infrastructure, "IP 119.28.42.49", StringComparison.OrdinalIgnoreCase))
                {
                    Uri sharedNewsUri;
                    if (Uri.TryCreate(job.Url, UriKind.Absolute, out sharedNewsUri))
                        return (sharedNewsUri.Host ?? infrastructure).Trim().ToLowerInvariant();
                }
                return infrastructure;
            }
            Uri uri;
            if (job != null && Uri.TryCreate(job.Url, UriKind.Absolute, out uri))
            {
                string key = Checker.RequestPacingKey(uri);
                if (!String.IsNullOrWhiteSpace(key)) return key;
            }
            if (!String.IsNullOrWhiteSpace(platform)) return platform;
            return "未知平台";
        }
    }

    internal static class BatchRunSafetyPolicy
    {
        internal static bool ShouldPauseAfterPreflight(BatchPreflightSummary summary, bool userChoseContinue)
        {
            return summary != null && summary.RequiresDecision && !userChoseContinue;
        }

        internal static bool ShouldUseGlobalCircuitBreaker(bool userChoseContinue)
        {
            return !userChoseContinue;
        }
    }

    internal sealed class PlatformRestrictionController
    {
        private readonly object _sync = new object();
        private readonly int _threshold;
        private readonly Dictionary<string, int> _consecutive =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _publicUnavailableCount =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _paused =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pausedAsPubliclyUnavailable =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal PlatformRestrictionController(int threshold)
        {
            _threshold = Math.Max(2, threshold);
        }

        internal bool IsPaused(CheckJob job)
        {
            string key = BatchPreflightPlanner.PlatformKey(job);
            lock (_sync) { return _paused.ContainsKey(key); }
        }

        internal bool IsPubliclyUnavailable(CheckJob job)
        {
            string key = BatchPreflightPlanner.PlatformKey(job);
            lock (_sync) { return _pausedAsPubliclyUnavailable.Contains(key); }
        }

        internal bool Observe(CheckJob job, CheckResult result, out string pausedPlatform)
        {
            pausedPlatform = "";
            string key = BatchPreflightPlanner.PlatformKey(job);
            lock (_sync)
            {
                if (_paused.ContainsKey(key)) return false;
                // 百度、B站、微博等大型平台的不同内容页不能因为少量 200 空壳页而整组跳过。
                // 只有明确共享 IP 的网媒站群才复用连续基础设施异常。
                string platform = job == null ? "" : (job.Platform ?? "").Trim();
                bool genericPlatform = String.IsNullOrWhiteSpace(platform) ||
                    platform == "网媒" || platform == "未知" || platform == "未知平台";
                if (!genericPlatform)
                {
                    _consecutive[key] = 0;
                    return false;
                }
                if (!ShouldPauseAfterResult(result))
                {
                    _consecutive[key] = 0;
                    _publicUnavailableCount[key] = 0;
                    return false;
                }

                int count;
                _consecutive.TryGetValue(key, out count);
                count++;
                _consecutive[key] = count;
                int publicCount;
                _publicUnavailableCount.TryGetValue(key, out publicCount);
                if (String.Equals(result.Verdict, "公网不可访问", StringComparison.OrdinalIgnoreCase))
                    _publicUnavailableCount[key] = ++publicCount;
                if (count < _threshold) return false;
                pausedPlatform = DisplayLabel(job, key);
                _paused[key] = pausedPlatform;
                if (publicCount >= 2) _pausedAsPubliclyUnavailable.Add(key);
                return true;
            }
        }

        internal static bool ShouldPauseAfterResult(CheckResult result)
        {
            if (result == null) return false;
            string evidence = result.Evidence ?? "";
            if (evidence.IndexOf("Globalping", StringComparison.OrdinalIgnoreCase) >= 0 ||
                evidence.IndexOf("外部中国宽带探针", StringComparison.OrdinalIgnoreCase) >= 0 ||
                evidence.IndexOf("探针额度", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            int statusCode;
            if (Int32.TryParse(result.StatusCode ?? "", out statusCode))
                return statusCode == 403 || statusCode == 429 || statusCode == 444;
            return NetworkRestrictionCircuitBreaker.IsSecurityOrRateLimitText(evidence);
        }

        internal static string DisplayLabel(CheckJob job, string key)
        {
            string platform = job == null ? "" : (job.Platform ?? "").Trim();
            string siteKey = (key ?? "").Trim();
            if (String.IsNullOrWhiteSpace(siteKey)) siteKey = "未知站点";
            if (String.IsNullOrWhiteSpace(platform) ||
                platform == "网媒" || platform == "未知" || platform == "未知平台")
                return siteKey;
            if (String.Equals(platform, siteKey, StringComparison.OrdinalIgnoreCase)) return platform;
            return platform + "（" + siteKey + "）";
        }

        internal List<string> PausedPlatforms
        {
            get
            {
                lock (_sync) { return _paused.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToList(); }
            }
        }
    }

    // Shared hosting/IP failures are deferred once per infrastructure group.
    // Deferring later requests reduces duplicate outages without assigning a
    // content verdict to a network failure.
    internal sealed class InfrastructureRestrictionController
    {
        private readonly object _sync = new object();
        private readonly int _threshold;
        private readonly Dictionary<string, int> _consecutive =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _paused =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal InfrastructureRestrictionController(int threshold)
        {
            _threshold = Math.Max(2, threshold);
        }

        internal bool IsPaused(CheckJob job)
        {
            string key = Key(job);
            lock (_sync) { return key.Length > 0 && _paused.Contains(key); }
        }

        internal bool Observe(CheckJob job, CheckResult result, out string pausedInfrastructure)
        {
            pausedInfrastructure = "";
            string key = Key(job);
            if (key.Length == 0) return false;
            lock (_sync)
            {
                if (_paused.Contains(key)) return false;
                if (!NetworkRestrictionCircuitBreaker.IsTransientRestriction(result))
                {
                    _consecutive[key] = 0;
                    return false;
                }
                int count;
                _consecutive.TryGetValue(key, out count);
                _consecutive[key] = ++count;
                if (count < _threshold) return false;
                _paused.Add(key);
                pausedInfrastructure = key;
                return true;
            }
        }

        internal List<string> PausedInfrastructures
        {
            get { lock (_sync) { return _paused.OrderBy(item => item).ToList(); } }
        }

        private static string Key(CheckJob job)
        {
            if (job == null) return "";
            string platform = (job.Platform ?? "").Trim();
            bool genericPlatform = String.IsNullOrWhiteSpace(platform) ||
                platform == "网媒" || platform == "未知" || platform == "未知平台";
            // Do not let a CDN/shared IP outage on generic news hosts pause a
            // named platform that happens to resolve to the same address.
            if (!genericPlatform) return "";
            string infrastructure = (job.InfrastructureKey ?? "").Trim();
            if (infrastructure.Length > 0) return infrastructure;
            Uri uri;
            return Uri.TryCreate(job.Url, UriKind.Absolute, out uri)
                ? (uri.Host ?? "").Trim().Trim('.').ToLowerInvariant() : "";
        }
    }

    internal sealed class NetworkRestrictionCircuitBreaker
    {
        private readonly object _sync = new object();
        private readonly int _threshold;
        private int _consecutiveRestrictions;
        private bool _tripped;

        internal NetworkRestrictionCircuitBreaker(int threshold)
        {
            _threshold = Math.Max(2, threshold);
        }

        internal bool Observe(CheckResult item, out string reason)
        {
            reason = "";
            lock (_sync)
            {
                if (_tripped) return false;
                if (!IsTransientRestriction(item))
                {
                    _consecutiveRestrictions = 0;
                    return false;
                }

                _consecutiveRestrictions++;
                if (_consecutiveRestrictions < _threshold) return false;
                _tripped = true;
                reason = "连续 " + _consecutiveRestrictions + " 条返回限流、验证码或网络异常";
                return true;
            }
        }

        internal static bool IsTransientRestriction(CheckResult item)
        {
            if (item == null) return false;
            int statusCode;
            if (Int32.TryParse(item.StatusCode ?? "", out statusCode) &&
                (statusCode == 403 || statusCode == 408 || statusCode == 429 ||
                 statusCode == 444 || statusCode >= 500))
                return true;
            if (String.Equals(item.Verdict, "暂时异常", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(item.Verdict, "公网不可访问", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(item.StatusCode, "超时", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(item.StatusCode, "连接失败", StringComparison.OrdinalIgnoreCase))
                return true;
            return IsSecurityOrRateLimitText(item.Evidence);
        }

        internal static bool IsSecurityOrRateLimitText(string text)
        {
            string evidence = (text ?? "").ToLowerInvariant();
            return evidence.Contains("安全验证") || evidence.Contains("验证码") ||
                evidence.Contains("滑动验证") || evidence.Contains("访问过于频繁") ||
                evidence.Contains("操作频繁") || evidence.Contains("风控") ||
                evidence.Contains("访问受限") || evidence.Contains("访问被限制") ||
                evidence.Contains("captcha") ||
                evidence.Contains("verify you are human") || evidence.Contains("unusual traffic") ||
                evidence.Contains("too many requests") || evidence.Contains("连接关闭") ||
                evidence.Contains("连接已关闭") ||
                evidence.Contains("无法建立连接") || evidence.Contains("代理和直连都失败");
        }
    }

    internal sealed class RemoteEvidenceSettingsForm : Form
    {
        private readonly TextBox _endpoints = new TextBox();

        internal RemoteEvidenceSettingsForm()
        {
            Text = "远程取证节点设置";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(720, 470);
            MinimumSize = new Size(620, 400);
            Font = new Font("微软雅黑", 9.5f);
            BackColor = Color.White;

            var title = new Label
            {
                Text = "独立远程取证节点",
                Font = new Font("微软雅黑", 15, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(24, 20)
            };
            var help = new Label
            {
                Text = "工具已内置无缓存公开云取证；这里用于追加你或单位自有的 HTTPS 节点（最多 4 个）。\n" +
                    "支持 GET 模板（地址中包含 {url}）或 POST JSON：{\"url\":\"目标地址\"}。\n" +
                    "节点返回 JSON：status、finalUrl、title、text/html、source、error。这里只发送单条公开目标链接。",
                AutoSize = true,
                ForeColor = Color.FromArgb(75, 85, 99),
                Location = new Point(25, 62)
            };
            _endpoints.Multiline = true;
            _endpoints.ScrollBars = ScrollBars.Vertical;
            _endpoints.Location = new Point(28, 135);
            _endpoints.Size = new Size(646, 210);
            _endpoints.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _endpoints.Text = String.Join(Environment.NewLine, RemoteEvidenceStore.LoadEndpoints());

            var save = new Button
            {
                Text = "保存",
                Size = new Size(110, 36),
                Location = new Point(564, 365),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                BackColor = Color.FromArgb(38, 99, 177),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            save.FlatAppearance.BorderSize = 0;
            save.Click += delegate
            {
                try
                {
                    RemoteEvidenceStore.SaveEndpoints(_endpoints.Lines);
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Controls.Add(title);
            Controls.Add(help);
            Controls.Add(_endpoints);
            Controls.Add(save);
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly TextBox _input = new TextBox();
        private readonly Button _start = new Button();
        private readonly Button _stop = new Button();
        private readonly Button _import = new Button();
        private readonly Button _resume = new Button();
        private readonly Button _retryNetwork = new Button();
        private readonly Button _clear = new Button();
        private readonly Button _export = new Button();
        private readonly Button _deepReview = new Button();
        private readonly Button _aiReview = new Button();
        private readonly Button _aiSettings = new Button();
        private readonly Button _remoteSettings = new Button();
        private readonly Button _open = new Button();
        private readonly Button _openLog = new Button();
        private readonly Button _acceptancePack = new Button();
        private readonly ComboBox _filter = new ComboBox();
        private readonly ComboBox _performance = new ComboBox();
        private readonly ComboBox _networkMode = new ComboBox();
        private readonly DataGridView _grid = new DataGridView();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Label _progressText = new Label();
        private readonly Label _activity = new Label();
        private readonly System.Windows.Forms.Timer _animationTimer = new System.Windows.Forms.Timer();
        private readonly Label _allCount = MakeStat("0", "总链接");
        private readonly Label _removedCount = MakeStat("0", "失效");
        private readonly Label _aliveCount = MakeStat("0", "有效");
        private readonly Label _unavailableCount = MakeStat("0", "未完成");
        private readonly Label _temporaryCount = MakeStat("0", "访问异常待重试");
        private readonly Label _reviewCount = MakeStat("0", "其他待补证/复核");
        private readonly BindingList<CheckResult> _rows = new BindingList<CheckResult>();
        private readonly List<CheckResult> _allRows = new List<CheckResult>();
        private List<ExcelSheetPlan> _excelPlans = new List<ExcelSheetPlan>();
        private List<CheckJob> _importJobs = new List<CheckJob>();
        private string _excelPath;
        private CancellationTokenSource _cancellation;
        private bool _running;
        private bool _preflightRunning;
        private bool _closeRequested;
        private int _animationFrame;
        private Stopwatch _runWatch;
        private int _runCompleted;
        private int _runTotal;
        private int _runStartCompleted;
        private int _removedTotal;
        private int _aliveTotal;
        private int _unavailableTotal;
        private int _temporaryTotal;
        private int _reviewTotal;
        private readonly ConcurrentQueue<CheckResult> _uiResults = new ConcurrentQueue<CheckResult>();
        private PerformanceProfile _performanceProfile;

        public MainForm()
        {
            Text = "链接失效检测工具 · v" + SessionStore.CurrentEngineVersion;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle working = Screen.PrimaryScreen == null ? new Rectangle(0, 0, 1280, 800) : Screen.PrimaryScreen.WorkingArea;
            int minimumWidth = Math.Min(1060, Math.Max(640, working.Width - 24));
            int minimumHeight = Math.Min(690, Math.Max(520, working.Height - 40));
            MinimumSize = new Size(minimumWidth, minimumHeight);
            Size = new Size(Math.Min(1280, Math.Max(minimumWidth, working.Width - 24)),
                Math.Min(800, Math.Max(minimumHeight, working.Height - 40)));
            Font = new Font("微软雅黑", 9.5f);
            BackColor = Color.FromArgb(244, 247, 251);
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildLayout();
            SetupGrid();
            _performanceProfile = PerformanceProfile.Resolve("自动适配");
            RefreshResumeButton();
            FormClosing += MainFormClosing;
            _animationTimer.Interval = 180;
            _animationTimer.Tick += AnimationTick;
        }

        private void BuildLayout()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Color.FromArgb(27, 62, 111) };
            var title = new Label { Text = "链接失效检测工具", ForeColor = Color.White, Font = new Font("微软雅黑", 19, FontStyle.Bold), AutoSize = true, Location = new Point(24, 13) };
            var sub = new Label { Text = "批量检测 · 自动重试 · 正文确认", ForeColor = Color.FromArgb(206, 220, 239), AutoSize = true, Location = new Point(27, 51) };
            var versionPanel = new Panel { Dock = DockStyle.Right, Width = 152, BackColor = Color.FromArgb(27, 62, 111) };
            var versionBadge = new Label
            {
                Text = "版本 " + SessionStore.CurrentEngineVersion,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(38, 99, 177),
                Font = new Font("微软雅黑", 10.5f, FontStyle.Bold),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(124, 34),
                Location = new Point(8, 23),
                BorderStyle = BorderStyle.FixedSingle
            };
            versionPanel.Controls.Add(versionBadge);
            header.Controls.Add(title); header.Controls.Add(sub); header.Controls.Add(versionPanel);

            var main = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 16), ColumnCount = 1, RowCount = 5 };
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(main);
            Controls.Add(header);

            var stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(0, 0, 0, 10) };
            for (int i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            stats.Controls.Add(StatCard(_allCount), 0, 0); stats.Controls.Add(StatCard(_removedCount), 1, 0);
            stats.Controls.Add(StatCard(_aliveCount), 2, 0); stats.Controls.Add(StatCard(_unavailableCount), 3, 0);
            main.Controls.Add(stats, 0, 0);

            var inputPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14, 10, 14, 10) };
            var inputLabel = new Label { Text = "选择 Excel（核验后手动写回），或粘贴待核验链接", AutoSize = true, ForeColor = Color.FromArgb(55, 65, 81), Font = new Font("微软雅黑", 9.5f, FontStyle.Bold), Location = new Point(14, 9) };
            _input.Multiline = true; _input.ScrollBars = ScrollBars.Vertical; _input.AcceptsReturn = true; _input.WordWrap = false;
            _input.BorderStyle = BorderStyle.FixedSingle; _input.Location = new Point(14, 36); _input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _input.Size = new Size(inputPanel.Width - 190, 88);
            _input.PlaceholderTextCompat("例如：https://example.com/article/123");
            var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 150, Dock = DockStyle.Right, Padding = new Padding(8, 9, 0, 0) };
            StyleButton(_import, "选择 Excel / CSV", false); StyleButton(_resume, "恢复上次进度", false); StyleButton(_clear, "清空输入", false);
            _import.Click += ImportClick; _resume.Click += async delegate { await ResumeLastSessionAsync(); };
            _clear.Click += delegate
            {
                if (_running) return;
                _input.Clear(); _excelPath = null; _excelPlans.Clear(); _importJobs.Clear(); _allRows.Clear(); _rows.Clear();
                RecalculateCounters();
                _deepReview.Enabled = false; _export.Text = "导出结果"; _progressText.Text = "尚未开始"; UpdateStats();
            };
            side.Controls.Add(_import); side.Controls.Add(_resume); side.Controls.Add(_clear);
            inputPanel.Controls.Add(inputLabel); inputPanel.Controls.Add(_input); inputPanel.Controls.Add(side);
            _input.SizeChanged += delegate { _input.Width = Math.Max(200, inputPanel.ClientSize.Width - 190); };
            main.Controls.Add(inputPanel, 0, 1);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 6, 0, 4) };
            StyleButton(_start, "开始检测", true); StyleButton(_retryNetwork, "继续未完成", false);
            StyleButton(_deepReview, "自动补证", false);
            StyleButton(_stop, "停止", false);
            StyleButton(_export, "导出结果", false);
            StyleButton(_open, "打开链接", false); StyleButton(_openLog, "运行日志", false);
            _start.Width = 125; _stop.Width = 92; _export.Width = 125; _open.Width = 125; _openLog.Width = 125;
            _stop.Enabled = false; _retryNetwork.Enabled = false; _deepReview.Enabled = false;
            _aiReview.Enabled = false; _acceptancePack.Enabled = false;
            _start.Click += async delegate { await StartChecksAsync(false); }; _stop.Click += delegate { if (_cancellation != null) _cancellation.Cancel(); };
            _retryNetwork.Click += async delegate { await ResumeLastSessionAsync(true); };
            _deepReview.Click += delegate { RunSelectedReview(); }; _export.Click += ExportClick; _open.Click += OpenSelectedClick;
            _aiReview.Click += async delegate { await RunAiReviewAsync(); };
            _aiSettings.Click += delegate { ShowAiSettings(); };
            _remoteSettings.Click += delegate
            {
                if (_running) return;
                using (var form = new RemoteEvidenceSettingsForm()) form.ShowDialog(this);
            };
            _openLog.Click += delegate { OpenLatestExecutionLog(); };
            _acceptancePack.Click += delegate { GenerateAcceptanceEvidencePackage(null, true); };
            toolbar.Controls.Add(_start); toolbar.Controls.Add(_retryNetwork); toolbar.Controls.Add(_deepReview); toolbar.Controls.Add(_stop);
            toolbar.Controls.Add(_export); toolbar.Controls.Add(_open); toolbar.Controls.Add(_openLog);
            toolbar.Controls.Add(new Label { Text = "    显示：", AutoSize = true, Margin = new Padding(8, 9, 0, 0), ForeColor = Color.FromArgb(75, 85, 99) });
            _filter.DropDownStyle = ComboBoxStyle.DropDownList; _filter.Width = 160; _filter.Margin = new Padding(4, 4, 0, 0);
            _filter.Items.AddRange(new object[] { "全部结果", "有效", "失效", "未完成" }); _filter.SelectedIndex = 0; _filter.SelectedIndexChanged += delegate { ApplyFilter(); };
            toolbar.Controls.Add(_filter); main.Controls.Add(toolbar, 0, 2);
            _performance.DropDownStyle = ComboBoxStyle.DropDownList; _performance.Width = 115; _performance.Margin = new Padding(4, 4, 0, 0);
            _performance.Items.AddRange(new object[] { "自动适配", "低配模式", "标准模式", "高性能模式" }); _performance.SelectedIndex = 0;
            _networkMode.Items.Add("标准核验"); _networkMode.SelectedIndex = 0;

            _grid.Dock = DockStyle.Fill; main.Controls.Add(_grid, 0, 3);

            var footer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            _activity.Text = "●"; _activity.Font = new Font("微软雅黑", 13, FontStyle.Bold); _activity.ForeColor = Color.FromArgb(38, 99, 177); _activity.AutoSize = true; _activity.Location = new Point(0, 6);
            _progress.Width = 300; _progress.Height = 20; _progress.Location = new Point(26, 10); _progress.Anchor = AnchorStyles.Left;
            _progress.Style = ProgressBarStyle.Continuous;
            _progressText.Text = "尚未开始"; _progressText.AutoSize = true; _progressText.Location = new Point(340, 11); _progressText.ForeColor = Color.FromArgb(75, 85, 99);
            var note = new Label { Text = "取得正文才判定有效；404、410 或明确删除页才判定失效", AutoSize = true, ForeColor = Color.FromArgb(107, 114, 128), Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(footer.Width - 560, 11) };
            footer.Controls.Add(_activity); footer.Controls.Add(_progress); footer.Controls.Add(_progressText); footer.Controls.Add(note);
            footer.SizeChanged += delegate
            {
                note.Visible = footer.ClientSize.Width >= 1080;
                if (note.Visible) note.Left = Math.Max(520, footer.ClientSize.Width - note.Width);
            };
            main.Controls.Add(footer, 0, 4);
        }

        private void SetupGrid()
        {
            _grid.AutoGenerateColumns = false; _grid.DataSource = _rows; _grid.ReadOnly = true; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false; _grid.RowHeadersVisible = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = false;
            _grid.BackgroundColor = Color.White; _grid.BorderStyle = BorderStyle.None; _grid.GridColor = Color.FromArgb(229, 231, 235); _grid.RowTemplate.Height = 34;
            _grid.ColumnHeadersHeight = 38; _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(238, 242, 247); _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(55, 65, 81);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("微软雅黑", 9.2f, FontStyle.Bold); _grid.EnableHeadersVisualStyles = false;
            AddColumn("Number", "#", 46); AddColumn("DisplayVerdict", "状态", 90);
            AddColumn("StatusCode", "HTTP", 68); AddColumn("Title", "页面标题", 230);
            AddColumn("OriginalUrl", "原链接", 360); AddColumn("FinalUrl", "最终地址", 280);
            AddColumn("Evidence", "检测说明", 430); AddColumn("CheckedAt", "检测时间", 145);
            _grid.CellFormatting += GridCellFormatting;
            _grid.CellDoubleClick += delegate { OpenSelected(); };
        }

        private void AddColumn(string property, string header, int width)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.Automatic });
        }

        private async Task StartChecksAsync(bool resumeExisting, string launchMode = null)
        {
            List<CheckJob> jobs = BuildJobs();
            if (jobs.Count == 0) { MessageBox.Show("未找到有效链接。\n\n请粘贴以 http:// 或 https:// 开头的地址。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (_running) return;
            if (_networkMode.SelectedIndex == 1)
            {
                PrepareEdgeCompatibilityJobs(jobs, resumeExisting);
                return;
            }

            _running = true; _start.Enabled = false; _retryNetwork.Enabled = false; _stop.Enabled = true; _deepReview.Enabled = false; _remoteSettings.Enabled = false; _acceptancePack.Enabled = false; _import.Enabled = false; _clear.Enabled = false; _input.ReadOnly = true;
            _performance.Enabled = false;
            _networkMode.Enabled = false;
            _resume.Enabled = false;
            if (!resumeExisting) { _allRows.Clear(); _rows.Clear(); }
            CheckResult discarded;
            while (_uiResults.TryDequeue(out discarded)) { }
            RecalculateCounters(); UpdateStats(); _progress.Minimum = 0; _progress.Maximum = jobs.Count; _progress.Value = Math.Min(_allRows.Count, jobs.Count);
            _progressText.Text = "正在准备批量核验……"; _cancellation = new CancellationTokenSource();
            _runTotal = jobs.Count; _runCompleted = _allRows.Count; _runStartCompleted = _allRows.Count; _runWatch = Stopwatch.StartNew(); _animationFrame = 0;
            SaveSessionSafe();
            _performanceProfile = PerformanceProfile.Resolve(Convert.ToString(_performance.SelectedItem));
            _animationTimer.Interval = _performanceProfile.RefreshMilliseconds;
            _animationTimer.Start();

            var checker = new Checker(_performanceProfile.BodyBytes);
            var completedKeys = new HashSet<string>(_allRows.Select(ResultKey), StringComparer.OrdinalIgnoreCase);
            var pendingJobs = jobs.Where(job => !completedKeys.Contains(job.Key)).ToList();
            ExecutionLogContext executionLog = ExecutionLogContext.Start("快速核验",
                !String.IsNullOrWhiteSpace(launchMode) ? launchMode : resumeExisting ? "继续上次核验" : "开始核验",
                Convert.ToString(_performance.SelectedItem), Convert.ToString(_networkMode.SelectedItem),
                jobs.Count, _allRows.Count, pendingJobs.Count);
            var platformRestrictions = new PlatformRestrictionController(3);
            var infrastructureRestrictions = new InfrastructureRestrictionController(2);
            string circuitReason = "";
            string executionError = "";
            bool cancelled = false;
            int deferredJobs = 0;
            BatchPreflightSummary preflightSummary = new BatchPreflightSummary();
            try
            {
                _progressText.Text = "正在识别共享基础设施并安排访问节奏……";
                Dictionary<string, int> infrastructures =
                    await Checker.RegisterInfrastructureAsync(pendingJobs, _cancellation.Token);
                int sharedGroups = infrastructures.Count(item => item.Value > 1);
                executionLog.RecordEvent("基础设施识别完成：共 " + infrastructures.Count +
                    " 组，其中 " + sharedGroups + " 组承载多个链接");
                // Do not run a second sampled request pass before the real batch.
                // It duplicates traffic, can trigger platform throttling, and its
                // result used to defer rows that had not actually been checked.
                // The formal pass below is the only source of row-level truth.
                executionLog.RecordEvent("已跳过重复网络预检；直接对每条原链接执行正式取证");

                if (!cancelled)
                {
                    pendingJobs = pendingJobs
                        .OrderBy(BatchPreflightPlanner.PlatformKey, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(job => job.Number)
                        .ToList();
                    int nextJob = -1;
                    int workerCount = Math.Min(_performanceProfile.Workers, Math.Max(1, pendingJobs.Count));
                    var workers = Enumerable.Range(0, workerCount).Select(async workerNumber =>
                    {
                        while (true)
                        {
                            int index = Interlocked.Increment(ref nextJob);
                            if (index >= pendingJobs.Count) break;
                            CheckJob job = pendingJobs[index];
                            // Circuit breakers only record route health. They do
                            // not skip this URL: a real batch must never contain
                            // an unfinished placeholder for an unvisited row.
                            // Quick verification must not launch WebView2. The
                            // browser pass is manually started after this phase.
                            bool quickBrowser = false;
                            var item = await checker.CheckAsync(job.Url, job.Number, job.ExpectedTitle, job.ExpectedExcerpt, job.ExpectedAuthor, job.Platform, job.ContentType, quickBrowser, _cancellation.Token);
                            // Automatic batch verification stops after the local
                            // HTTP/API pass. Public-cloud, remote-node and
                            // browser evidence are started only by the explicit
                            // deep-review command.
                            item.SourceSheet = job.SourceSheet; item.SourceRow = job.SourceRow;
                            item.InfrastructureKey = job.InfrastructureKey;
                            _uiResults.Enqueue(item);
                            executionLog.Observe(item);
                            Interlocked.Increment(ref _runCompleted);
                            string pausedPlatform;
                            platformRestrictions.Observe(job, item, out pausedPlatform);
                            string pausedInfrastructure;
                            infrastructureRestrictions.Observe(job, item, out pausedInfrastructure);
                        }
                    }).ToArray();
                    await Task.WhenAll(workers);
                }
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex)
            {
                cancelled = true;
                executionError = ExecutionLogWriter.Safe(ex.Message, 300);
            }
            finally
            {
                if (!String.IsNullOrWhiteSpace(circuitReason)) cancelled = true;
                List<string> pausedPlatforms = platformRestrictions.PausedPlatforms;
                bool hasDeferredJobs = deferredJobs > 0 && !cancelled && String.IsNullOrWhiteSpace(circuitReason);
                bool interrupted = cancelled || !String.IsNullOrWhiteSpace(executionError);
                if (hasDeferredJobs)
                    executionLog.RecordEvent("本轮未中断其他站点；连续受限站点保留待重试 " + deferredJobs + " 条");
                _animationTimer.Stop();
                FlushUiResults(Int32.MaxValue);
                if (_runWatch != null) _runWatch.Stop();
                _allRows.Sort((a, b) => a.Number.CompareTo(b.Number));
                _running = false; ApplyFilter(); UpdateStats(); _start.Enabled = true; _stop.Enabled = false; _remoteSettings.Enabled = true; _import.Enabled = true; _clear.Enabled = true; _input.ReadOnly = false;
                _performance.Enabled = true;
                _networkMode.Enabled = true;
                RefreshResumeButton(); SaveSessionSafe();
                string executionOutcome = !String.IsNullOrWhiteSpace(executionError) ? "失败" :
                    !String.IsNullOrWhiteSpace(circuitReason) ? "暂停" :
                    hasDeferredJobs ? "完成并保留待重试" : cancelled ? "用户停止" : "完成";
                string executionReason = !String.IsNullOrWhiteSpace(executionError) ? executionError :
                    !String.IsNullOrWhiteSpace(circuitReason) ? circuitReason :
                    hasDeferredJobs ? "已处理可继续任务，另有 " + deferredJobs + " 条因站点持续受限保留待重试：" + String.Join("、", pausedPlatforms.Take(12)) :
                    cancelled ? "使用者停止或取消" : "所有计划任务已处理";
                CompleteExecutionLog(executionLog, executionOutcome, executionReason);
                _activity.Text = interrupted ? "■" : "✓";
                _activity.ForeColor = interrupted || hasDeferredJobs ? Color.FromArgb(180, 116, 20) : Color.FromArgb(22, 128, 85);
                double seconds = Math.Max(0.1, _runWatch == null ? 0.1 : _runWatch.Elapsed.TotalSeconds);
                double speed = Math.Max(0, (_runCompleted - _runStartCompleted) / seconds);
                _progressText.Text = !String.IsNullOrWhiteSpace(executionError)
                    ? "执行异常已停止  " + _runCompleted + " / " + jobs.Count + "，进度和日志已保存"
                    : !String.IsNullOrWhiteSpace(circuitReason)
                    ? "访问异常已暂停  " + _runCompleted + " / " + jobs.Count + "，进度已保存"
                    : hasDeferredJobs
                    ? "本轮已结束：已处理 " + _runCompleted + " / " + jobs.Count + "，另有 " + deferredJobs + " 条站点受限待重试（未中断其他站点）"
                    : cancelled
                    ? "已停止  " + _runCompleted + " / " + jobs.Count + "，进度已保存"
                    : "核验完成  " + _runCompleted + " 条，用时 " + FormatDuration(_runWatch.Elapsed) + "，平均 " + speed.ToString("0.0") + " 条/秒";
                _progressText.Text += "  ·  " + _performanceProfile.Name + "模式 / " + _performanceProfile.Workers + " 并发";
                if (_allRows.Count > _performanceProfile.GridRows) _progressText.Text += "（" + _performanceProfile.Name + "模式仅显示前 " + _performanceProfile.GridRows.ToString("N0") + " 条，导出包含全部）";
                if (!String.IsNullOrWhiteSpace(executionError))
                {
                    MessageBox.Show("核验执行发生异常，已经保存当前进度和执行日志：\n\n" + executionError +
                        "\n\n请点击“查看执行日志”并把日志发送给维护者。",
                        "执行异常", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else if (!interrupted && !hasDeferredJobs && _allRows.Count > 0)
                {
                    _progressText.Text = "基础核验完成，正在自动启动浏览器快速追证……";
                }
                else if (!String.IsNullOrWhiteSpace(circuitReason))
                {
                    MessageBox.Show("检测到" + circuitReason + "，工具已自动停止继续请求并保存进度。\n\n" +
                        "这些记录会显示为“访问异常待重试”，不是要求人工逐条复核。请先暂停一段时间或确认代理/目标站点恢复后，再点击“重试访问异常”。",
                        "访问异常已暂停", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else if (hasDeferredJobs)
                {
                    _progressText.Text = "基础核验完成，" + deferredJobs +
                        " 条共享基础设施异常已生成结果；正在自动处理其余可追证候选……";
                }
            }

            if (!cancelled && String.IsNullOrWhiteSpace(executionError))
            {
                // The quick pass must finish without silently starting a
                // second browser pass.  Users can launch the bounded browser
                // evidence stage from the review button when they choose.
                SaveSessionSafe();
                RecalculateCounters();
                ApplyFilter();
                UpdateStats();
                int unfinished = Math.Max(0, _allRows.Count - _removedTotal - _aliveTotal);
                _progressText.Text = "检测结束：共 " + _allRows.Count +
                    " 条；有效 " + _aliveTotal + "，失效 " + _removedTotal +
                    "，未完成 " + unfinished +
                    (unfinished > 0 ? "（可手动点击“自动补证”继续）" : "");
            }
        }

        private async Task<BatchPreflightSummary> RunBatchPreflightAsync(Checker checker, List<CheckJob> pendingJobs,
            PlatformRestrictionController platformRestrictions, ExecutionLogContext executionLog)
        {
            _preflightRunning = true;
            try
            {
                List<CheckJob> samples = BatchPreflightPlanner.SelectSamples(pendingJobs, 8, 2);
                var observations = new List<KeyValuePair<CheckJob, CheckResult>>();
                foreach (CheckJob job in samples)
                {
                    _progressText.Text = "正在进行网络预检  " + (observations.Count + 1) + " / " + samples.Count +
                        "  ·  " + BatchPreflightPlanner.PlatformKey(job);
                    CheckResult item = await checker.CheckAsync(job.Url, job.Number, job.ExpectedTitle, job.ExpectedExcerpt,
                        job.ExpectedAuthor, job.Platform, job.ContentType, false, _cancellation.Token);
                    if (NetworkRestrictionCircuitBreaker.IsTransientRestriction(item))
                        item = await checker.EscalateEvidenceAsync(item, _cancellation.Token);
                    item.SourceSheet = job.SourceSheet;
                    item.SourceRow = job.SourceRow;
                    item.InfrastructureKey = job.InfrastructureKey;
                    _uiResults.Enqueue(item);
                    executionLog.Observe(item);
                    Interlocked.Increment(ref _runCompleted);
                    string pausedPlatform;
                    platformRestrictions.Observe(job, item, out pausedPlatform);
                    observations.Add(new KeyValuePair<CheckJob, CheckResult>(job, item));
                    FlushUiResults(Int32.MaxValue);
                    BatchPreflightSummary interim = BatchPreflightPlanner.Analyze(observations);
                    if (interim.RequiresDecision) return interim;
                }
                return BatchPreflightPlanner.Analyze(observations);
            }
            finally { _preflightRunning = false; }
        }

        private void PrepareEdgeCompatibilityJobs(List<CheckJob> jobs, bool preserveExisting)
        {
            if (!preserveExisting) { _allRows.Clear(); _rows.Clear(); }
            var completedKeys = new HashSet<string>(_allRows.Select(ResultKey), StringComparer.OrdinalIgnoreCase);
            foreach (CheckJob job in jobs.Where(item => !completedKeys.Contains(item.Key)))
                _allRows.Add(CreateEdgeCompatibilityResult(job));
            _allRows.Sort((left, right) => left.Number.CompareTo(right.Number));
            RecalculateCounters(); ApplyFilter(); UpdateStats();
            _progress.Minimum = 0; _progress.Maximum = Math.Max(1, jobs.Count); _progress.Value = Math.Min(jobs.Count, _allRows.Count);
            RefreshReviewButton();
            _activity.Text = "✓"; _activity.ForeColor = Color.FromArgb(22, 128, 85);
            _progressText.Text = "浏览器兼容任务已建立：" + _allRows.Count + " / " + jobs.Count + " 条，进度已保存";
            SaveSessionSafe(); RefreshResumeButton();
            MessageBox.Show("浏览器兼容任务已经建立并保存，尚未自动开始浏览器核验。\n\n请点击“开始内置浏览器快速复核”，再点击右下角“开始内置浏览器复核（登录可选）”。登录不是前提，工具会先检查公开页面状态。",
                "等待手动开始", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        internal static CheckResult CreateEdgeCompatibilityResult(CheckJob job)
        {
            if (job == null) throw new ArgumentNullException("job");
            return new CheckResult
            {
                Number = job.Number,
                Verdict = "人工复核",
                StatusCode = "浏览器待核验",
                Title = "",
                OriginalUrl = job.Url,
                FinalUrl = "",
                Evidence = "浏览器兼容模式已跳过普通网络请求，等待手动启动内置浏览器快速复核",
                CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Duration = "0.0s",
                ExpectedTitle = job.ExpectedTitle ?? "",
                ExpectedExcerpt = job.ExpectedExcerpt ?? "",
                ExpectedAuthor = job.ExpectedAuthor ?? "",
                Platform = job.Platform ?? "",
                ContentType = String.IsNullOrWhiteSpace(job.ContentType) ? Checker.InferContentType(job.Platform, job.Url, job.ExpectedTitle) : job.ContentType,
                SourceSheet = job.SourceSheet,
                SourceRow = job.SourceRow,
                    DeepReviewed = false,
                    EdgeFastReviewed = false
                };
        }

        internal static CheckResult CreateInfrastructureDeferredResult(CheckJob job, string label,
            bool publiclyUnavailable = false)
        {
            if (job == null) throw new ArgumentNullException("job");
            string infrastructure = String.IsNullOrWhiteSpace(job.InfrastructureKey)
                ? BatchPreflightPlanner.PlatformKey(job) : job.InfrastructureKey;
            return new CheckResult
            {
                Number = job.Number,
                Verdict = publiclyUnavailable ? "公网不可访问" : "暂时异常",
                StatusCode = publiclyUnavailable ? "多出口不可达" : "基础设施异常",
                Title = "",
                OriginalUrl = job.Url,
                FinalUrl = "",
                Evidence = publiclyUnavailable
                    ? "自动多线路不可访问（内容状态未知）：同一共享基础设施已有多条代表链接经本机代理、直连、同站对照及独立公网云浏览器验证，均无法取得正常页面；本条复用该基础设施访问结论，不能证明删除或归责供应商"
                    : "同一基础设施已有多条链接完成自动追证，但均未取得正文或明确删除页；为避免继续集中请求，本条沿用该组访问异常并保留一键重试",
                CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Duration = "0.0s",
                ExpectedTitle = job.ExpectedTitle ?? "",
                ExpectedExcerpt = job.ExpectedExcerpt ?? "",
                ExpectedAuthor = job.ExpectedAuthor ?? "",
                Platform = job.Platform ?? "",
                ContentType = String.IsNullOrWhiteSpace(job.ContentType)
                    ? Checker.InferContentType(job.Platform, job.Url, job.ExpectedTitle) : job.ContentType,
                SourceSheet = job.SourceSheet,
                SourceRow = job.SourceRow,
                InfrastructureKey = infrastructure,
                SiteHealth = "基础设施组连续异常：" + (label ?? infrastructure),
                EvidenceStage = publiclyUnavailable ? "自动多线路不可访问记录复用" : "基础设施组追证已执行",
                AcquisitionAttempts = publiclyUnavailable ? "共享基础设施多出口证据复用" : "共享基础设施结果复用",
                SkipDeepReview = true,
                EvidenceTrail = new List<VerificationEvidence>
                {
                    new VerificationEvidence
                    {
                        Kind = EvidenceKind.TemporaryFailure,
                        Strength = EvidenceStrength.Strong,
                        Source = "infrastructure-circuit",
                        Platform = job.Platform ?? "",
                        Message = "同一基础设施连续自动追证失败，未获得目标正文或明确删除证据",
                        FinalUrl = "",
                        IsCurrentResponse = true
                    }
                }
            };
        }

        private void AnimationTick(object sender, EventArgs e)
        {
            FlushUiResults(800);
            _animationFrame++;
            string[] frames = new[] { "●", "◉", "◎", "◉" };
            _activity.Text = frames[_animationFrame % frames.Length];
            _activity.ForeColor = Color.FromArgb(38 + (_animationFrame % 3) * 18, 99, 177);
            int completed = Math.Min(_runCompleted, _runTotal);
            _progress.Value = Math.Min(completed, _progress.Maximum);
            if (_preflightRunning) return;
            double elapsed = Math.Max(0.1, _runWatch == null ? 0.1 : _runWatch.Elapsed.TotalSeconds);
            int completedThisRun = Math.Max(0, completed - _runStartCompleted);
            double speed = Math.Max(0.0, completedThisRun / elapsed);
            TimeSpan eta = speed > 0.05 ? TimeSpan.FromSeconds(Math.Max(0, _runTotal - completed) / speed) : TimeSpan.Zero;
            int percent = _runTotal == 0 ? 0 : (int)Math.Round(completed * 100.0 / _runTotal);
            _progressText.Text = "高速核验中 " + percent + "%  ·  " + completed.ToString("N0") + " / " + _runTotal.ToString("N0") +
                "  ·  " + speed.ToString("0.0") + " 条/秒" + (eta > TimeSpan.Zero ? "  ·  预计剩余 " + FormatDuration(eta) : "");
        }

        private void FlushUiResults(int maximum)
        {
            int added = 0;
            CheckResult item;
            var batch = new List<CheckResult>();
            _rows.RaiseListChangedEvents = false;
            while (added < maximum && _uiResults.TryDequeue(out item))
            {
                item.Verdict = Checker.NormalizeVisibleVerdict(item.Verdict);
                ContractAcceptanceClassifier.Apply(item);
                _allRows.Add(item);
                if (_rows.Count < _performanceProfile.GridRows && ShouldDisplay(item)) _rows.Add(item);
                if (item.Verdict == "已失效") _removedTotal++;
                else if (item.Verdict == "仍可访问") _aliveTotal++;
                else if (item.Verdict == "公网不可访问") _unavailableTotal++;
                else if (item.Verdict == "暂时异常") _temporaryTotal++;
                else _reviewTotal++;
                batch.Add(item);
                added++;
            }
            _rows.RaiseListChangedEvents = true;
            if (added > 0)
            {
                try { SessionStore.AppendBatch(batch); }
                catch (Exception ex) { _progressText.Text = "进度自动保存失败：" + ex.Message; }
                _rows.ResetBindings(); UpdateStats();
            }
        }

        private void RecalculateCounters()
        {
            foreach (CheckResult item in _allRows)
            {
                item.Verdict = Checker.NormalizeVisibleVerdict(item.Verdict);
                ContractAcceptanceClassifier.Apply(item);
            }
            _removedTotal = _allRows.Count(item => item.Verdict == "已失效");
            _aliveTotal = _allRows.Count(item => item.Verdict == "仍可访问");
            _unavailableTotal = _allRows.Count(item => item.Verdict == "公网不可访问");
            _temporaryTotal = _allRows.Count(item => item.Verdict == "暂时异常");
            _reviewTotal = _allRows.Count - _removedTotal - _aliveTotal -
                _unavailableTotal - _temporaryTotal;
        }

        private bool ShouldDisplay(CheckResult item)
        {
            int selected = _filter.SelectedIndex;
            return selected == 0 ||
                (selected == 1 && item.Verdict == "仍可访问") ||
                (selected == 2 && item.Verdict == "已失效") ||
                (selected == 3 && item.Verdict != "已失效" && item.Verdict != "仍可访问");
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value.TotalHours >= 1) return ((int)value.TotalHours) + "小时" + value.Minutes + "分";
            if (value.TotalMinutes >= 1) return ((int)value.TotalMinutes) + "分" + value.Seconds + "秒";
            return Math.Max(0, (int)value.TotalSeconds) + "秒";
        }

        internal static bool IsEvidenceReviewCandidate(CheckResult item)
        {
            return item != null && !item.SkipDeepReview &&
                (item.Verdict == "人工复核" || item.Verdict == "疑似已处置" ||
                 (item.Verdict == "暂时异常" &&
                  String.Equals(item.SiteHealth, "站点首页可访问", StringComparison.OrdinalIgnoreCase)));
        }

        internal static bool IsFastEvidenceReviewCandidate(CheckResult item)
        {
            return item != null && !item.SkipDeepReview &&
                item.Verdict != "已失效" && item.Verdict != "仍可访问" &&
                DeepReviewForm.ShouldFastRenderPlatform(item);
        }

        private void RunDeepReview(bool automatic)
        {
            var reviewable = _allRows.Where(IsEvidenceReviewCandidate).OrderBy(item => item.Number).ToList();
            var pending = reviewable.Where(item => !item.DeepReviewed).ToList();
            if (pending.Count == 0)
            {
                if (automatic) return;
                if (reviewable.Count == 0)
                {
                    if (!automatic) MessageBox.Show("当前没有需要深度复核的链接。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult retry = MessageBox.Show("当前待复核链接都已经完成过一次深度复核，但仍没有足够证据。\n\n是否重新复核这些链接？",
                    "重新深度复核", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (retry != DialogResult.Yes) return;
                foreach (CheckResult item in reviewable) item.DeepReviewed = false;
                pending = reviewable;
            }
            int beforeResolved = pending.Count(item => item.Verdict == "已失效" || item.Verdict == "仍可访问");
            ExecutionLogContext executionLog = ExecutionLogContext.Start("后台深度复核",
                automatic ? "自动启动" : "手动启动", Convert.ToString(_performance.SelectedItem),
                Convert.ToString(_networkMode.SelectedItem), _allRows.Count, _allRows.Count - pending.Count, pending.Count);
            using (var form = new DeepReviewForm(pending, SaveDeepReviewProgress, false, automatic))
                form.ShowDialog(this);
            foreach (CheckResult item in pending.Where(item => item.DeepReviewed)) executionLog.Observe(item);
            RecalculateCounters(); ApplyFilter(); UpdateStats();
            SaveSessionSafe();
            int newlyResolved = pending.Count(item => item.Verdict == "已失效" || item.Verdict == "仍可访问") - beforeResolved;
            int remaining = _allRows.Count(item => item.Verdict == "人工复核" || item.Verdict == "疑似已处置");
            _progressText.Text = "后台复核结束：新增自动确认 " + Math.Max(0, newlyResolved) + " 条，证据仍待复核 " + remaining + " 条";
            CompleteExecutionLog(executionLog,
                pending.All(item => item.DeepReviewed) ? "完成" : "部分完成",
                "处理 " + executionLog.ObservedItems.Count + " / " + pending.Count + " 条，新增自动确认 " + Math.Max(0, newlyResolved) + " 条");
        }

        private void RunSelectedReview()
        {
            bool hasFastPending = _allRows.Any(item =>
                IsFastEvidenceReviewCandidate(item) && !item.EdgeFastReviewed);
            if (hasFastPending) RunEdgeFastReview();
            else RunDeepReview(false);
        }

        private void ShowAiSettings()
        {
            if (_running) return;
            using (var form = new AiSettingsForm()) form.ShowDialog(this);
            UpdateStats();
        }

        private async Task RunAiReviewAsync(bool automatic = false)
        {
            if (_running) return;
            AiRuntimeSettings settings = AiSettingsStore.Load();
            if (String.IsNullOrWhiteSpace(settings.Token) || String.IsNullOrWhiteSpace(settings.Model))
            {
                if (automatic) return;
                using (var form = new AiSettingsForm())
                {
                    if (form.ShowDialog(this) != DialogResult.OK || !form.SettingsSaved) return;
                }
                settings = AiSettingsStore.Load();
            }

            List<CheckResult> candidates = _allRows.Where(AiReviewPolicy.IsEligible).OrderBy(item => item.Number).ToList();
            if (candidates.Count == 0)
            {
                if (automatic) return;
                int withoutContext = _allRows.Count(item =>
                    (item.Verdict == "人工复核" || item.Verdict == "疑似已处置") &&
                    !NetworkRestrictionCircuitBreaker.IsTransientRestriction(item) &&
                    String.IsNullOrWhiteSpace(item.AnalysisContext));
                MessageBox.Show(withoutContext > 0
                    ? "当前结果没有可发送给 AI 的页面正文摘要。\n\n请使用当前版本重新核验这些待复核链接；新版会在本地保存必要的可见页面摘要，再由 AI 辅助判断。"
                    : "当前没有适合 AI 辅助复核的结果。\n\n网络异常、验证码、登录页和已经确定的结果不会发送给 AI。",
                    "暂无 AI 复核候选", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!automatic)
            {
                DialogResult answer = MessageBox.Show("将把 " + candidates.Count + " 条记录的链接、标题、作者、HTTP 状态、机器证据和可见正文摘要发送到 Yunwu API。\n\n" +
                    "不会发送 Cookie、账号、完整 Excel 或浏览器凭证。处理数量不设硬上限，可能产生 API 费用。\n\n是否继续？",
                    "开始 AI 辅助复核", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }

            _running = true;
            _cancellation = new CancellationTokenSource();
            SetAiReviewBusy(true);
            ExecutionLogContext executionLog = ExecutionLogContext.Start("AI辅助复核", automatic ? "自动启动" : "手动启动",
                Convert.ToString(_performance.SelectedItem), Convert.ToString(_networkMode.SelectedItem),
                _allRows.Count, _allRows.Count - candidates.Count, candidates.Count);
            string executionOutcome = "完成";
            string executionReason = "";
            int processed = 0;
            int resolved = 0;
            int failed = 0;
            int consecutiveFailures = 0;
            bool batchPaused = false;
            try
            {
                using (var client = new YunwuAiClient(settings.Token))
                {
                    foreach (CheckResult item in candidates)
                    {
                        _cancellation.Token.ThrowIfCancellationRequested();
                        _progressText.Text = "AI 辅助复核  " + (processed + 1) + " / " + candidates.Count +
                            "  ·  " + (String.IsNullOrWhiteSpace(item.Platform) ? "未知平台" : item.Platform);
                        AiReviewDecision decision = null;
                        Exception lastError = null;
                        int attempts = 0;
                        while (attempts < AiBatchPolicy.MaximumAttemptsPerItem && decision == null)
                        {
                            attempts++;
                            try
                            {
                                decision = await client.ReviewAsync(settings, item, _cancellation.Token);
                            }
                            catch (OperationCanceledException ex)
                            {
                                if (_cancellation.IsCancellationRequested) throw;
                                lastError = ex;
                            }
                            catch (Exception ex)
                            {
                                lastError = ex;
                            }

                            if (decision != null || lastError == null || AiBatchPolicy.IsFatal(lastError) ||
                                !AiBatchPolicy.CanRetry(lastError, attempts)) break;
                            int delay = AiBatchPolicy.RetryDelayMilliseconds(lastError, attempts);
                            _progressText.Text = "AI 第 " + (processed + 1) + " 条暂时失败，" +
                                (delay / 1000.0).ToString("0.0") + " 秒后重试（" + attempts + " / " +
                                AiBatchPolicy.MaximumAttemptsPerItem + "）";
                            await Task.Delay(delay, _cancellation.Token);
                        }

                        item.AiAttemptCount += attempts;
                        if (decision == null)
                        {
                            string safeError = ExecutionLogWriter.Safe(lastError == null ? "未知 AI 错误" : lastError.Message, 300);
                            item.AiLastError = safeError;
                            failed++;
                            processed++;
                            consecutiveFailures++;
                            executionLog.Observe(item);
                            executionLog.RecordAiFailure(attempts,
                                "第 " + item.Number + " 条 AI 调用失败（连续 " + consecutiveFailures + " 条）：" + safeError);
                            SessionStore.Append(item);
                            RecalculateCounters();
                            UpdateStats();

                            if (lastError != null && AiBatchPolicy.IsFatal(lastError))
                            {
                                executionOutcome = "失败";
                                executionReason = "AI 配置或账户错误，已停止批次：" + safeError;
                                batchPaused = true;
                                break;
                            }
                            if (AiBatchPolicy.ShouldPauseBatch(consecutiveFailures))
                            {
                                executionOutcome = "暂停";
                                executionReason = "AI 连续 " + consecutiveFailures + " 条调用失败，已暂停避免继续消耗额度；最后错误：" + safeError;
                                batchPaused = true;
                                break;
                            }
                            continue;
                        }

                        item.AiLastError = "";
                        AiReviewApplication application = AiReviewPolicy.Apply(item, decision, settings.Model);
                        executionLog.RecordAiSuccess(attempts);
                        executionLog.Observe(item);
                        if (application.Resolved) resolved++;
                        processed++;
                        consecutiveFailures = 0;
                        SessionStore.Append(item);
                        RecalculateCounters();
                        ApplyFilter();
                        UpdateStats();
                        await Task.Delay(450, _cancellation.Token);
                    }
                }
                int remainingEligible = _allRows.Count(AiReviewPolicy.IsEligible);
                if (String.IsNullOrWhiteSpace(executionReason))
                    executionReason = "尝试 " + processed + " / " + candidates.Count + " 条，成功 " +
                        Math.Max(0, processed - failed) + " 条，失败 " + failed + " 条，新增自动确认 " + resolved + " 条";
                _progressText.Text = (batchPaused ? "AI 辅助复核已暂停：" : "AI 辅助复核完成：") +
                    "尝试 " + processed + " 条，成功 " + Math.Max(0, processed - failed) +
                    " 条，失败 " + failed + " 条，仍可稍后处理 " + remainingEligible + " 条";
                if (!automatic || batchPaused)
                    MessageBox.Show((batchPaused ? "AI 辅助复核已安全暂停。" : "AI 辅助复核完成。") +
                        "\n\n尝试：" + processed + " 条\n成功：" + Math.Max(0, processed - failed) +
                        " 条\n失败：" + failed + " 条\n新增自动确认：" + resolved +
                        " 条\n仍可稍后处理：" + remainingEligible + " 条" +
                        (batchPaused ? "\n\n原因：" + executionReason : ""),
                        batchPaused ? "AI 复核已暂停" : "AI 复核完成", MessageBoxButtons.OK,
                        batchPaused ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                executionOutcome = "用户停止";
                executionReason = "使用者停止 AI 辅助复核";
                _progressText.Text = "AI 辅助复核已停止，已处理 " + processed + " / " + candidates.Count + " 条，进度已保存";
            }
            catch (Exception ex)
            {
                executionOutcome = "失败";
                executionReason = ex.Message;
                _progressText.Text = "AI 辅助复核失败，已处理结果仍已保存";
                MessageBox.Show("AI 辅助复核失败：\n" + Regex.Replace(ex.Message ?? "", @"sk-[A-Za-z0-9_\-]+", "[Token已隐藏]"),
                    "AI API 错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _running = false;
                SetAiReviewBusy(false);
                SaveSessionSafe();
                RecalculateCounters();
                ApplyFilter();
                UpdateStats();
                if (String.IsNullOrWhiteSpace(executionReason))
                    executionReason = "尝试 " + processed + " / " + candidates.Count + " 条，成功 " +
                        Math.Max(0, processed - failed) + " 条，失败 " + failed + " 条，新增自动确认 " + resolved + " 条";
                CompleteExecutionLog(executionLog, executionOutcome, executionReason);
            }
        }

        private void SetAiReviewBusy(bool busy)
        {
            _start.Enabled = !busy;
            _stop.Enabled = busy;
            _import.Enabled = !busy;
            _resume.Enabled = !busy && SessionStore.Exists;
            _retryNetwork.Enabled = !busy && GetSavedRetryCount() > 0;
            _clear.Enabled = !busy;
            RefreshReviewButton();
            _aiSettings.Enabled = !busy;
            _aiReview.Enabled = !busy && _allRows.Any(AiReviewPolicy.IsEligible);
            _export.Enabled = !busy;
            _acceptancePack.Enabled = !busy && _allRows.Count > 0;
            _input.ReadOnly = busy;
            _performance.Enabled = !busy;
            _networkMode.Enabled = !busy;
        }

        private void RunEdgeFastReview(bool automatic = false)
        {
            var pending = _allRows.Where(item => IsFastEvidenceReviewCandidate(item) && !item.EdgeFastReviewed)
                .OrderBy(item => item.Number).ToList();
            if (pending.Count == 0)
            {
                if (!automatic) RunDeepReview(false);
                return;
            }
            ExecutionLogContext executionLog = ExecutionLogContext.Start("浏览器快速复核", automatic ? "快速核验自动追证" : "手动启动",
                Convert.ToString(_performance.SelectedItem), Convert.ToString(_networkMode.SelectedItem),
                _allRows.Count, _allRows.Count - pending.Count, pending.Count);
            using (var form = new DeepReviewForm(pending, SaveDeepReviewProgress, true, automatic)) form.ShowDialog(this);
            foreach (CheckResult item in pending.Where(item => item.EdgeFastReviewed)) executionLog.Observe(item);
            RecalculateCounters(); ApplyFilter(); UpdateStats(); SaveSessionSafe();
            int remaining = _allRows.Count(item => item.Verdict == "人工复核" || item.Verdict == "疑似已处置");
            int notFastReviewed = _allRows.Count(item =>
                IsFastEvidenceReviewCandidate(item) && !item.EdgeFastReviewed);
            _progressText.Text = "内置浏览器快速复核结束：已处理 " + executionLog.ObservedItems.Count +
                " 条，证据仍待复核 " + remaining + " 条";
            CompleteExecutionLog(executionLog,
                pending.All(item => item.EdgeFastReviewed) ? "完成" : "部分完成",
                "处理 " + executionLog.ObservedItems.Count + " / " + pending.Count + " 条，仍需复核 " + remaining + " 条");
        }

        private string CompleteExecutionLog(ExecutionLogContext context, string outcome, string reason)
        {
            if (context == null) return "";
            try
            {
                context.EndedAt = DateTime.Now;
                context.Outcome = outcome ?? "";
                context.StopReason = reason ?? "";
                context.RecordEvent("任务结束：" + context.Outcome + "；" + context.StopReason);
                string path = ExecutionLogWriter.Write(context, _allRows);
                _openLog.Enabled = File.Exists(path);
                return path;
            }
            catch (Exception ex)
            {
                _progressText.Text = "执行已结束，但诊断日志生成失败：" + ex.Message;
                return "";
            }
        }

        private string GenerateAcceptanceEvidencePackage(string runId, bool showMessage)
        {
            if (_allRows.Count == 0) return "";
            try
            {
                AcceptanceEvidencePackage package = AcceptanceEvidencePackageWriter.Write(_allRows, runId);
                _acceptancePack.Tag = package.ZipPath;
                if (showMessage)
                {
                    MessageBox.Show(
                        "验收证据包已生成：\n" + package.ZipPath +
                        "\n\n内容状态已确认：" + package.ContentResolved + " / " + package.Total +
                        "\n待独立普通网络复核：" + package.IndependentReviewRequired +
                        "\n待补证/人工复核：" + package.SupplementRequired +
                        "\n\n注意：证据包包含完整业务链接，发送给供应商前请按单位的数据管理要求处理。",
                        "验收证据包已生成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return package.ZipPath;
            }
            catch (Exception ex)
            {
                if (showMessage)
                    MessageBox.Show("验收证据包生成失败：\n" + ex.Message,
                        "生成失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    _progressText.Text = "核验已完成，但验收证据包生成失败：" + ex.Message;
                return "";
            }
        }

        private void OpenLatestExecutionLog()
        {
            string path = ExecutionLogWriter.LatestLogPath;
            if (!File.Exists(path))
            {
                MessageBox.Show("还没有执行日志。完成一次快速核验、后台复核或 AI 复核后会自动生成。",
                    "暂无执行日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开执行日志：\n" + ex.Message + "\n\n日志位置：\n" + path,
                    "打开日志失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SaveSessionSafe()
        {
            try { SessionStore.Save(_input.Text, _excelPath, BuildJobs(), _allRows); }
            catch (Exception ex) { _progressText.Text = "进度自动保存失败：" + ex.Message; }
        }

        private void SaveDeepReviewProgress(CheckResult item)
        {
            try
            {
                if (item != null)
                {
                    item.Verdict = Checker.NormalizeVisibleVerdict(item.Verdict);
                    ContractAcceptanceClassifier.Apply(item);
                }
                SessionStore.Append(item);
            }
            catch (Exception ex) { _progressText.Text = "深度复核进度保存失败：" + ex.Message; }
        }

        private void RefreshResumeButton()
        {
            _resume.Enabled = !_running && SessionStore.Exists;
            _resume.Text = SessionStore.Exists ? "恢复上次进度" : "暂无上次进度";
            string description = SessionStore.Describe();
            _resume.Tag = description;
            int retryCount = GetSavedRetryCount();
            _retryNetwork.Text = retryCount > 0 ? "继续未完成（" + retryCount + "）" : "继续未完成";
            _retryNetwork.Enabled = !_running && retryCount > 0;
            _retryNetwork.Tag = retryCount > 0
                ? "继续检测尚未得到有效或失效结论的链接，共 " + retryCount + " 条"
                : "当前没有未完成链接";
        }

        private int GetSavedRetryCount()
        {
            if (!SessionStore.Exists) return 0;
            try
            {
                CheckSession session = SessionStore.Load();
                if (session == null) return 0;
                int unfinished = (session.Results ?? new List<CheckResult>())
                    .Count(item => ShouldDiscardForResume(item, false));
                int missing = Math.Max(0, (session.Jobs ?? new List<CheckJob>()).Count -
                    (session.Results ?? new List<CheckResult>()).Count);
                return unfinished + missing;
            }
            catch { return 0; }
        }

        private async Task ResumeLastSessionAsync(bool retryNetworkOnly = false)
        {
            if (_running || !SessionStore.Exists) return;
            try
            {
                CheckSession session = SessionStore.Load();
                if (session == null) return;
                bool engineChanged = !String.Equals(session.EngineVersion, SessionStore.CurrentEngineVersion, StringComparison.OrdinalIgnoreCase);
                _importJobs = session.Jobs == null ? new List<CheckJob>() : session.Jobs.OrderBy(item => item.Number).ToList();
                if (_importJobs.Count == 0 && LooksLikeCsvContent(session.InputText))
                    _importJobs = LoadCsvJobsFromContent(session.InputText, "CSV");
                _input.Text = _importJobs.Count > 0
                    ? String.Join(Environment.NewLine, _importJobs.Select(item => item.Url))
                    : session.InputText ?? "";
                _allRows.Clear();
                _allRows.AddRange(session.Results ?? new List<CheckResult>());
                RecalculateCounters();
                _excelPath = null;
                _excelPlans.Clear();
                if (!String.IsNullOrWhiteSpace(session.ExcelPath) && File.Exists(session.ExcelPath))
                {
                    _excelPath = session.ExcelPath;
                    _excelPlans = OpenXmlExcelBridge.LoadPlans(_excelPath);
                    _importJobs.Clear();
                    _export.Text = "写回原 Excel";
                }
                else _export.Text = "导出结果";
                List<CheckJob> restoredJobs = BuildJobs();
                var validKeys = new HashSet<string>(restoredJobs.Select(job => job.Key), StringComparer.OrdinalIgnoreCase);
                _allRows.RemoveAll(item => !validKeys.Contains(ResultKey(item)));
                int savedUnfinished = _allRows.Count(item => ShouldDiscardForResume(item, false));
                int savedMissing = Math.Max(0, restoredJobs.Count - _allRows.Count);
                if (retryNetworkOnly)
                {
                    int retryTotal = savedUnfinished + savedMissing;
                    if (retryTotal <= 0)
                    {
                        ApplyFilter(); UpdateStats(); RefreshResumeButton();
                        MessageBox.Show("当前没有未完成链接。", "无需续检",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    DialogResult retryDecision = MessageBox.Show(
                        "将重新执行 " + retryTotal + " 条链接：\n\n" +
                        "上次未完成 " + savedUnfinished + " 条；上次尚未处理 " + savedMissing + " 条。\n\n" +
                        "续检会重新建立访问会话并自动重试，不会把 403、429、502 或超时直接判成失效。\n\n是否现在继续？",
                        "继续未完成", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button1);
                    if (retryDecision != DialogResult.Yes)
                    {
                        ApplyFilter(); UpdateStats(); RefreshResumeButton();
                        return;
                    }
                    _allRows.RemoveAll(item => ShouldDiscardForResume(item, false));
                }
                int transientRetries = 0;
                if (!retryNetworkOnly && engineChanged)
                    _allRows.RemoveAll(item => ShouldDiscardResultForEngineUpgrade(item, session.EngineVersion));
                else if (!retryNetworkOnly)
                    transientRetries = _allRows.RemoveAll(item => ShouldDiscardForResume(item, false));
                ApplyFilter(); UpdateStats();
                RefreshReviewButton();
                int total = restoredJobs.Count;
                _progressText.Text = "已恢复上次进度：" + _allRows.Count + " / " + total + " 条" +
                    (retryNetworkOnly ? "；将续检 " + (savedUnfinished + savedMissing) + " 条未完成链接" :
                    engineChanged ? "；规则已升级，将自动重跑旧版不兼容结论" :
                    transientRetries > 0 ? "；将续检 " + transientRetries + " 条未完成链接" : "");
                if (_allRows.Count < total) await StartChecksAsync(true, retryNetworkOnly ? "继续未完成" : "继续上次检测");
                else MessageBox.Show("上次检测已经全部得到有效或失效结论。", "进度已恢复", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法恢复上次进度：\n" + ex.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async void MainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_closeRequested) return;
            if (_running)
            {
                e.Cancel = true;
                _closeRequested = true;
                Enabled = false;
                _progressText.Text = "正在停止并保存最后一批结果……";
                if (_cancellation != null) _cancellation.Cancel();
                while (_running)
                {
                    FlushUiResults(Int32.MaxValue);
                    await Task.Delay(80);
                }
                FlushUiResults(Int32.MaxValue);
                SaveSessionSafe();
                BeginInvoke(new Action(Close));
                return;
            }
            FlushUiResults(Int32.MaxValue);
            if (_input.TextLength > 0 || _allRows.Count > 0) SaveSessionSafe();
        }

        private static List<string> ExtractUrls(string text)
        {
            var result = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matches = Regex.Matches(text ?? "", @"https?://[^\s\""'<>\uff0c\uff1b]+", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                string url = match.Value.Trim().TrimEnd('.', ',', ';', ')', ']', '}', '。', '，', '；');
                if (seen.Add(url)) result.Add(url);
            }
            return result;
        }

        internal static List<CheckJob> LoadCsvJobs(string path)
        {
            Encoding encoding = DetectFileEncoding(path);
            string content = File.ReadAllText(path, encoding);
            return LoadCsvJobsFromContent(content, "CSV");
        }

        private static bool LooksLikeCsvContent(string content)
        {
            if (String.IsNullOrWhiteSpace(content)) return false;
            string firstLine = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return firstLine.IndexOf(",", StringComparison.Ordinal) >= 0 &&
                (firstLine.IndexOf("链接", StringComparison.OrdinalIgnoreCase) >= 0 || firstLine.IndexOf("url", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static List<CheckJob> LoadCsvJobsFromContent(string content, string sourceName)
        {
            List<List<string>> rows = ParseCsvRows(content);
            if (rows.Count == 0) return new List<CheckJob>();

            List<string> headers = rows[0].Select(NormalizeCsvHeader).ToList();
            int linkColumn = FindCsvColumn(headers, new[] { "链接", "url", "网址", "文章链接", "原链接", "发布链接" });
            if (linkColumn < 0) throw new InvalidDataException("CSV 中没有找到“链接/URL”列。");
            int titleColumn = FindCsvColumn(headers, new[] { "标题", "文章标题", "内容标题", "title" });
            int excerptColumn = FindCsvColumn(headers, new[] { "摘要", "内容摘要", "正文摘要", "excerpt", "summary" });
            int authorColumn = FindCsvColumn(headers, new[] { "账号昵称", "作者", "发文作者", "发布账号", "发布人", "发布者", "账号名称", "昵称", "账号", "author" });
            int platformColumn = FindCsvColumn(headers, new[] { "平台", "平台名称", "发布平台", "来源平台", "platform" });
            int contentTypeColumn = FindCsvColumn(headers, new[] { "内容类型", "信息类型", "媒体类型", "类型", "contenttype", "type" });
            int sourceSheetColumn = FindCsvColumn(headers, new[] { "来源工作表", "源工作表", "sourcesheet" });
            int sourceRowColumn = FindCsvColumn(headers, new[] { "来源行号", "源行号", "sourcerow" });

            var jobs = new List<CheckJob>();
            for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
            {
                List<string> row = rows[rowIndex];
                string url = CsvValue(row, linkColumn).Trim();
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https")) continue;
                int sourceRow;
                if (!Int32.TryParse(CsvValue(row, sourceRowColumn).Trim(), out sourceRow) || sourceRow <= 0)
                    sourceRow = rowIndex + 1;
                string sourceSheet = CsvValue(row, sourceSheetColumn).Trim();
                jobs.Add(new CheckJob
                {
                    Number = jobs.Count + 1,
                    Url = url,
                    ExpectedTitle = CsvValue(row, titleColumn),
                    ExpectedExcerpt = CsvValue(row, excerptColumn),
                    ExpectedAuthor = CsvValue(row, authorColumn),
                    Platform = CsvValue(row, platformColumn),
                    ContentType = String.IsNullOrWhiteSpace(CsvValue(row, contentTypeColumn))
                        ? Checker.InferContentType(CsvValue(row, platformColumn), url, CsvValue(row, titleColumn))
                        : CsvValue(row, contentTypeColumn),
                    SourceSheet = String.IsNullOrWhiteSpace(sourceSheet)
                        ? (String.IsNullOrWhiteSpace(sourceName) ? "CSV" : sourceName)
                        : sourceSheet,
                    SourceRow = sourceRow
                });
            }
            return jobs;
        }

        private static List<List<string>> ParseCsvRows(string content)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            string text = content ?? "";
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                if (quoted)
                {
                    if (character == '"')
                    {
                        if (index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index++; }
                        else quoted = false;
                    }
                    else field.Append(character);
                }
                else if (character == '"') quoted = true;
                else if (character == ',') { row.Add(field.ToString()); field.Clear(); }
                else if (character == '\r' || character == '\n')
                {
                    if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                    row.Add(field.ToString()); field.Clear();
                    if (row.Any(value => !String.IsNullOrWhiteSpace(value))) rows.Add(row);
                    row = new List<string>();
                }
                else field.Append(character);
            }
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                if (row.Any(value => !String.IsNullOrWhiteSpace(value))) rows.Add(row);
            }
            return rows;
        }

        private static string NormalizeCsvHeader(string value)
        {
            return (value ?? "").Trim().TrimStart('\uFEFF').Replace(" ", "").ToLowerInvariant();
        }

        private static int FindCsvColumn(List<string> headers, IEnumerable<string> names)
        {
            var expected = new HashSet<string>(names.Select(NormalizeCsvHeader), StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < headers.Count; index++) if (expected.Contains(headers[index])) return index;
            return -1;
        }

        private static string CsvValue(List<string> row, int column)
        {
            return column >= 0 && row != null && column < row.Count ? row[column] ?? "" : "";
        }

        private List<CheckJob> BuildJobs()
        {
            var jobs = new List<CheckJob>();
            int number = 1;
            if (!String.IsNullOrEmpty(_excelPath) && _excelPlans.Count > 0)
            {
                foreach (ExcelSheetPlan plan in _excelPlans)
                    foreach (ExcelLinkSource source in plan.Sources.Where(item => !item.ManualOnly && !String.IsNullOrWhiteSpace(item.Url)))
                        jobs.Add(new CheckJob
                        {
                            Number = number++, Url = source.Url, ExpectedTitle = source.ExpectedTitle ?? "", ExpectedExcerpt = source.ExpectedExcerpt ?? "", ExpectedAuthor = source.ExpectedAuthor ?? "", Platform = source.Platform ?? "", ContentType = String.IsNullOrWhiteSpace(source.ContentType) ? Checker.InferContentType(source.Platform, source.Url, source.ExpectedTitle) : source.ContentType,
                            SourceSheet = plan.SheetName, SourceRow = source.Row
                        });
                return jobs;
            }
            if (_importJobs.Count > 0) return _importJobs.OrderBy(item => item.Number).ToList();
            if (LooksLikeCsvContent(_input.Text)) return LoadCsvJobsFromContent(_input.Text, "CSV");
            foreach (string url in ExtractUrls(_input.Text)) jobs.Add(new CheckJob { Number = number++, Url = url, ExpectedTitle = "" });
            return jobs;
        }

        private static string ResultKey(CheckResult result)
        {
            return result == null ? "" : CheckIdentity.Create(result.SourceSheet, result.SourceRow, result.OriginalUrl);
        }

        internal static bool ShouldDiscardForResume(CheckResult result, bool engineChanged)
        {
            if (result == null) return false;
            return result.Verdict != "已失效" && result.Verdict != "仍可访问";
        }

        internal static bool ShouldDiscardResultForEngineUpgrade(CheckResult item, string previousEngineVersion)
        {
            if (item == null) return false;
            if (item.Verdict != "已失效" && item.Verdict != "仍可访问") return true;
            return IsEngineOlderThan(previousEngineVersion, 4, 4, 2);
        }

        internal static bool IsEngineOlderThan(string version, int major, int minor, int patch)
        {
            Version current;
            if (!Version.TryParse(version ?? "", out current)) return true;
            return current < new Version(major, minor, patch);
        }

        private void ApplyFilter()
        {
            if (_running) return;
            int selected = _filter.SelectedIndex; _rows.RaiseListChangedEvents = false; _rows.Clear();
            foreach (var item in _allRows.OrderBy(r => r.Number))
            {
                if (_rows.Count >= (_performanceProfile == null ? 2500 : _performanceProfile.GridRows)) break;
                if (ShouldDisplay(item)) _rows.Add(item);
            }
            _rows.RaiseListChangedEvents = true; _rows.ResetBindings();
        }

        private void UpdateStats()
        {
            _allCount.Text = _allRows.Count.ToString();
            _removedCount.Text = _removedTotal.ToString();
            _aliveCount.Text = _aliveTotal.ToString();
            _unavailableCount.Text = Math.Max(0, _allRows.Count - _removedTotal - _aliveTotal).ToString();
            _temporaryCount.Text = _temporaryTotal.ToString();
            _reviewCount.Text = _reviewTotal.ToString();
            _aiSettings.Enabled = !_running;
            _aiReview.Enabled = !_running && _allRows.Any(AiReviewPolicy.IsEligible);
            _openLog.Enabled = File.Exists(ExecutionLogWriter.LatestLogPath);
            _acceptancePack.Enabled = !_running && _allRows.Count > 0;
            RefreshReviewButton();
        }

        private void RefreshReviewButton()
        {
            int candidateCount = _allRows.Count(item =>
                IsEvidenceReviewCandidate(item) || IsFastEvidenceReviewCandidate(item));
            _deepReview.Text = ReviewButtonText(candidateCount);
            _deepReview.Enabled = !_running && candidateCount > 0;
            _deepReview.Tag = candidateCount > 0
                ? "手动启动浏览器补证，共 " + candidateCount + " 条；不会由基础核验自动触发"
                : "当前没有可自动补证的链接";
        }

        internal static string ReviewButtonText(int candidateCount)
        {
            return candidateCount > 0 ? "自动补证（" + candidateCount + "）" : "自动补证";
        }

        private void ImportClick(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Filter = "Excel 工作簿 (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|链接文件 (*.txt;*.csv)|*.txt;*.csv|所有文件 (*.*)|*.*", Multiselect = false })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                string file = dialog.FileName;
                _deepReview.Enabled = false;
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension == ".xlsx" || extension == ".xlsm")
                {
                    try
                    {
                        Cursor = Cursors.WaitCursor;
                        List<ExcelSheetPlan> plans = OpenXmlExcelBridge.LoadPlans(file);
                        if (plans.Count == 0)
                        {
                            MessageBox.Show("在该 Excel 中没有找到包含 http:// 或 https:// 的链接列。", "未找到链接", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }
                        _excelPath = file;
                        _excelPlans = plans;
                        _importJobs.Clear();
                        _export.Text = "写回原 Excel";
                        var checkableSources = plans.SelectMany(plan => plan.Sources).Where(source => !source.ManualOnly && !String.IsNullOrWhiteSpace(source.Url));
                        _input.Text = String.Join(Environment.NewLine, checkableSources.Select(source => source.Url).Distinct(StringComparer.OrdinalIgnoreCase));
                        int count = checkableSources.Select(source => source.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                        int manualOnly = plans.SelectMany(plan => plan.Sources).Count(source => source.ManualOnly);
                        _progressText.Text = "已载入 " + Path.GetFileName(file) + "：" + count + " 条链接，" + plans.Count + " 个工作表" +
                            (manualOnly > 0 ? "；视频号无链接 " + manualOnly + " 条将标为待复核" : "");
                    }
                    catch (Exception ex)
                    {
                        _excelPath = null;
                        _excelPlans.Clear();
                        _importJobs.Clear();
                        _export.Text = "导出结果";
                        MessageBox.Show("无法读取 Excel：\n" + ex.Message + "\n\n请确认文件没有损坏；旧版 .xls 请先另存为 .xlsx。", "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    finally { Cursor = Cursors.Default; }
                }
                else
                {
                    try
                    {
                        _excelPath = null;
                        _excelPlans.Clear();
                        _export.Text = "导出结果";
                        if (extension == ".csv")
                        {
                            _importJobs = LoadCsvJobs(file);
                            _input.Text = String.Join(Environment.NewLine, _importJobs.Select(item => item.Url));
                            _progressText.Text = "已按 CSV 表头载入 " + Path.GetFileName(file) + "：" + _importJobs.Count + " 条链接";
                        }
                        else
                        {
                            _importJobs.Clear();
                            _input.Text = File.ReadAllText(file, DetectFileEncoding(file));
                            _progressText.Text = "已载入 " + Path.GetFileName(file);
                        }
                    }
                    catch (Exception ex) { MessageBox.Show("无法读取文件：" + Path.GetFileName(file) + "\n" + ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
            }
        }

        private static Encoding DetectFileEncoding(string path)
        {
            byte[] bytes;
            using (var stream = File.OpenRead(path))
            {
                int length = (int)Math.Min(stream.Length, 262144L);
                bytes = new byte[length];
                stream.Read(bytes, 0, length);
            }
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8;
            if (LooksLikeUtf8(bytes)) return new UTF8Encoding(false, true);
            return Encoding.Default;
        }

        private static bool LooksLikeUtf8(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return true;
            try
            {
                string decoded = new UTF8Encoding(false, true).GetString(bytes);
                return decoded.Any(character => character > 127) || bytes.All(value => value < 128);
            }
            catch (DecoderFallbackException) { return false; }
        }

        private void ExportClick(object sender, EventArgs e)
        {
            if (_allRows.Count == 0) { MessageBox.Show("当前没有可导出的结果。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!String.IsNullOrEmpty(_excelPath) && _excelPlans.Count > 0)
            {
                DialogResult answer = MessageBox.Show("将在原 Excel 中回填“链接状态、HTTP状态、页面标题”三列，并先自动创建备份。\n\n是否继续？",
                    "写回原 Excel", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
                try
                {
                    string backup = OpenXmlExcelBridge.WriteResults(_excelPath, _excelPlans, _allRows);
                    MessageBox.Show("已写回：\n" + _excelPath + "\n\n写回前备份：\n" + backup,
                        "写回完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("写回失败：\n" + ex.Message + "\n\n请先关闭 Excel 中打开的该文件后重试。",
                        "写回失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }
            using (var dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = "链接核验结果_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv" })
            {
                if (dialog.ShowDialog() == DialogResult.OK) { WriteCsv(dialog.FileName, _allRows); MessageBox.Show("已导出到：\n" + dialog.FileName, "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            }
        }

        private string AutoSave()
        {
            string folder = StoragePaths.ResolveResultsDirectory(); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "链接核验结果_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"); WriteCsv(path, _allRows); return path;
        }

        private static void WriteCsv(string path, IEnumerable<CheckResult> rows)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("序号,链接状态,HTTP状态,页面标题,原链接,最终地址,检测说明,检测时间,耗时");
                foreach (var r in rows.OrderBy(x => x.Number))
                {
                    writer.WriteLine(String.Join(",", new[]
                    {
                        r.Number.ToString(), Csv(r.DisplayVerdict), Csv(r.StatusCode), Csv(r.Title),
                        Csv(r.OriginalUrl), Csv(r.FinalUrl), Csv(r.Evidence),
                        Csv(r.CheckedAt), Csv(r.Duration)
                    }));
                }
            }
        }

        private static string Csv(string value) { return "\"" + (value ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\""; }

        private void OpenSelectedClick(object sender, EventArgs e) { OpenSelected(); }
        private void OpenSelected()
        {
            if (_grid.CurrentRow == null) return; var item = _grid.CurrentRow.DataBoundItem as CheckResult; if (item == null) return;
            try { Process.Start(new ProcessStartInfo(item.OriginalUrl) { UseShellExecute = true }); }
            catch { MessageBox.Show("无法打开该链接。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void GridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (_grid.Columns[e.ColumnIndex].DataPropertyName != "DisplayVerdict" || e.Value == null) return;
            string v = e.Value.ToString(); e.CellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
            if (v == "有效") e.CellStyle.ForeColor = Color.FromArgb(22, 128, 85);
            else if (v == "失效") e.CellStyle.ForeColor = Color.FromArgb(201, 66, 51);
            else e.CellStyle.ForeColor = Color.FromArgb(180, 116, 20);
        }

        private static Label MakeStat(string number, string caption)
        {
            return new Label { Text = number, Tag = caption, Font = new Font("微软雅黑", 20, FontStyle.Bold), ForeColor = Color.FromArgb(31, 41, 55), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(16, 7), Size = new Size(210, 40) };
        }

        private static Panel StatCard(Label number)
        {
            var p = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), BackColor = Color.White };
            var caption = new Label { Text = number.Tag.ToString(), AutoSize = true, ForeColor = Color.FromArgb(107, 114, 128), Location = new Point(17, 50) };
            p.Controls.Add(number); p.Controls.Add(caption); return p;
        }

        private static void StyleButton(Button button, string text, bool primary)
        {
            button.Text = text; button.Width = 142; button.Height = 34; button.Margin = new Padding(0, 0, 9, 0); button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1; button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            button.BackColor = primary ? Color.FromArgb(38, 99, 177) : Color.White; button.ForeColor = primary ? Color.White : Color.FromArgb(55, 65, 81);
            button.Cursor = Cursors.Hand;
        }
    }

    internal static class TextBoxExtensions
    {
        public static void PlaceholderTextCompat(this TextBox box, string text) { box.Tag = text; }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] requiredFiles = new[] { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll", "platform-rules.json" };
            string missing = requiredFiles.FirstOrDefault(file => !File.Exists(Path.Combine(baseDirectory, file)));
            if (!String.IsNullOrEmpty(missing))
            {
                MessageBox.Show("工具文件不完整，缺少：" + missing + "\n\n请完整解压整个便携版 ZIP，不要只复制 exe。",
                    "无法启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args)
            {
                string report = RuntimeReport.Write("界面线程", args.Exception);
                MessageBox.Show("工具运行时发生异常：\n" + (args.Exception == null ? "未知错误" : args.Exception.Message) +
                    "\n\n已完成的核验结果会保留在自动进度中。\n异常报告：\n" + report,
                    "运行异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
            {
                RuntimeReport.Write("后台线程", args.ExceptionObject as Exception ?? new Exception("未知后台异常"));
            };
            try { Application.Run(new MainForm()); }
            catch (Exception ex)
            {
                string report = RuntimeReport.Write("程序启动", ex);
                MessageBox.Show("工具无法启动：\n" + ex.Message +
                    "\n\n请确认整个便携版文件夹已经完整解压，并使用“启动工具.cmd”启动。\n异常报告：\n" + report,
                    "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
