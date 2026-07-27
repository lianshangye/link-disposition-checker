using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LinkDispositionChecker;

internal static class ReliabilityTests
{
    private static int _failures;

    private static void Expect(string name, bool passed)
    {
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
        if (!passed) _failures++;
    }

    public static int Main()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), "LinkCheckerReliabilityData_" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LINK_CHECKER_TEST_DATA_DIR", testDirectory);
        Directory.CreateDirectory(StoragePaths.UserDataDirectory);
        SessionStore.Clear();
        try
        {
            var oldJob = new CheckJob { Number = 1, Url = "https://example.com/old", SourceSheet = "Sheet1", SourceRow = 8 };
            var changedJob = new CheckJob { Number = 1, Url = "https://example.com/new", SourceSheet = "Sheet1", SourceRow = 8 };
            Expect("同一行更换链接后必须视为不同任务", oldJob.Key != changedJob.Key);

            SessionStore.Save("", "", new[] { oldJob }, new CheckResult[0]);
            SessionStore.Append(new CheckResult
            {
                Number = 1, OriginalUrl = oldJob.Url, SourceSheet = oldJob.SourceSheet, SourceRow = oldJob.SourceRow,
                Verdict = "仍可访问", CheckedAt = "2026-07-24 10:00:00"
            });
            File.AppendAllText(SessionStore.JournalPath, "{\"Number\":", Encoding.UTF8);
            CheckSession torn = SessionStore.Load();
            Expect("日志末行损坏时保留之前完整结果", torn != null && torn.Results.Count == 1 && torn.Results[0].OriginalUrl == oldJob.Url);

            File.WriteAllText(SessionStore.SessionPath, "{broken", Encoding.UTF8);
            CheckSession backup = SessionStore.Load();
            Expect("主进度损坏时可以从备份恢复", backup != null && backup.Jobs != null && backup.Jobs.Count == 1);

            Expect("同平台最终 404 可以作为目标 HTTP 证据", Checker.IsAuthoritativeTargetHttpRemoval(
                new Uri("https://news.example.com/article/1"), new Uri("https://www.news.example.com/article/1")));
            Expect("跨站跳转 404 不能证明原目标删除", !Checker.IsAuthoritativeTargetHttpRemoval(
                new Uri("https://source.example.com/article/1"), new Uri("https://gateway.invalid/error/404")));
            Expect("登录页 404 不能证明原目标删除", !Checker.IsAuthoritativeTargetHttpRemoval(
                new Uri("https://news.example.com/article/1"), new Uri("https://news.example.com/login?next=1")));

            var fast404 = new CheckResult { OriginalUrl = "https://news.example.com/article/1" };
            bool fast404Resolved = DeepReviewForm.ClassifyFastResponse(fast404, new EdgeFetchedResponse { StatusCode = 404 });
            Expect("快速请求缺少最终跳转地址时 404 不直接判失效",
                !fast404Resolved && fast404.Verdict == "人工复核");

            Expect("4.4.2 升级会重跑旧版确定结论", MainForm.ShouldDiscardResultForEngineUpgrade(
                new CheckResult { Verdict = "仍可访问", StatusCode = "200" }, "4.4.1"));
            Expect("4.4.2 当前版本保留确定结论", !MainForm.ShouldDiscardResultForEngineUpgrade(
                new CheckResult { Verdict = "仍可访问", StatusCode = "200" }, "4.4.2"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL reliability test exception: " + ex.GetType().Name + " / " + ex.Message);
            _failures++;
        }
        finally
        {
            SessionStore.Clear();
            try { Directory.Delete(testDirectory, true); } catch { }
        }
        return _failures == 0 ? 0 : 1;
    }
}
