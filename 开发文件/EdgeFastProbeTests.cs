using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LinkDispositionChecker;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal sealed class EdgeFastProbeTestForm : Form
{
    private readonly WebView2 _browser = new WebView2();
    private readonly string _profile;

    public EdgeFastProbeTestForm()
    {
        _profile = Path.Combine(Path.GetTempPath(), "LinkCheckerEdgeFastTest_" + Guid.NewGuid().ToString("N"));
        ShowInTaskbar = false;
        Width = 320;
        Height = 240;
        _browser.Dock = DockStyle.Fill;
        Controls.Add(_browser);
        Shown += async delegate
        {
            try
            {
                Directory.CreateDirectory(_profile);
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, _profile);
                await _browser.EnsureCoreWebView2Async(environment);
                var watch = Stopwatch.StartNew();
                Task<EdgeFetchedResponse>[] requests = Enumerable.Range(0, 8).Select(index =>
                    DeepReviewForm.LoadEdgeResourceAsync(_browser.CoreWebView2,
                        "https://www.baidu.com/?edge-fast-test=" + index, 120000, CancellationToken.None)).ToArray();
                EdgeFetchedResponse[] responses = await Task.WhenAll(requests);
                watch.Stop();
                bool passed = responses.All(response => response != null && response.StatusCode == 200 && (response.Body ?? "").Length > 100);
                Console.WriteLine((passed ? "PASS" : "FAIL") + " Edge CDP concurrent fast fetch => completed=" +
                    responses.Count(response => response != null && response.StatusCode == 200) + "/8, seconds=" +
                    watch.Elapsed.TotalSeconds.ToString("0.00") + ", requests_per_second=" +
                    (8 / Math.Max(0.01, watch.Elapsed.TotalSeconds)).ToString("0.0"));
                string[] platformUrls =
                {
                    "http://www.baidu.com/", "https://www.toutiao.com/",
                    "http://xueqiu.com/", "https://www.zhihu.com/"
                };
                EdgeFetchedResponse[] platformResponses = await Task.WhenAll(platformUrls.Select(url =>
                    DeepReviewForm.LoadEdgeResourceAsync(_browser.CoreWebView2, url, 120000, CancellationToken.None)));
                bool platformPassed = platformResponses.All(response => response != null && response.StatusCode > 0);
                Console.WriteLine((platformPassed ? "PASS" : "FAIL") + " Edge mixed-platform fetch => " +
                    String.Join(", ", platformUrls.Select((url, index) => new Uri(url).Host + "=" + platformResponses[index].StatusCode)));
                await _browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.setUserAgentOverride",
                    "{\"userAgent\":\"osee2unifiedRelease/19540 osee2unifiedReleaseVersion/10.56.0 Mozilla/5.0\"}");
                await _browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.setExtraHTTPHeaders",
                    "{\"headers\":{\"x-api-version\":\"3.0.91\",\"Referer\":\"https://www.zhihu.com/\"}}");
                EdgeFetchedResponse zhihuProbe = await DeepReviewForm.LoadEdgeResourceAsync(_browser.CoreWebView2,
                    "https://api.zhihu.com/answers/2061029338363998793", 300000, CancellationToken.None);
                bool zhihuPassed = zhihuProbe != null && zhihuProbe.StatusCode > 0;
                bool zhihuRestricted = zhihuProbe != null && (zhihuProbe.StatusCode == 403 || zhihuProbe.StatusCode == 429);
                Console.WriteLine((zhihuPassed ? "PASS" : "FAIL") + " Edge Zhihu API route" +
                    (zhihuRestricted ? " (platform security restriction detected)" : "") + " => status=" +
                    (zhihuProbe == null ? 0 : zhihuProbe.StatusCode) + ", bytes=" +
                    (zhihuProbe == null ? 0 : (zhihuProbe.Body ?? "").Length));
                passed = passed && platformPassed;
                passed = passed && zhihuPassed;
                Environment.ExitCode = passed ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL Edge CDP fast fetch => " + ex);
                Environment.ExitCode = 1;
            }
            finally { Close(); }
        };
        FormClosed += delegate
        {
            try { if (Directory.Exists(_profile)) Directory.Delete(_profile, true); } catch { }
        };
    }
}

internal static class EdgeFastProbeTests
{
    [STAThread]
    public static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new EdgeFastProbeTestForm());
        return Environment.ExitCode;
    }
}
