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
            Checker.NormalizeVisibleVerdict("疑似已处置") == "疑似已处置";
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
            genericController.Observe(genericA, blockedObservations[0].Value, out pausedPlatform) &&
            pausedPlatform == "news-a.example.com" &&
            genericController.IsPaused(genericA) &&
            !genericController.IsPaused(genericB) &&
            genericController.PausedPlatforms.SequenceEqual(new[] { "news-a.example.com" });
        bool preflightPassed = preflightSelectionPassed && blockedSummary.RequiresDecision &&
            BatchRunSafetyPolicy.ShouldPauseAfterPreflight(blockedSummary, false) &&
            !BatchRunSafetyPolicy.ShouldPauseAfterPreflight(blockedSummary, true) &&
            !BatchRunSafetyPolicy.ShouldUseGlobalCircuitBreaker(true) &&
            BatchRunSafetyPolicy.ShouldUseGlobalCircuitBreaker(false) &&
            blockedSummary.TransientRestrictions == 4 && platformPausePassed && genericSitePausePassed;
        preflightPassed = preflightPassed &&
            MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "暂时异常" }, false) &&
            !MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "人工复核" }, false) &&
            MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "人工复核" }, true) &&
            !MainForm.ShouldDiscardForResume(new CheckResult { Verdict = "仍可访问" }, true);
        Console.WriteLine((preflightPassed ? "PASS " : "FAIL ") + "跨平台小样本预检和平台独立熔断");
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
            !MainForm.IsEvidenceReviewCandidate(new CheckResult { Verdict = "人工复核", SkipDeepReview = true });
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
        bool evidenceEscalationRoutingPassed =
            SessionStore.CurrentEngineVersion == "4.0.0" &&
            infrastructureDeferred.Number == 88 &&
            infrastructureDeferred.Verdict == "暂时异常" &&
            infrastructureDeferred.SkipDeepReview &&
            infrastructureDeferred.InfrastructureKey == "IP 203.0.113.8" &&
            infrastructureDeferred.EvidenceTrail != null &&
            infrastructureDeferred.EvidenceTrail.Count == 1 &&
            infrastructureDeferred.EvidenceStage.Contains("基础设施");
        Console.WriteLine((evidenceEscalationRoutingPassed ? "PASS " : "FAIL ") +
            "4.0 全量结果、共享基础设施复用和自动追证字段");
        if (!evidenceEscalationRoutingPassed) _failures++;

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

        return _failures == 0 ? 0 : 1;
    }
}
