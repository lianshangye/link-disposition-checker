using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LinkDispositionChecker;

internal static class NetworkFallbackTests
{
    public static int Main()
    {
        return RunAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        const string url = "http://127.0.0.1:18767/original-http";
        var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18767/");
        listener.Start();
        Task server = Task.Run(async delegate
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { break; }
                string path = context.Request.Url.AbsolutePath;
                string query = context.Request.Url.Query;
                int status;
                string contentType = "text/html; charset=utf-8";
                string text;
                if (path == "/original-http")
                {
                    status = 200;
                    contentType = "application/octet-stream";
                    text = "plain HTTP resource is available";
                }
                else if (path == "/deleted" && !String.IsNullOrWhiteSpace(query))
                {
                    status = 502;
                    text = "<html><title>Bad Gateway</title><body>HTTP ERROR 502</body></html>";
                }
                else if (path == "/deleted")
                {
                    status = 410;
                    text = "<html><title>Gone</title><body>gone</body></html>";
                }
                else if (path == "/target")
                {
                    status = 502;
                    text = "<html><title>Bad Gateway</title><body>HTTP ERROR 502</body></html>";
                }
                else
                {
                    status = 200;
                    text = "<html><title>Site home</title><main>网站首页正常运行，其他公开内容可以访问。</main></html>";
                }
                byte[] body = Encoding.UTF8.GetBytes(text);
                context.Response.StatusCode = status;
                context.Response.ContentType = contentType;
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, 0, body.Length);
                context.Response.Close();
            }
        });

        try
        {
            var checker = new Checker();
            CheckResult result = await checker.CheckAsync(url, 1, "", "", false, CancellationToken.None);
            bool originalPassed = result.StatusCode == "200" && result.FinalUrl == url && result.Verdict == "仍可访问";
            Console.WriteLine((originalPassed ? "PASS" : "FAIL") + " HTTP original route => " +
                result.StatusCode + " / " + result.FinalUrl + " / " + result.Verdict);

            CheckResult deleted = await checker.CheckAsync(
                "http://127.0.0.1:18767/deleted?utm_source=batch&share_token=demo",
                2, "目标文章", "", "", "网媒", "文章", false, CancellationToken.None);
            deleted = await checker.EscalateEvidenceAsync(deleted, CancellationToken.None);
            bool deletedPassed = deleted.Verdict == "已失效" && deleted.StatusCode == "410" &&
                deleted.EvidenceStage == "自动追证已确认" &&
                (deleted.AcquisitionAttempts ?? "").Contains("去除分享/统计参数");
            Console.WriteLine((deletedPassed ? "PASS" : "FAIL") +
                " tracking cleanup removal evidence => " + deleted.Verdict + " / " + deleted.Evidence);

            CheckResult target = await checker.CheckAsync(
                "http://127.0.0.1:18767/target", 3, "目标文章", "", "", "网媒", "文章",
                false, CancellationToken.None);
            target = await checker.EscalateEvidenceAsync(target, CancellationToken.None);
            bool controlPassed = target.Verdict == "暂时异常" &&
                target.SiteHealth == "站点首页可访问" &&
                MainForm.IsEvidenceReviewCandidate(target);
            Console.WriteLine((controlPassed ? "PASS" : "FAIL") +
                " same-site control routes target to browser evidence => " +
                target.Verdict + " / " + target.SiteHealth);
            return originalPassed && deletedPassed && controlPassed ? 0 : 1;
        }
        finally
        {
            listener.Stop();
            listener.Close();
            server.Wait(5000);
        }
    }
}
