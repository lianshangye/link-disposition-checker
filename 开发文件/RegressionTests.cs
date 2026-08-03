using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LinkDispositionChecker;

internal static class RegressionTests
{
    private static int _failures;

    private static void Expect(string name, string expected, DeepDecision actual)
    {
        bool passed = actual != null && actual.Verdict == expected;
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " => " +
            (actual == null ? "null" : actual.Verdict + " / " + actual.Evidence));
        if (!passed) _failures++;
    }

    public static int Main()
    {
        bool pacingPassed =
            Checker.RequestPacingKey(new Uri("https://www.zhihu.com/question/1")) == "zhihu.com" &&
            Checker.RequestPacingKey(new Uri("https://api.weibo.com/2/statuses/show.json")) == "weibo.com" &&
            Checker.RequestPacingMilliseconds(new Uri("https://www.douyin.com/video/1")) >= 1500 &&
            Checker.RequestPacingMilliseconds(new Uri("https://example.com/page")) < 1000 &&
            PerformanceProfile.Resolve("低配模式").Workers == 1 &&
            PerformanceProfile.Resolve("标准模式").Workers == 3 &&
            PerformanceProfile.Resolve("高性能模式").Workers == 6;
        Console.WriteLine((pacingPassed ? "PASS " : "FAIL ") + "平台级限速和保守并发配置");
        if (!pacingPassed) _failures++;

        bool targetSignalPassed =
            Checker.IsTencentVideoUnavailableResponse(
                "QZOutputJson={\"vid\":\"l1257edy4lk\",\"em\":80,\"msg\":\"该内容暂时不支持观看，可以看看其他内容哦\"};",
                "l1257edy4lk") &&
            !Checker.IsTencentVideoUnavailableResponse(
                "QZOutputJson={\"vid\":\"l1257edy4lk\",\"em\":0,\"ti\":\"正常视频\"};",
                "l1257edy4lk") &&
            Checker.IsAutohomeArticleErrorRedirect(
                new Uri("https://chejiahao.autohome.com.cn/info/25934642"),
                "https://chejiahao.autohome.com.cn/?from=pc-error-no-hidden#pvareaid=6867538",
                "<html>pc-error-no-hidden</html>") &&
            Checker.IsAutohomeArticleErrorRedirect(
                new Uri("https://chejiahao.autohome.com.cn/info/25934642"),
                "https://chejiahao.autohome.com.cn/?from=pc-error-no-hidden#pvareaid=6867538",
                "<html>首页</html>") &&
            !Checker.IsAutohomeArticleErrorRedirect(
                new Uri("https://chejiahao.autohome.com.cn/info/25934642"),
                "https://chejiahao.autohome.com.cn/info/25934642",
                "<html>pc-error-no-hidden</html>");
        Console.WriteLine((targetSignalPassed ? "PASS " : "FAIL ") + "腾讯视频和汽车之家目标级失效信号");
        if (!targetSignalPassed) _failures++;

        var circuitBreaker = new NetworkRestrictionCircuitBreaker(8);
        bool circuitPassed = true;
        string circuitReason = "";
        for (int index = 0; index < 7; index++)
        {
            circuitPassed = circuitPassed && !circuitBreaker.Observe(new CheckResult
            {
                Verdict = "暂时异常",
                StatusCode = index % 2 == 0 ? "502" : "444",
                Evidence = "站点服务异常"
            }, out circuitReason);
        }
        circuitPassed = circuitPassed && circuitBreaker.Observe(new CheckResult
        {
            Verdict = "暂时异常",
            StatusCode = "200",
            Evidence = "遇到验证码"
        }, out circuitReason);
        circuitPassed = circuitPassed && !String.IsNullOrWhiteSpace(circuitReason) &&
            Checker.NormalizeVisibleVerdict("暂时异常") == "暂时异常" &&
            Checker.NormalizeVisibleVerdict("疑似已处置") == "疑似已处置" &&
            Checker.NormalizeVisibleVerdict("公网不可访问") == "公网不可访问";
        Console.WriteLine((circuitPassed ? "PASS " : "FAIL ") + "连续网络风控自动熔断且保留真实结果标签");
        if (!circuitPassed) _failures++;

        var preflightJobs = new List<CheckJob>();
        for (int index = 0; index < 6; index++)
        {
            preflightJobs.Add(new CheckJob { Number = index + 1, Url = "https://www.zhihu.com/question/" + index, Platform = "知乎" });
            preflightJobs.Add(new CheckJob { Number = index + 20, Url = "https://weibo.com/123/" + index, Platform = "微博" });
            preflightJobs.Add(new CheckJob { Number = index + 40, Url = "https://www.toutiao.com/article/" + index, Platform = "今日头条" });
        }
        List<CheckJob> preflightSamples = BatchPreflightPlanner.SelectSamples(preflightJobs, 6, 2);
        bool preflightSelectionPassed = preflightSamples.Count == 6 &&
            preflightSamples.GroupBy(BatchPreflightPlanner.PlatformKey).Count() == 3 &&
            preflightSamples.GroupBy(BatchPreflightPlanner.PlatformKey).All(group => group.Count() <= 2);
        var blockedObservations = preflightSamples.Take(4).Select(job =>
            new KeyValuePair<CheckJob, CheckResult>(job, new CheckResult
            {
                Verdict = "暂时异常",
                StatusCode = "502",
                Evidence = "网络预检站点服务异常"
            })).ToList();
        BatchPreflightSummary blockedSummary = BatchPreflightPlanner.Analyze(blockedObservations);
        var platformController = new PlatformRestrictionController(3);
        string pausedPlatform = "";
        bool platformPausePassed =
            !platformController.Observe(preflightJobs[0], blockedObservations[0].Value, out pausedPlatform) &&
            !platformController.Observe(preflightJobs[0], blockedObservations[0].Value, out pausedPlatform) &&
            !platformController.Observe(preflightJobs[0], blockedObservations[0].Value, out pausedPlatform) &&
            String.IsNullOrWhiteSpace(pausedPlatform) &&
            !platformController.IsPaused(preflightJobs[0]) &&
            !platformController.IsPaused(preflightJobs[1]);
        var genericController = new PlatformRestrictionController(3);
        var genericA = new CheckJob { Url = "https://news-a.example.com/article/1", Platform = "网媒" };
        var genericB = new CheckJob { Url = "https://news-b.example.com/article/2", Platform = "网媒" };
        bool genericSitePausePassed =
            !genericController.Observe(genericA, blockedObservations[0].Value, out pausedPlatform) &&
            !genericController.Observe(genericA, blockedObservations[0].Value, out pausedPlatform) &&
            !genericController.Observe(genericA, blockedObservations[0].Value, out pausedPlatform) &&
            String.IsNullOrWhiteSpace(pausedPlatform) &&
            !genericController.IsPaused(genericA) &&
            !genericController.IsPaused(genericB) &&
            genericController.PausedPlatforms.Count == 0;
        bool preflightPassed = preflightSelectionPassed && blockedSummary.RequiresDecision &&
            BatchRunSafetyPolicy.ShouldPauseAfterPreflight(blockedSummary, false) &&
            !BatchRunSafetyPolicy.ShouldPauseAfterPreflight(blockedSummary, true) &&
            !BatchRunSafetyPolicy.ShouldUseGlobalCircuitBreaker(true) &&
            BatchRunSafetyPolicy.ShouldUseGlobalCircuitBreaker(false) &&
            blockedSummary.TransientRestrictions == 4 && platformPausePassed && genericSitePausePassed;
        preflightPassed = preflightPassed &&
            MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "暂时异常" }, false) &&
            MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "人工复核" }, false) &&
            MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "人工复核" }, true) &&
            !MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "仍可访问" }, true);
        Console.WriteLine((preflightPassed ? "PASS " : "FAIL ") + "跨平台小样本预检和 502 不提前跳过");
        if (!preflightPassed) _failures++;

        bool aiUrlPassed =
            YunwuAiClient.NormalizeBaseUrl("https://yunwu.ai") == "https://yunwu.ai/v1" &&
            YunwuAiClient.NormalizeBaseUrl("https://yunwu.ai/v1/") == "https://yunwu.ai/v1" &&
            YunwuAiClient.ChatUrl("https://yunwu.ai/v1/chat/completions") == "https://yunwu.ai/v1/chat/completions" &&
            YunwuAiClient.ModelsUrl("https://yunwu.ai") == "https://yunwu.ai/v1/models";
        using (var aiTransportClient = new YunwuAiClient("regression-placeholder-token"))
        {
            aiUrlPassed = aiUrlPassed &&
                (System.Net.ServicePointManager.SecurityProtocol & System.Net.SecurityProtocolType.Tls12) != 0;
        }
        var aiAlive = new CheckResult
        {
            Verdict = "人工复核",
            StatusCode = "200",
            ExpectedTitle = "新能源汽车行业进入新的技术竞争阶段",
            AnalysisContext = "页面标题：行业观察 页面可见内容：新能源汽车行业进入新的技术竞争阶段，作者继续介绍市场、技术、产品和用户反馈，正文内容仍然完整可读。文章还包含发布时间、连续段落、评论、收藏和分享区域，可确认这不是平台首页或空白网页外壳。",
            Evidence = "页面已经打开但通用规则证据不足"
        };
        bool aiEligiblePassed = AiReviewPolicy.IsEligible(aiAlive) &&
            !AiReviewPolicy.IsEligible(new CheckResult
            {
                Verdict = "暂时异常",
                StatusCode = "502",
                AnalysisContext = new string('字', 120),
                Evidence = "站点服务异常"
            });
        AiReviewApplication aiAliveApplied = AiReviewPolicy.Apply(aiAlive, new AiReviewDecision
        {
            Verdict = "仍可访问",
            Confidence = 0.98,
            Reason = "当前页面明确出现目标标题和连续正文"
        }, "test-model");
        var aiRemoved = new CheckResult
        {
            Verdict = "人工复核",
            StatusCode = "200",
            ExpectedTitle = "目标文章",
            AnalysisContext = "页面标题：提示 页面可见内容：该文章已被删除，返回首页。" + new string('证', 100),
            Evidence = "页面出现删除提示但位置尚未确认"
        };
        AiReviewApplication aiRemovedApplied = AiReviewPolicy.Apply(aiRemoved, new AiReviewDecision
        {
            Verdict = "已失效",
            Confidence = 0.99,
            Reason = "页面明确提示目标文章已删除"
        }, "test-model");
        bool aiPolicyPassed = aiUrlPassed && aiEligiblePassed && aiAliveApplied.Resolved &&
            aiAlive.Verdict == "仍可访问" && !aiRemovedApplied.Resolved && aiRemoved.Verdict == "疑似已处置";
        Console.WriteLine((aiPolicyPassed ? "PASS " : "FAIL ") + "Yunwu 兼容接口、AI候选过滤和本地安全门");
        if (!aiPolicyPassed) _failures++;

        var logContext = ExecutionLogContext.Start("快速核验", "回归测试", "标准模式", "自动网络", 10, 2, 8);
        logContext.EndedAt = DateTime.Now;
        logContext.Outcome = "部分完成";
        logContext.StopReason = "回归测试";
        logContext.RecordAiFailure(2, "第 3 条 AI 调用失败：测试错误");
        logContext.RecordAiSuccess(1);
        string privateUrl = "https://example.com/private/path?case=123456";
        string fakeCredential = "sk-" + new string('x', 32);
        var loggedFailure = new CheckResult
        {
            Number = 3,
            OriginalUrl = privateUrl,
            Platform = "测试平台",
            StatusCode = "502",
            Verdict = "暂时异常",
            Duration = "18.0s",
            Evidence = "访问 " + privateUrl + " 时失败，测试凭据 " + fakeCredential
        };
        logContext.Observe(loggedFailure);
        string diagnosticLog = String.Join("\n", ExecutionLogWriter.BuildLines(logContext, new[] { loggedFailure }));
        string logTestDirectory = Path.Combine(Path.GetTempPath(), "LinkCheckerLogTest-" + Guid.NewGuid().ToString("N"));
        string writtenLog = "";
        bool fileLogPassed = false;
        try
        {
            Directory.CreateDirectory(logTestDirectory);
            for (int index = 0; index < 105; index++)
            {
                string oldLog = Path.Combine(logTestDirectory, "执行日志_旧记录_" + index.ToString("D3") + ".txt");
                File.WriteAllText(oldLog, "test");
                File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-2).AddMinutes(-index));
            }
            writtenLog = ExecutionLogWriter.WriteToDirectory(logContext, new[] { loggedFailure }, logTestDirectory);
            string latestLog = Path.Combine(logTestDirectory, "最近一次执行日志.txt");
            fileLogPassed = File.Exists(writtenLog) && File.Exists(latestLog) &&
                File.ReadAllText(latestLog).Contains(logContext.RunId) &&
                Directory.GetFiles(logTestDirectory, "执行日志_*.txt").Length == 100;
        }
        finally
        {
            if (Directory.Exists(logTestDirectory)) Directory.Delete(logTestDirectory, true);
        }
        bool logPassed = fileLogPassed &&
            diagnosticLog.Contains("example.com") &&
            diagnosticLog.Contains("HTTP 5xx") &&
            diagnosticLog.Contains("RUN-") &&
            diagnosticLog.Contains("本次尚未处理：7") &&
            diagnosticLog.Contains("本次 AI 请求次数：3") &&
            diagnosticLog.Contains("本次 AI 失败条数：1") &&
            diagnosticLog.Contains("关键执行事件") &&
            diagnosticLog.Contains("[链接]") &&
            diagnosticLog.Contains("[凭据已隐藏]") &&
            !diagnosticLog.Contains(privateUrl) &&
            !diagnosticLog.Contains(fakeCredential);
        Console.WriteLine((logPassed ? "PASS " : "FAIL ") + "执行日志统计、匿名样本和凭据脱敏");
        if (!logPassed) _failures++;

        var fatalAiError = new AiServiceException("配置失败", true, false);
        var retryableAiError = new AiServiceException("临时限流", false, true, 4000);
        bool aiBatchPolicyPassed = AiBatchPolicy.IsFatal(fatalAiError) &&
            !AiBatchPolicy.CanRetry(fatalAiError, 1) &&
            AiBatchPolicy.CanRetry(retryableAiError, 1) &&
            !AiBatchPolicy.CanRetry(retryableAiError, AiBatchPolicy.MaximumAttemptsPerItem) &&
            !AiBatchPolicy.ShouldPauseBatch(AiBatchPolicy.ConsecutiveFailuresBeforePause - 1) &&
            AiBatchPolicy.ShouldPauseBatch(AiBatchPolicy.ConsecutiveFailuresBeforePause);
        Console.WriteLine((aiBatchPolicyPassed ? "PASS " : "FAIL ") + "AI 单条重试、致命错误和连续失败暂停策略");
        if (!aiBatchPolicyPassed) _failures++;

        bool reviewRoutingPassed =
            !MainForm.IsEvidenceReviewCandidate(new CheckResult { Verdict = "暂时异常" }) &&
            MainForm.IsEvidenceReviewCandidate(new CheckResult { Verdict = "暂时异常", SiteHealth = "站点首页可访问" }) &&
            MainForm.IsEvidenceReviewCandidate(new CheckResult { Verdict = "人工复核" }) &&
            MainForm.IsEvidenceReviewCandidate(new CheckResult { Verdict = "疑似已处置" }) &&
            !MainForm.IsEvidenceReviewCandidate(new CheckResult { Verdict = "人工复核", SkipDeepReview = true }) &&
            MainForm.ReviewButtonText(0) == "自动补证" &&
            MainForm.ReviewButtonText(3) == "自动补证（3）";
        Console.WriteLine((reviewRoutingPassed ? "PASS " : "FAIL ") + "网络待重试与证据复核候选严格分流");
        if (!reviewRoutingPassed) _failures++;

        CheckResult infrastructureDeferred = MainForm.CreateInfrastructureDeferredResult(new CheckJob
        {
            Number = 88,
            Url = "https://news-a.example.com/article/88",
            Platform = "网媒",
            ExpectedTitle = "目标文章",
            InfrastructureKey = "IP 203.0.113.8"
        }, "IP 203.0.113.8");
        CheckResult publicUnavailableDeferred = MainForm.CreateInfrastructureDeferredResult(new CheckJob
        {
            Number = 89,
            Url = "https://news-b.example.com/article/89",
            Platform = "网媒",
            InfrastructureKey = "IP 203.0.113.9"
        }, "IP 203.0.113.9", true);
        bool publicUnavailableGatePassed = Checker.ShouldMarkPubliclyUnavailable(
            new CheckResult
            {
                Verdict = "暂时异常",
                StatusCode = "502",
                SiteHealth = "站点整体异常",
                Evidence = "系统代理和直连均失败"
            },
            new RemoteEvidenceResponse { TargetUnreachable = true }) &&
            !Checker.ShouldMarkPubliclyUnavailable(
                new CheckResult
                {
                    Verdict = "暂时异常",
                    StatusCode = "502",
                    SiteHealth = "站点首页可访问"
                },
                new RemoteEvidenceResponse { TargetUnreachable = true });
        bool evidenceEscalationRoutingPassed =
            SessionStore.CurrentEngineVersion == "4.5.5" &&
            infrastructureDeferred.Number == 88 &&
            infrastructureDeferred.Verdict == "暂时异常" &&
            infrastructureDeferred.SkipDeepReview &&
            infrastructureDeferred.InfrastructureKey == "IP 203.0.113.8" &&
            infrastructureDeferred.EvidenceTrail != null &&
            infrastructureDeferred.EvidenceTrail.Count == 1 &&
            infrastructureDeferred.EvidenceStage.Contains("基础设施") &&
            publicUnavailableDeferred.Verdict == "公网不可访问" &&
            publicUnavailableDeferred.EvidenceStage.Contains("自动多线路不可访问") &&
            publicUnavailableGatePassed;
        Console.WriteLine((evidenceEscalationRoutingPassed ? "PASS " : "FAIL ") +
            "4.4 未完成状态、共享基础设施兼容和自动追证字段");
        if (!evidenceEscalationRoutingPassed) _failures++;

        var chinaEyeballCandidate = new CheckResult
        {
            Verdict = "暂时异常",
            StatusCode = "502",
            Platform = "网媒",
            InfrastructureKey = "IP 119.28.42.49",
            Evidence = "HTTP ERROR 502"
        };
        bool chinaEyeballRulePassed =
            !Checker.ShouldTryChinaEyeballEvidence(chinaEyeballCandidate,
                new Uri("http://news.example.com/xinwen/123.html")) &&
            Checker.IsChinaEyeballChallenge(403,
                "<title>网站防火墙</title><script>window.location.href='/xinwen/123.html';</script>",
                "challenge=abc; server_name_session=def") &&
            Checker.MergeCookieHeaders("challenge=old; session=abc", "challenge=new; path_token=xyz")
                .Contains("challenge=new") &&
            Checker.MergeCookieHeaders("challenge=old; session=abc", "challenge=new; path_token=xyz")
                .Contains("path_token=xyz") &&
            Checker.ExtractMetaDescription(
                "<html><head><meta content=\"这是一段来自当前文章正文的有效摘要，长度足以证明目标页面仍在公开返回文章内容。\" name=\"description\"></head></html>")
                .Contains("当前文章正文") &&
            Checker.IsChinaProbeCapacityFailure(
                "Globalping 返回 HTTP 429: rate_limit_exceeded");
        Console.WriteLine((chinaEyeballRulePassed ? "PASS " : "FAIL ") +
            "中国普通宽带防火墙挑战、Cookie 重试和正文摘要识别");
        if (!chinaEyeballRulePassed) _failures++;

        // Zhihu's public answer API may be blocked by a configured proxy while
        // the direct route still returns the answer. The production path now
        // retries that direct route before leaving the item unfinished.

        bool excelVerdictPassed =
            OpenXmlExcelBridge.ToExcelVerdict("已失效") == "失效" &&
            OpenXmlExcelBridge.ToExcelVerdict("仍可访问") == "有效" &&
            OpenXmlExcelBridge.ToExcelVerdict("公网不可访问") == "未完成" &&
            OpenXmlExcelBridge.ToExcelVerdict("人工复核") == "未完成";
        Console.WriteLine((excelVerdictPassed ? "PASS " : "FAIL ") +
            "Excel 只写入有效、失效和未完成");
        if (!excelVerdictPassed) _failures++;

        bool unfinishedRetryPassed =
            MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "公网不可访问" }, false) &&
            MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "暂时异常" }, false) &&
            MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "人工复核" }, false) &&
            !MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "已失效" }, false) &&
            !MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "仍可访问" }, false) &&
            new CheckResult { Verdict = "公网不可访问" }.DisplayVerdict == "未完成" &&
            new CheckResult { Verdict = "仍可访问" }.DisplayVerdict == "有效" &&
            !PlatformRestrictionController.ShouldPauseAfterResult(new CheckResult
            {
                Verdict = "暂时异常",
                StatusCode = "502",
                Evidence = "目标站点返回 HTTP 502"
            }) &&
            !PlatformRestrictionController.ShouldPauseAfterResult(new CheckResult
            {
                Verdict = "暂时异常",
                StatusCode = "502",
                Evidence = "检测未完成：外部中国宽带探针达到本小时额度"
            }) &&
            PlatformRestrictionController.ShouldPauseAfterResult(new CheckResult
            {
                Verdict = "暂时异常",
                StatusCode = "403",
                Evidence = "目标站点安全验证"
            });
        Console.WriteLine((unfinishedRetryPassed ? "PASS " : "FAIL ") +
            "601 条未完成续检、502 不提前跳过和目标风控保护");
        if (!unfinishedRetryPassed) _failures++;

        var sharedInfrastructureController = new InfrastructureRestrictionController(2);
        var sharedA = new CheckJob { Number = 701, Url = "http://a.shared.test/x", InfrastructureKey = "IP 119.28.42.49" };
        var sharedB = new CheckJob { Number = 702, Url = "http://b.shared.test/y", InfrastructureKey = "IP 119.28.42.49" };
        var sharedFailure = new CheckResult { Verdict = "暂时异常", StatusCode = "502", Evidence = "HTTP 502" };
        string pausedInfrastructure;
        bool sharedInfrastructurePassed =
            !sharedInfrastructureController.Observe(sharedA, sharedFailure, out pausedInfrastructure) &&
            sharedInfrastructureController.Observe(sharedA, sharedFailure, out pausedInfrastructure) &&
            sharedInfrastructureController.IsPaused(sharedB) &&
            sharedInfrastructureController.PausedInfrastructures.Count == 1 &&
            sharedInfrastructureController.PausedInfrastructures[0] == "IP 119.28.42.49" &&
            MainForm.CreateInfrastructureDeferredResult(sharedB, "共享基础设施已暂停重复访问").Verdict == "暂时异常";
        Console.WriteLine((sharedInfrastructurePassed ? "PASS " : "FAIL ") +
            "共享基础设施异常只触发一次访问并保留可重试状态");
        if (!sharedInfrastructurePassed) _failures++;

        bool kuaishouRemovedPassed =
            Checker.IsKuaishouRemovedSsrContent("{\"result\":223,\"error_msg\":\"获取失败，作品可能已被删除或尚未上传\"}", "3x3hbza3vsiqe5w") &&
            !Checker.IsKuaishouRemovedSsrContent("{\"result\":0,\"error_msg\":\"网络错误\"}", "3x3hbza3vsiqe5w");
        Console.WriteLine((kuaishouRemovedPassed ? "PASS " : "FAIL ") +
            "快手作品专用删除提示识别");
        if (!kuaishouRemovedPassed) _failures++;

        var unavailableForAcceptance = new CheckResult
        {
            Number = 90,
            OriginalUrl = "https://shared.example.com/article/90",
            Verdict = "公网不可访问",
            StatusCode = "502",
            InfrastructureKey = "IP 203.0.113.90",
            Evidence = "系统代理、直连和公开云均未取得正文"
        };
        ContractAcceptanceView unavailableView =
            ContractAcceptanceClassifier.Evaluate(unavailableForAcceptance);
        BatchPreflightSummary unavailablePreflight = BatchPreflightPlanner.Analyze(new[]
        {
            new KeyValuePair<CheckJob, CheckResult>(
                new CheckJob { Number = 90, Url = unavailableForAcceptance.OriginalUrl },
                unavailableForAcceptance)
        });
        bool acceptanceClassifierPassed =
            unavailableView.ContentStatus == "未知" &&
            unavailableView.RequiresIndependentNetworkReview &&
            unavailableView.AcceptanceRecommendation.Contains("尚不能归责供应商") &&
            !unavailableView.ContentResolved &&
            unavailablePreflight.Resolved == 0 &&
            unavailablePreflight.EvidenceInsufficient == 1 &&
            ContractAcceptanceClassifier.IsContentResolved(new CheckResult { Verdict = "已失效" }) &&
            ContractAcceptanceClassifier.IsContentResolved(new CheckResult { Verdict = "仍可访问" });
        Console.WriteLine((acceptanceClassifierPassed ? "PASS " : "FAIL ") +
            "内容状态、公开可访问性和合同归责严格分离");
        if (!acceptanceClassifierPassed) _failures++;

        string packageTestDirectory = Path.Combine(Path.GetTempPath(),
            "LinkCheckerAcceptanceTest-" + Guid.NewGuid().ToString("N"));
        bool evidencePackagePassed = false;
        try
        {
            var packageRows = new List<CheckResult>();
            for (int index = 0; index < 6; index++)
            {
                packageRows.Add(new CheckResult
                {
                    Number = index + 1,
                    OriginalUrl = "https://control" + index + ".example.com/article/" + index,
                    Verdict = "仍可访问",
                    InfrastructureKey = "CONTROL-" + index,
                    Evidence = "取得目标正文"
                });
            }
            packageRows.Add(new CheckResult
            {
                Number = 7,
                OriginalUrl = "https://removed.example.com/article/7",
                Verdict = "已失效",
                InfrastructureKey = "REMOVED-1",
                Evidence = "目标页明确提示已删除"
            });
            for (int index = 0; index < 34; index++)
            {
                packageRows.Add(new CheckResult
                {
                    Number = 8 + index,
                    OriginalUrl = "https://blocked" + (index % 9) + ".example.com/article/" + index,
                    Verdict = "公网不可访问",
                    StatusCode = "502",
                    InfrastructureKey = "SHARED-" + (index % 4),
                    Evidence = "多线路均未取得正文"
                });
            }
            AcceptanceEvidencePackage package = AcceptanceEvidencePackageWriter.WriteToBaseDirectory(
                packageRows, "RUN-CONTRACT-TEST", packageTestDirectory,
                System.Reflection.Assembly.GetExecutingAssembly().Location,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "platform-rules.json"));
            string method = File.ReadAllText(Path.Combine(package.DirectoryPath, "04_环境与方法说明.txt"));
            string review = File.ReadAllText(Path.Combine(package.DirectoryPath, "03_独立普通网络复核清单.csv"));
            string manifest = File.ReadAllText(Path.Combine(package.DirectoryPath, "SHA256SUMS.txt"));
            int targetSamples = review.Split('\n').Count(line => line.Contains("\"目标样本\""));
            evidencePackagePassed =
                package.Total == 41 && package.ContentResolved == 7 &&
                package.IndependentReviewRequired == 34 &&
                targetSamples == 30 &&
                File.Exists(package.ZipPath) &&
                File.Exists(Path.Combine(packageTestDirectory, "最近一次验收证据包.txt")) &&
                method.Contains("不能单独排除某一共享基础设施对数据中心网络的限制") &&
                method.Contains("不等于内容删除") &&
                manifest.Contains("01_验收汇总.csv") &&
                !review.Contains("sk-secret");
        }
        finally
        {
            if (Directory.Exists(packageTestDirectory))
                Directory.Delete(packageTestDirectory, true);
        }
        Console.WriteLine((evidencePackagePassed ? "PASS " : "FAIL ") +
            "合同验收证据包、独立普通网络分层抽样和文件哈希");
        if (!evidencePackagePassed) _failures++;

        bool baijiaIdPassed = Checker.ExtractBaiduArticleId(new Uri("https://baijiahao.baidu.com/s?id=1870762825559558263&wfr=spider&for=pc")) == "1870762825559558263";
        bool dtNidPassed = Checker.ExtractBaiduArticleNid(new Uri("https://mbd.baidu.com/newspage/data/dtlandingwise?nid=dt_5277434666597158759")) == "dt_5277434666597158759";
        bool baiduPublicUrlPassed = Checker.BuildBaiduPublicArticleUrl("5277434666597158759").Contains("news_5277434666597158759");
        bool yoojiaIdPassed = Checker.ExtractBaiduArticleId(new Uri("https://www.yoojia.com/article/9455543928563677004.html")) == "9455543928563677004";
        Console.WriteLine(((baijiaIdPassed && dtNidPassed && baiduPublicUrlPassed && yoojiaIdPassed) ? "PASS " : "FAIL ") + "百度百家号 s?id、dt_ 编号及公开页识别");
        if (!baijiaIdPassed || !dtNidPassed || !baiduPublicUrlPassed || !yoojiaIdPassed) _failures++;

        Uri toutiaoShort = new Uri("https://m.toutiao.com/is/yx4jYZTtpy0/");
        Uri toutiaoRedirected = Checker.SelectPlatformProbeUri(toutiaoShort,
            "https://www.toutiao.com/article/7639324771377512969/?utm_source=copy_link");
        Uri crossPlatformRedirect = Checker.SelectPlatformProbeUri(toutiaoShort,
            "https://example.com/article/7639324771377512969/");
        bool redirectedProbePassed =
            toutiaoRedirected != null && toutiaoRedirected.AbsolutePath.Contains("/article/7639324771377512969/") &&
            Object.ReferenceEquals(crossPlatformRedirect, toutiaoShort);
        Console.WriteLine((redirectedProbePassed ? "PASS " : "FAIL ") + "同平台短链按最终内容页核验且拒绝跨站跟随");
        if (!redirectedProbePassed) _failures++;

        string yicheForum;
        string yicheThreadId;
        bool newPlatformIdentityPassed = Checker.TryExtractYicheThreadIdentity(
                new Uri("https://baa.yiche.com/qichezatan/thread-53841716.html"), out yicheForum, out yicheThreadId) &&
            yicheForum == "qichezatan" && yicheThreadId == "53841716" &&
            Checker.ExtractXimalayaTrackId(new Uri("https://m.ximalaya.com/sound/979015265")) == "979015265" &&
            Checker.IsXimalayaMissingResponse("{\"ret\":404,\"msg\":\"该声音[id:979015265]所属专辑已下架!\"}", "979015265") &&
            Checker.IsZakerMissingPage("https://app.myzaker.com/news/404.php?f=Normal") &&
            Checker.ExtractUcArticleId(new Uri("http://a.mp.uc.cn/article.html?client=uc#!wm_cid=1!!wm_aid=8166197791739647165!!wm_id=2")) == "8166197791739647165" &&
            Checker.IsCurrentToutiaoContentSource("2") && Checker.IsCurrentToutiaoContentSource("5") && Checker.IsCurrentToutiaoContentSource("21") &&
            !Checker.IsCurrentToutiaoContentSource("148") &&
            Checker.ExtractToutiaoAuthor("{\"name\":\"微头条作者\",\"source\":\"被转发作者\"}") == "微头条作者";
        Console.WriteLine((newPlatformIdentityPassed ? "PASS " : "FAIL ") + "UC、喜马拉雅、易车论坛和 ZAKER 目标身份识别");
        if (!newPlatformIdentityPassed) _failures++;

        string kuaishouCaption;
        string kuaishouAuthor;
        string kuaishouSsr = "{\"userName\":\"小痞说车\",\"caption\":\"我们车企公关流程不是这样的，魏总！ #魏建军 #长城\",\"share_info\":\"userId=abc&photoId=3xktpbe4x7ujatm\",\"photoStatus\":0}";
        bool kuaishouProbePassed = Checker.TryMatchKuaishouSsrContent(kuaishouSsr, "3xktpbe4x7ujatm",
                "我们车企公关流程不是这样的，魏总！ #魏建军 #长城", "小痞说车Zzz", out kuaishouCaption, out kuaishouAuthor) &&
            kuaishouCaption.Contains("我们车企公关流程") && kuaishouAuthor == "小痞说车" &&
            !Checker.TryMatchKuaishouSsrContent(kuaishouSsr.Replace("\"photoStatus\":0", "\"photoStatus\":1"), "3xktpbe4x7ujatm",
                "我们车企公关流程不是这样的，魏总！ #魏建军 #长城", "小痞说车Zzz", out kuaishouCaption, out kuaishouAuthor) &&
            Checker.TryMatchKuaishouSsrContent("{\"userName\":\"福小轩窗\",\"caption\":\"...\",\"share_info\":\"photoId=3xufc6rz6fgjjpu\",\"photoStatus\":0}",
                "3xufc6rz6fgjjpu", "...", "福小轩窗", out kuaishouCaption, out kuaishouAuthor) &&
            !Checker.TryMatchKuaishouSsrContent("<html><div id='app'></div></html>", "3xufc6rz6fgjjpu",
                "...", "福小轩窗", out kuaishouCaption, out kuaishouAuthor);
        Console.WriteLine((kuaishouProbePassed ? "PASS " : "FAIL ") + "快手 SSR 目标作品、公开状态、文案和作者联合识别");
        if (!kuaishouProbePassed) _failures++;

        string weiboReason;
        bool weiboProbePassed = Checker.IsWeiboPresentResponse("{\"ok\":1,\"mblogid\":\"QFX1GphBm\",\"text_raw\":\"目标微博正文\"}", "QFX1GphBm") &&
            Checker.IsWeiboUnavailableResponse("{\"ok\":0,\"message\":\"暂无查看权限\",\"error_code\":20112}", out weiboReason) &&
            weiboReason.Contains("隐藏") &&
            !Checker.IsWeiboUnavailableResponse("{\"ok\":0,\"message\":\"请求频繁\",\"error_code\":100005}", out weiboReason);
        Console.WriteLine((weiboProbePassed ? "PASS " : "FAIL ") + "微博访客接口存在、隐藏和风控响应区分");
        if (!weiboProbePassed) _failures++;

        Uri weiboVideoEvidence;
        bool weiboVideoRedirectPassed = Checker.TryExtractWeiboVideoEvidenceUri(
            "https://passport.weibo.com/visitor/visitor?url=https%3A%2F%2Fweibo.com%2Ftv%2Fshow%2F1034%3A5275753856827437%3Ffrom%3Dold_pc_videoshow",
            out weiboVideoEvidence) && weiboVideoEvidence != null &&
            weiboVideoEvidence.AbsoluteUri == "https://weibo.com/tv/show/1034:5275753856827437";
        Console.WriteLine((weiboVideoRedirectPassed ? "PASS " : "FAIL ") + "微博视频短链登录跳转可恢复目标视频地址");
        if (!weiboVideoRedirectPassed) _failures++;

        bool bilibiliArticlePassed;
        bool bilibiliArticleRemoved;
        bilibiliArticlePassed = Checker.TryMatchBilibiliArticleInfo(
            "{\"code\":0,\"data\":{\"id\":49360671,\"title\":\"家用SUV的终极答案？哈弗大狗PLUS凭空间与配置赢得全家青睐\",\"author_name\":\"远离人品差的人\"}}",
            "49360671", "家用SUV的终极答案？哈弗大狗PLUS凭空间与配置赢得全家青睐", "", "远离人品差的人", out bilibiliArticleRemoved) && !bilibiliArticleRemoved;
        bool bilibiliArticleDeleted = !Checker.TryMatchBilibiliArticleInfo(
            "{\"code\":-404,\"message\":\"文稿不存在\"}", "49360671", "", "", "", out bilibiliArticleRemoved) && bilibiliArticleRemoved;
        bool bilibiliApiWithoutEchoedId = Checker.IsBilibiliArticleApiSuccess(
            "{\"code\":0,\"data\":{\"title\":\"有效专栏\",\"author_name\":\"作者\"}}");
        Console.WriteLine((bilibiliArticlePassed && bilibiliArticleDeleted && bilibiliApiWithoutEchoedId ? "PASS " : "FAIL ") + "B站专栏官方接口目标编号、标题、作者和删除状态识别");
        if (!(bilibiliArticlePassed && bilibiliArticleDeleted && bilibiliApiWithoutEchoedId)) _failures++;

        bool bilibiliDynamicTitleMayDiffer = Checker.TryMatchBilibiliDynamicInfo(
            "{\"code\":0,\"data\":{\"id\":1225803934248992774,\"visible\":true," +
            "\"title\":\"长城汽车半年报视频\",\"author\":{\"name\":\"小鲤玩游戏_\"}," +
            "\"modules\":[{\"type\":8,\"desc\":\"视频动态内容\"}]}}",
            "1225803934248992774", "长城汽车 2026 年上半年净利润骤降近六成，是什么原因导致的？", "",
            "小鲤玩游戏_");
        Console.WriteLine((bilibiliDynamicTitleMayDiffer ? "PASS " : "FAIL ") +
            "B站动态官方编号、可见状态和作者覆盖供应商首句标题");
        if (!bilibiliDynamicTitleMayDiffer) _failures++;

        bool renderedSocialRemovalPassed = Checker.IsXueqiuRenderedRemoval(
                "<article data-id='373407682'>原帖已被作者删除</article>", "373407682") &&
            Checker.IsXueqiuRenderedRemoval(
                "<main>当前内容不适合展示，无法查看</main>", "399636095") &&
            !Checker.IsXueqiuRenderedRemoval(
                "<aside>推荐内容：原帖已被作者删除</aside>", "373407682") &&
            Checker.IsWeiboRenderedUnavailable(
                "<article><a href='/1764053084/QA8ja96OB'>目标微博</a>博文涉及营销推广正在审核中，暂时无法传播。</article>",
                "QA8ja96OB") &&
            !Checker.IsWeiboRenderedUnavailable(
                "<div>Sina Visitor System 请登录后查看</div>", "QA8ja96OB");
        renderedSocialRemovalPassed = renderedSocialRemovalPassed &&
            Checker.IsTiebaRenderedRemoval("<div data-pid='10790425311'>贴子可能已被删除</div>", "10790425311") &&
            Checker.IsBilibiliRenderedUnavailable("<div data-dynamic-id='1215952761060851713'>动态不存在</div>", "1215952761060851713") &&
            Checker.TryMatchBilibiliOpusPage(
                "<title>我哭得那么真的动态 - 哔哩哔哩</title><article data-id='1226381761994293283'>h10也把dlt减配了 魏总，这是为何？ 作者：我哭得那么真</article>",
                "1226381761994293283", "h10也把dlt减配了", "魏总，这是为何？", "我哭得那么真") &&
            Checker.TryMatchIqiyiCrawlerPage(
                "<title>中国车出海狂销，魏建军为何急得直跳脚-爱奇艺</title><meta data-video-id='v_1ws4pk79xyw'><main>发布者：悦悦聊社科</main>",
                "v_1ws4pk79xyw", "中国车出海狂销，魏建军为何急得直跳脚？", "", "悦悦聊社科");
        Console.WriteLine((renderedSocialRemovalPassed ? "PASS " : "FAIL ") + "雪球和微博渲染页只接受带目标编号的明确不可见状态");
        if (!renderedSocialRemovalPassed) _failures++;

        string dzhTitle;
        bool dzhPagePassed = Checker.TryMatchDzhArticlePage(
            "var pageData={\"ErrCode\":0,\"Data\":{\"RequestDocId\":\"gsxw-hk-2222921\",\"Found\":1,\"Docs\":[{\"Title\":\"魏建军亲自道歉，值吗？长城汽车老板给所有企业上了一课\",\"Summary\":\"目标资讯正文\"}]}};",
            "gsxw-hk-2222921", "魏建军亲自道歉，值吗？长城汽车老板给所有企业上了一课", "", out dzhTitle) &&
            dzhTitle.Contains("魏建军亲自道歉") &&
            Checker.IsUcMissingArticlePage("<title>UC头条</title><main>文章不存在</main>",
                "https://m.uczzd.cn/ucnews/news?aid=43955633116663337", "43955633116663337") &&
            Checker.IsDingxinwenMissingTopicResponse("{\"code\":500,\"msg\":\"帖子不存在!!!!\"}");
        Console.WriteLine((dzhPagePassed ? "PASS " : "FAIL ") + "大智慧公开页目标编号、Found 状态和标题联合识别");
        if (!dzhPagePassed) _failures++;

        string tonghuashunAuthor;
        bool tonghuashunIdentityPassed = Checker.IsTonghuashunRemovedResponse(
                "{\"status_code\":-2,\"status_msg\":\"帖子已被删除\"}") &&
            Checker.TryMatchTonghuashunPost(
                "{\"status_code\":0,\"data\":{\"post\":{\"content_id\":\"1d2xgg7ixtbbbu8c0bac21\",\"valid\":1," +
                "\"content\":\"老魏是汽车卖不过迪子，技术不如迪子\",\"user\":{\"nickname\":\"测试作者\"}}}}",
                "1d2xgg7ixtbbbu8c0bac21", "老魏是汽车卖不过迪子", "技术不如迪子", out tonghuashunAuthor) &&
            tonghuashunAuthor == "测试作者" && Checker.IsEastmoneyFortuneRemovedPage("抱歉，该文章已被删除 4秒后返回首页");
        Console.WriteLine((tonghuashunIdentityPassed ? "PASS " : "FAIL ") + "同花顺公开接口目标编号和明确删除状态识别");
        if (!tonghuashunIdentityPassed) _failures++;

        CheckResult edgePending = MainForm.CreateEdgeCompatibilityResult(new CheckJob
        {
            Number = 7,
            Url = "http://xueqiu.com/9632298307/400108447",
            ExpectedTitle = "表格采集的第一句话",
            ExpectedExcerpt = "正文摘要",
            ExpectedAuthor = "投资之道在于懒",
            SourceSheet = "测试表",
            SourceRow = 8
        });
        bool edgePendingPassed = edgePending.Verdict == "人工复核" && edgePending.StatusCode == "浏览器待核验" &&
            !edgePending.DeepReviewed && !edgePending.EdgeFastReviewed && edgePending.ExpectedTitle == "表格采集的第一句话" &&
            edgePending.ExpectedExcerpt == "正文摘要" && edgePending.SourceRow == 8;
        edgePendingPassed = edgePendingPassed && edgePending.ExpectedAuthor == "投资之道在于懒";
        Console.WriteLine((edgePendingPassed ? "PASS " : "FAIL ") + "浏览器兼容任务保留作者和断点字段");
        if (!edgePendingPassed) _failures++;

        bool targetedRenderPassed = DeepReviewForm.ShouldFastRenderPlatform(new CheckResult
        {
            OriginalUrl = "https://weibo.com/123456789/AbCdEf",
            Platform = "新浪微博"
        }) && DeepReviewForm.ShouldFastRenderPlatform(new CheckResult
        {
            OriginalUrl = "https://www.dongchedi.com/article/123456789",
            Platform = "懂车帝"
        });
        Console.WriteLine((targetedRenderPassed ? "PASS " : "FAIL ") + "短渲染覆盖已验证动态平台");
        if (!targetedRenderPassed) _failures++;

        bool publicReaderCoveragePassed =
            Checker.ShouldTryPublicCloudForUnresolved(
                new Uri("https://xueqiu.com/2037102031/396950721"),
                new CheckResult { Platform = "雪球" }) &&
            Checker.ShouldTryPublicCloudForUnresolved(
                new Uri("https://www.dcdapp.com/article/7661190628492214809"),
                new CheckResult { Platform = "懂车帝" }) &&
            Checker.ShouldTryPublicCloudForUnresolved(
                new Uri("https://www.douyin.com/article/7647574419547671850"),
                new CheckResult { Platform = "抖音" }) &&
            Checker.ShouldTryPublicCloudForUnresolved(
                new Uri("https://www.jianshu.com/p/fe6551ad86d7"),
                new CheckResult { Platform = "简书" });
        Console.WriteLine((publicReaderCoveragePassed ? "PASS " : "FAIL ") + "雪球和懂车帝空壳进入公开补证");
        if (!publicReaderCoveragePassed) _failures++;

        Expect("懂车帝登录壳不等于下架", "人工复核", EvidenceAdjudicator.Decide(new[]
        {
            new VerificationEvidence { Kind = EvidenceKind.GenericPage, Strength = EvidenceStrength.Supporting,
                IsCurrentResponse = true, Message = "懂车帝登录页/验证码页" }
        }));

        Expect("统一证据：目标正文覆盖通用删除词", "仍可访问", EvidenceAdjudicator.Decide(new[]
        {
            new VerificationEvidence { Kind = EvidenceKind.TargetContentPresent, Strength = EvidenceStrength.Strong, IsCurrentResponse = true, Message = "目标正文仍在" },
            new VerificationEvidence { Kind = EvidenceKind.GenericPage, Strength = EvidenceStrength.Supporting, IsCurrentResponse = true, Message = "推荐区出现删除字样" }
        }));
        Expect("统一证据：明确目标失效", "已失效", EvidenceAdjudicator.Decide(new[]
        {
            new VerificationEvidence { Kind = EvidenceKind.TargetRemovalExplicit, Strength = EvidenceStrength.Conclusive, IsCurrentResponse = true, Message = "目标内容不存在" }
        }));
        Expect("统一证据：接口身份缓存不能证明仍在", "人工复核", EvidenceAdjudicator.Decide(new[]
        {
            new VerificationEvidence { Kind = EvidenceKind.IdentityOnly, Strength = EvidenceStrength.Strong, IsCurrentResponse = true, Message = "接口仍返回编号" }
        }));
        Expect("统一证据：存在和失效冲突保留复核", "人工复核", EvidenceAdjudicator.Decide(new[]
        {
            new VerificationEvidence { Kind = EvidenceKind.TargetContentPresent, Strength = EvidenceStrength.Strong, IsCurrentResponse = true, Message = "目标正文仍在" },
            new VerificationEvidence { Kind = EvidenceKind.TargetRemovalExplicit, Strength = EvidenceStrength.Conclusive, IsCurrentResponse = true, Message = "目标已删除" }
        }));

        Expect("同平台跨子域跳转保留可靠标题身份", "仍可访问", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://feng.ifeng.com/c/8uuAsXwuyHo",
                FinalUrl = "https://ishare.ifeng.com/c/s/8uuAsXwuyHo",
                ExpectedTitle = "盖世周报|奔驰扩建工厂；博世美国首座半导体工厂启动样品生产",
                Platform = "凤凰新闻"
            },
            new RenderedPageData
            {
                Url = "https://ishare.ifeng.com/c/s/8uuAsXwuyHo",
                Title = "盖世周报|奔驰扩建工厂；博世美国首座半导体工厂启动样品生产__凤凰网",
                Text = "盖世周报 发布时间 2026-07-20 凤凰网 当前目标新闻内容仍可阅读。",
                Html = "<main>当前目标新闻内容仍可阅读</main>"
            }));

        Expect("正文存在覆盖相关推荐删除字样", "仍可访问", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://news.example.com/article/12345678",
                FinalUrl = "https://news.example.com/article/12345678",
                ExpectedTitle = "新能源行业进入新的竞争阶段",
                ExpectedExcerpt = "魏建军表示企业应当长期坚持技术研发并持续改善用户体验这是行业健康发展的基础"
            },
            new RenderedPageData
            {
                Url = "https://news.example.com/article/12345678",
                Title = "行业竞争进入下半场",
                Text = "作者 张三 发布时间 2026-07-17 魏建军表示企业应当长期坚持技术研发并持续改善用户体验这是行业健康发展的基础。相关推荐：另一篇内容已删除。评论 分享 收藏",
                Html = "<article data-id='12345678'><p>魏建军表示企业应当长期坚持技术研发并持续改善用户体验这是行业健康发展的基础。</p></article>"
            }));

        Expect("标题变化但编号正文仍在", "仍可访问", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://news.example.com/article/87654321",
                FinalUrl = "https://news.example.com/article/87654321",
                ExpectedTitle = "旧标题"
            },
            new RenderedPageData
            {
                Url = "https://news.example.com/article/87654321",
                Title = "编辑后的新标题",
                Text = "作者 李四 发布于 2026-07-17 这是一段足够长的目标文章正文，用于确认内容页面并非只有平台外壳。文章继续讨论产品、技术、市场和用户反馈，并包含完整的阅读、评论、收藏和分享区域。",
                Html = "<main role='main'><article data-content-id='87654321'><p>完整正文</p></article></main>"
            }));

        Expect("雪球采集首句不是网页标题", "仍可访问", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "http://xueqiu.com/9632298307/400108447",
                FinalUrl = "https://xueqiu.com/9632298307/400108447",
                ExpectedTitle = "表格采集的是文章第一句话并不一定是网页标题"
            },
            new RenderedPageData
            {
                Url = "https://xueqiu.com/9632298307/400108447",
                Title = "雪球",
                Text = "作者 投资观察 发布于 2026-07-17 这是一条仍然正常展示的雪球长文，正文持续讨论企业经营、市场表现、产品规划和用户反馈。页面中可以看到完整段落，而不是推荐列表或空白网页外壳。这里继续补充足够的目标帖子内容，用来确认访问地址中的用户编号和帖子编号仍指向当前正文。评论 收藏 分享",
                Html = "<main><article class='status-detail'><p>仍然正常展示的雪球长文和完整正文内容</p></article></main>",
                MainText = "作者 投资观察 发布于 2026-07-17 这是一条仍然正常展示的雪球长文，正文持续讨论企业经营、市场表现、产品规划和用户反馈。页面中可以看到完整段落，而不是推荐列表或空白网页外壳。这里继续补充足够的目标帖子内容，用来确认访问地址中的用户编号和帖子编号仍指向当前正文。评论 收藏 分享",
                MainHtml = "<article class='status-detail'><p>仍然正常展示的雪球长文和完整正文内容</p></article>"
            }));

        Expect("微博通用标题但帖子正文仍在", "仍可访问", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://weibo.com/1234567890/AbCdEfGhI",
                FinalUrl = "https://weibo.com/1234567890/AbCdEfGhI",
                ExpectedTitle = "采集系统保存的第一句话"
            },
            new RenderedPageData
            {
                Url = "https://weibo.com/1234567890/AbCdEfGhI",
                Title = "微博",
                Text = "作者 汽车资讯 发布时间 2026-07-17 这条微博正文仍然存在，包含企业新闻、技术路线、市场反馈以及后续说明。页面主体保留了原帖的作者、发布时间和互动区域，还有足够长的正文用于区别推荐流、登录外壳与错误页面。以下内容继续说明事件背景和公开信息。评论 转发 收藏 分享",
                Html = "<main><article class='weibo-detail'><p>这条微博正文仍然存在并包含完整内容</p></article></main>",
                MainText = "作者 汽车资讯 发布时间 2026-07-17 这条微博正文仍然存在，包含企业新闻、技术路线、市场反馈以及后续说明。页面主体保留了原帖的作者、发布时间和互动区域，还有足够长的正文用于区别推荐流、登录外壳与错误页面。以下内容继续说明事件背景和公开信息。评论 转发 收藏 分享",
                MainHtml = "<article class='weibo-detail'><p>这条微博正文仍然存在并包含完整内容</p></article>"
            }));

        Expect("雪球网页外壳不能仅凭原地址判有效", "人工复核", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://xueqiu.com/9632298307/400108447",
                FinalUrl = "https://xueqiu.com/9632298307/400108447",
                ExpectedTitle = "采集系统保存的第一句话"
            },
            new RenderedPageData
            {
                Url = "https://xueqiu.com/9632298307/400108447",
                Title = "雪球",
                Text = "登录后查看更多内容 下载雪球 App",
                Html = "<div id='app'>平台网页外壳</div>"
            }));

        Expect("评论编号作者正文组合确认", "仍可访问", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://guba.eastmoney.com/news,600000,1234567890.html",
                FinalUrl = "https://guba.eastmoney.com/news,600000,1234567890.html",
                ExpectedTitle = "公司产品表现低于市场预期",
                ExpectedAuthor = "价值观察员"
            },
            new RenderedPageData
            {
                Url = "https://guba.eastmoney.com/news,600000,1234567890.html",
                Title = "股吧_东方财富网",
                Text = "价值观察员 公司产品表现低于市场预期，这是一条用户评论而不是完整文章。回复 点赞 分享",
                Html = "<div class='comment-detail' data-id='1234567890'>价值观察员 公司产品表现低于市场预期，这是一条用户评论而不是完整文章。</div>",
                MainText = "价值观察员 公司产品表现低于市场预期，这是一条用户评论而不是完整文章。回复 点赞 分享",
                MainHtml = "<div class='comment-detail' data-id='1234567890'>价值观察员 公司产品表现低于市场预期，这是一条用户评论而不是完整文章。</div>"
            }));

        Expect("作者缺失不能证明删除", "人工复核", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://weibo.com/1234567890/AbCdEfGhI",
                FinalUrl = "https://weibo.com/1234567890/AbCdEfGhI",
                ExpectedTitle = "供应商采集的正文首句",
                ExpectedAuthor = "汽车资讯"
            },
            new RenderedPageData
            {
                Url = "https://weibo.com/1234567890/AbCdEfGhI",
                Title = "微博",
                Text = "登录后查看更多内容 下载微博客户端",
                Html = "<div class='login'>登录后查看更多内容</div>"
            }));

        Expect("原标题加正文结构组合确认", "仍可访问", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://news.example.com/p/slug-only",
                FinalUrl = "https://news.example.com/p/slug-only",
                ExpectedTitle = "长城汽车发布全新技术路线"
            },
            new RenderedPageData
            {
                Url = "https://news.example.com/p/slug-only",
                Title = "长城汽车发布全新技术路线",
                Text = "作者 王五 发布时间 2026-07-17 长城汽车发布全新技术路线。文章正文详细介绍技术研发、产品规划、用户体验和未来市场计划，并给出了完整的背景信息、采访内容和数据说明。这段内容足够长，用来确认页面展示的是目标文章正文，而不是搜索结果、推荐卡片或者只有标题的网页外壳。阅读 评论 收藏 分享",
                Html = "<article><h1>长城汽车发布全新技术路线</h1><p>完整文章正文和采访内容</p></article>"
            }));

        Expect("目标编号和可靠页面标题可确认仍在", "仍可访问", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://news.example.com/article/77889900",
                FinalUrl = "https://news.example.com/article/77889900",
                ExpectedTitle = "长城汽车发布新的市场计划"
            },
            new RenderedPageData
            {
                Url = "https://news.example.com/article/77889900",
                Title = "长城汽车发布新的市场计划_示例新闻网",
                Text = "长城汽车发布新的市场计划 作者 发布时间 阅读 评论 分享，页面当前仍保留目标内容编号和可见信息。这里还有当前页面展示的来源、发布时间、作者介绍和相关正文说明，用于排除只有标题的缓存外壳。",
                Html = "<div><h1>长城汽车发布新的市场计划</h1><span>作者 发布时间 阅读 评论 分享</span></div>"
            }));

        Expect("通用外壳回显编号", "人工复核", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://www.kuaishou.com/short-video/3xabcdef12",
                FinalUrl = "https://www.kuaishou.com/short-video/3xabcdef12",
                ExpectedTitle = "目标视频标题"
            },
            new RenderedPageData
            {
                Url = "https://www.kuaishou.com/short-video/3xabcdef12",
                Title = "短视频-快手",
                Text = "发现更多精彩短视频 下载快手 App",
                Html = "<html data-route='3xabcdef12'><div id='app'></div></html>"
            }));

        Expect("知乎地址和脚本残留编号不能单独判有效", "人工复核", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://www.zhihu.com/question/123456789/answer/987654321",
                FinalUrl = "https://www.zhihu.com/question/123456789/answer/987654321",
                ExpectedTitle = "采集系统保存的回答第一句话"
            },
            new RenderedPageData
            {
                Url = "https://www.zhihu.com/question/123456789/answer/987654321",
                Title = "知乎 - 有问题，就会有答案",
                Text = "登录知乎，浏览更多优质内容。下载知乎 App，发现更多内容。",
                Html = "<div id='root' data-answer-id='987654321'>登录后查看</div>"
            }));

        Expect("知乎回答跳回同一问题页可确认失效", "已失效", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://www.zhihu.com/question/123456789/answer/987654321",
                FinalUrl = "https://www.zhihu.com/question/123456789",
                ExpectedTitle = "采集标题可能只是回答第一句话"
            },
            new RenderedPageData
            {
                Url = "https://www.zhihu.com/question/123456789",
                Title = "问题页标题 - 知乎",
                Text = "登录知乎，浏览更多优质内容。问题仍在，但目标回答已经不在最终地址中。",
                Html = "<main data-question-id='123456789'>问题页</main>"
            }));

        Expect("知乎跳到其他问题不能确认失效", "人工复核", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://www.zhihu.com/question/123456789/answer/987654321",
                FinalUrl = "https://www.zhihu.com/question/555555555",
                ExpectedTitle = "采集标题可能只是回答第一句话"
            },
            new RenderedPageData
            {
                Url = "https://www.zhihu.com/question/555555555",
                Title = "知乎 - 有问题，就会有答案",
                Text = "登录知乎，浏览更多优质内容。",
                Html = "<main data-question-id='555555555'>登录后查看</main>"
            }));

        Expect("东方财富明确错误地址", "已失效", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://guba.eastmoney.com/news,600000,1234567890.html",
                FinalUrl = "https://guba.eastmoney.com/error?type=2",
                ExpectedTitle = "目标股吧文章"
            },
            new RenderedPageData
            {
                Url = "https://guba.eastmoney.com/error?type=2",
                Title = "页面不存在",
                Text = "页面不存在",
                Html = "<html><body>页面不存在</body></html>"
            }));

        Expect("目标正文区明确删除", "已失效", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://news.example.com/article/55667788",
                FinalUrl = "https://news.example.com/article/55667788",
                ExpectedTitle = "已下架的目标文章"
            },
            new RenderedPageData
            {
                Url = "https://news.example.com/article/55667788",
                Title = "内容提示",
                Text = "该内容已删除",
                Html = "<main class='empty-state'>该内容已删除</main>",
                MainText = "该内容已删除",
                MainHtml = "<main class='empty-state'>该内容已删除</main>"
            }));

        Expect("通用删除空状态直接下架", "已失效", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://news.example.com/article/99887766",
                FinalUrl = "https://news.example.com/article/99887766",
                ExpectedTitle = "另一个目标文章"
            },
            new RenderedPageData
            {
                Url = "https://news.example.com/article/99887766",
                Title = "提示",
                Text = "该文章已删除",
                Html = "<div class='empty-state'>该文章已删除</div>"
            }));

        Expect("财富号文章已被删除变体", "已失效", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://caifuhao.eastmoney.com/news/20260715014814707315640",
                FinalUrl = "https://caifuhao.eastmoney.com/news/20260715014814707315640",
                ExpectedTitle = "半年少赚近40亿"
            },
            new RenderedPageData
            {
                Url = "https://caifuhao.eastmoney.com/news/20260715014814707315640",
                Title = "财富号_东方财富网",
                Text = "首页 社区 正文 抱歉，该文章已被删除 4秒后页面将自动返回财富号首页",
                Html = "<main><div class='empty-state'>抱歉，该文章已被删除</div></main>",
                MainText = "抱歉，该文章已被删除 4秒后页面将自动返回财富号首页",
                MainHtml = "<div class='empty-state'>抱歉，该文章已被删除</div>"
            }));

        Expect("知乎页面标题明确作者删除", "已失效", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://www.zhihu.com/question/2006945324976056152/answer/2007788604982858132",
                FinalUrl = "https://www.zhihu.com/question/2006945324976056152/answer/2007788604982858132",
                ExpectedTitle = "如何看待长城汽车董事长魏建军"
            },
            new RenderedPageData
            {
                Url = "https://www.zhihu.com/question/2006945324976056152/answer/2007788604982858132",
                Title = "抱歉，该内容已被作者删除 - 知乎",
                Text = "抱歉，该内容已被作者删除",
                Html = "<main>抱歉，该内容已被作者删除</main>"
            }));

        Expect("雪球页面标题明确原帖删除", "已失效", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://xueqiu.com/2037102031/377574152",
                FinalUrl = "https://xueqiu.com/2037102031/377574152",
                ExpectedTitle = "长城汽车相关讨论"
            },
            new RenderedPageData
            {
                Url = "https://xueqiu.com/2037102031/377574152",
                Title = "原帖已被作者删除 - 雪球",
                Text = "原帖已被作者删除",
                Html = "<main>原帖已被作者删除</main>"
            }));

        Expect("腾讯视频错误页明确视频不见", "已失效", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://v.qq.com/x/page/q3195ftx5t4.html",
                FinalUrl = "https://v.qq.com/error.html",
                ExpectedTitle = "你觉得这车怎么样？"
            },
            new RenderedPageData
            {
                Url = "https://v.qq.com/error.html",
                Title = "那条视频不见了-腾讯视频",
                Text = "那条视频不见了",
                Html = "<main class='empty-state'>那条视频不见了</main>"
            }));

        Expect("腾讯新闻错误页明确作者删除", "已失效", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://kandianshare.html5.qq.com/v2/news/2282553879446387010",
                FinalUrl = "https://newsa.html5.qq.com/v1/share-article?docId=2282553879446387010",
                ExpectedTitle = "华山论剑｜魏建军亲自代言"
            },
            new RenderedPageData
            {
                Url = "https://newsa.html5.qq.com/v1/share-article?docId=2282553879446387010",
                Title = "文章打开失败",
                Text = "该文章已被作者删除",
                Html = "<div class='ArticleGoneWrap-d19'><h1>文章打开失败</h1><p>该文章已被作者删除</p></div>"
            }));

        Expect("推荐流不是确定下架", "人工复核", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://www.toutiao.com/article/1234567890123456789/",
                FinalUrl = "https://www.toutiao.com/article/1234567890123456789/",
                ExpectedTitle = "目标头条文章"
            },
            new RenderedPageData
            {
                Url = "https://www.toutiao.com/article/1234567890123456789/",
                Title = "今日头条",
                Text = "下载头条APP 发布作品 " + new string('推', 700),
                Html = "<div id='app'>推荐流</div>"
            }));

        Expect("浏览器 HTTP 502 错误页进入访问异常待重试", "暂时异常", Checker.ClassifyRenderedPage(
            new CheckResult
            {
                OriginalUrl = "https://muyinghaow.dianjinghuw.com/article/1",
                FinalUrl = "https://muyinghaow.dianjinghuw.com/article/1",
                ExpectedTitle = "目标网媒文章"
            },
            new RenderedPageData
            {
                Url = "https://muyinghaow.dianjinghuw.com/article/1",
                Title = "当前无法使用此页面",
                Text = "muyinghaow.dianjinghuw.com 当前无法处理此请求。 HTTP ERROR 502 刷新",
                Html = "<main><h1>当前无法使用此页面</h1><p>HTTP ERROR 502</p></main>"
            }));

        string checkpointDirectory = Path.Combine(Path.GetTempPath(), "LinkCheckerCheckpointTest_" + Guid.NewGuid().ToString("N"));
        bool checkpointPassed = false;
        try
        {
            Directory.CreateDirectory(checkpointDirectory);
            string outputPath = Path.Combine(checkpointDirectory, "result.csv");
            var jobs = new List<CheckJob>
            {
                new CheckJob { Number = 1, Url = "https://example.com/one" },
                new CheckJob { Number = 2, Url = "https://example.com/two" }
            };
            using (var store = new AuditCheckpointStore(outputPath, "INPUT-A", true))
                store.Append(new CheckResult { Number = 1, OriginalUrl = jobs[0].Url, Verdict = "仍可访问" });
            File.AppendAllText(outputPath + ".checkpoint.jsonl", "{incomplete\r\n", new System.Text.UTF8Encoding(false));
            Dictionary<int, CheckResult> recovered;
            using (var store = new AuditCheckpointStore(outputPath, "INPUT-A", true))
                recovered = store.Load(jobs, ignored => { });
            bool mismatchRejected = false;
            try { using (var ignored = new AuditCheckpointStore(outputPath, "INPUT-B", true)) { } }
            catch (InvalidDataException) { mismatchRejected = true; }
            using (var reset = new AuditCheckpointStore(outputPath, "INPUT-A", false)) { }
            checkpointPassed = recovered.Count == 1 && recovered[1].Verdict == "仍可访问" && mismatchRejected &&
                !File.Exists(outputPath + ".checkpoint.jsonl") && !File.Exists(outputPath + ".checkpoint.json");
        }
        finally
        {
            try { Directory.Delete(checkpointDirectory, true); } catch { }
        }
        Console.WriteLine((checkpointPassed ? "PASS " : "FAIL ") + "全量核验检查点逐条恢复、坏尾行容错和输入隔离");
        if (!checkpointPassed) _failures++;

        return _failures == 0 ? 0 : 1;
    }
}
