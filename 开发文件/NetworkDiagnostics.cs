using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

internal static class NetworkDiagnostics
{
    private sealed class TestResult
    {
        public string Route;
        public string Url;
        public string Status;
        public string FinalUrl;
        public long Milliseconds;
        public string Error;
    }

    public static int Main()
    {
        try { return RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine("诊断工具异常：" + Flatten(ex));
            Console.WriteLine("请截图并发送此窗口。");
            Console.ReadKey();
            return 2;
        }
    }

    private static async Task<int> RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("链接失效核验工具 - 环境与网络诊断");
        Console.WriteLine("正在测试，请勿关闭窗口。最慢可能需要 1-2 分钟。\n");

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | (SecurityProtocolType)768 | SecurityProtocolType.Tls12;
        ServicePointManager.Expect100Continue = false;
        var lines = new List<string>();
        lines.Add("链接失效核验工具 - 环境与网络诊断报告");
        lines.Add("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        lines.Add("诊断版本：2.0");
        lines.Add("操作系统：" + ReadWindowsName());
        lines.Add("进程位数：" + (Environment.Is64BitProcess ? "64 位" : "32 位"));
        lines.Add("系统位数：" + (Environment.Is64BitOperatingSystem ? "64 位" : "32 位"));
        lines.Add("CLR：" + Environment.Version);
        lines.Add(".NET Framework：" + DescribeDotNetRelease(ReadDotNetRelease()));
        lines.Add("Edge WebView2 Runtime：" + (HasWebView2Runtime() ? "已检测到" : "未检测到（只影响浏览器深度复核）"));
        lines.Add("Edge/Chrome：" + (HasBrowser() ? "已检测到" : "未检测到（少数无界面网页补证会跳过）"));
        lines.Add("64 位程序文件：" + (HasPortableFiles("x64") ? "完整" : "缺失或当前不是便携包目录"));
        lines.Add("32 位程序文件：" + (HasPortableFiles("x86") ? "完整" : "缺失或当前不是便携包目录"));
        lines.Add("程序目录写入：" + (CanWriteDirectory(AppDomain.CurrentDomain.BaseDirectory) ? "可写" : "不可写，结果应保存到用户文档目录"));
        lines.Add("用户进度目录写入：" + (CanWriteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkDispositionChecker")) ? "可写" : "不可写，主程序将尝试备用目录"));
        lines.Add("TLS 设置：TLS 1.0 / 1.1 / 1.2");
        lines.Add("HTTP_PROXY 环境变量：" + (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HTTP_PROXY")) ? "未设置" : "已设置（值已隐藏）"));
        lines.Add("HTTPS_PROXY 环境变量：" + (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HTTPS_PROXY")) ? "未设置" : "已设置（值已隐藏）"));

        Uri proxyTest = new Uri("https://www.baidu.com/");
        try
        {
            IWebProxy proxy = WebRequest.GetSystemWebProxy();
            Uri route = proxy == null ? null : proxy.GetProxy(proxyTest);
            bool bypass = proxy == null || proxy.IsBypassed(proxyTest) || route == null || route == proxyTest;
            lines.Add("Windows 系统代理：" + (bypass ? "该测试地址走直连/未检测到代理" : "已检测到代理或 PAC 路由（地址已隐藏）"));
        }
        catch (Exception ex) { lines.Add("Windows 系统代理读取失败：" + DescribeError(ex)); }

        foreach (string host in new[] { "www.baidu.com", "www.toutiao.com", "xueqiu.com" })
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host);
                lines.Add("DNS " + host + "：成功，IPv4=" + addresses.Count(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) +
                    "，IPv6=" + addresses.Count(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6));
            }
            catch (Exception ex) { lines.Add("DNS " + host + "：失败，" + DescribeError(ex)); }
        }

        var urls = new List<string>
        {
            "http://www.baidu.com/", "https://www.baidu.com/",
            "http://www.toutiao.com/", "https://www.toutiao.com/",
            "http://xueqiu.com/", "https://xueqiu.com/"
        };
        string customPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "诊断链接.txt");
        if (File.Exists(customPath))
        {
            foreach (string line in File.ReadAllLines(customPath, Encoding.UTF8))
            {
                Uri uri;
                string value = (line ?? "").Trim();
                if (Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https") && !urls.Contains(value, StringComparer.OrdinalIgnoreCase))
                    urls.Add(value);
                if (urls.Count >= 9) break;
            }
        }

        var results = new List<TestResult>();
        using (HttpClient proxyClient = CreateClient(true))
        using (HttpClient directClient = CreateClient(false))
        {
            foreach (string url in urls)
            {
                results.Add(await TestAsync(proxyClient, "系统代理", url));
                results.Add(await TestAsync(directClient, "直连", url));
                Console.WriteLine("已完成：" + url);
            }
        }

        lines.Add("");
        lines.Add("连接测试：");
        foreach (TestResult result in results)
        {
            lines.Add("[" + result.Route + "] " + DisplayUrl(result.Url));
            lines.Add("  状态：" + result.Status + "，耗时：" + result.Milliseconds + " ms");
            if (!String.IsNullOrEmpty(result.FinalUrl)) lines.Add("  最终地址：" + DisplayUrl(result.FinalUrl));
            if (!String.IsNullOrEmpty(result.Error)) lines.Add("  错误：" + result.Error);
        }

        lines.Add("");
        if (results.Count > 0 && results.All(item => item.Status == "失败") &&
            results.All(item => (item.Error ?? "").IndexOf("连接被拒绝", StringComparison.Ordinal) >= 0))
        {
            lines.Add("诊断结论：DNS 正常，但普通程序通过系统代理和直连访问 HTTP/HTTPS 都被主动拒绝。此情况通常来自终端安全软件、代理白名单或网络策略，不是目标链接已失效。主工具会把这类连接失败保留为人工复核，不能靠放宽失效规则解决；请把本报告交给网络或终端管理人员确认是否允许该程序联网。");
        }
        else
        {
            lines.Add("诊断结论：只要系统代理或直连中至少一条路线能访问，快速核验通常可以工作。单个站点失败、超时、403、验证码或证书拦截都不能证明目标内容已经失效。");
        }

        lines.Add("");
        lines.Add("隐私说明：报告不包含用户名、密码、Cookie、代理地址、IP 地址、样本链接路径/参数或导入文件内容。");
        string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "网络诊断报告_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
        try { File.WriteAllLines(report, lines, new UTF8Encoding(true)); }
        catch
        {
            string reportFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkDispositionChecker", "Reports");
            try { Directory.CreateDirectory(reportFolder); report = Path.Combine(reportFolder, Path.GetFileName(report)); }
            catch { report = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.GetFileName(report)); }
            File.WriteAllLines(report, lines, new UTF8Encoding(true));
        }

        Console.WriteLine("\n诊断完成。报告已生成：\n" + report);
        Console.WriteLine("请把这个 txt 报告发给工具维护人员。按任意键关闭。");
        Console.ReadKey();
        return 0;
    }

    private static HttpClient CreateClient(bool useSystemProxy)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = useSystemProxy,
            UseCookies = false
        };
        if (useSystemProxy)
        {
            try
            {
                IWebProxy proxy = WebRequest.GetSystemWebProxy();
                if (proxy != null)
                {
                    proxy.Credentials = CredentialCache.DefaultNetworkCredentials;
                    handler.Proxy = proxy;
                }
                handler.UseDefaultCredentials = true;
            }
            catch { }
        }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/json;q=0.9,*/*;q=0.8");
        return client;
    }

    private static async Task<TestResult> TestAsync(HttpClient client, string route, string url)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                watch.Stop();
                return new TestResult
                {
                    Route = route,
                    Url = url,
                    Status = ((int)response.StatusCode) + " " + response.ReasonPhrase,
                    FinalUrl = response.RequestMessage != null && response.RequestMessage.RequestUri != null ? response.RequestMessage.RequestUri.AbsoluteUri : "",
                    Milliseconds = watch.ElapsedMilliseconds
                };
            }
        }
        catch (Exception ex)
        {
            watch.Stop();
            return new TestResult { Route = route, Url = url, Status = "失败", Milliseconds = watch.ElapsedMilliseconds, Error = DescribeError(ex) };
        }
    }

    private static string DisplayUrl(string value)
    {
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return "[地址已隐藏]";
        string root = uri.GetLeftPart(UriPartial.Authority) + "/";
        bool hasPrivatePart = uri.AbsolutePath != "/" || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment);
        return hasPrivatePart ? root + "[路径和参数已隐藏]" : root;
    }

    private static string DescribeError(Exception exception)
    {
        var types = new List<string>();
        var messages = new List<string>();
        for (Exception current = exception; current != null && types.Count < 5; current = current.InnerException)
        {
            types.Add(current.GetType().Name);
            if (!String.IsNullOrWhiteSpace(current.Message)) messages.Add(current.Message.ToLowerInvariant());
        }
        string all = String.Join(" ", messages);
        string category = "连接建立失败";
        if (all.Contains("certificate") || all.Contains("trust relationship") || all.Contains("ssl") || all.Contains("tls") || all.Contains("证书"))
            category = "TLS/证书验证失败";
        else if (all.Contains("name") && (all.Contains("resolve") || all.Contains("resolution")) || all.Contains("名称") && all.Contains("解析"))
            category = "名称解析失败";
        else if (all.Contains("timed out") || all.Contains("timeout") || all.Contains("canceled") || all.Contains("超时"))
            category = "连接超时";
        else if (all.Contains("refused") || all.Contains("拒绝"))
            category = "连接被拒绝";
        else if (all.Contains("reset") || all.Contains("closed") || all.Contains("终止") || all.Contains("关闭"))
            category = "连接被网关或远端重置";
        else if (all.Contains("proxy") && (all.Contains("auth") || all.Contains("407")) || all.Contains("代理") && all.Contains("身份"))
            category = "代理身份验证失败";
        return category + "（异常链：" + String.Join(" > ", types.Distinct()) + "）";
    }

    private static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (Exception current = exception; current != null && parts.Count < 5; current = current.InnerException)
            if (!String.IsNullOrWhiteSpace(current.Message)) parts.Add(current.GetType().Name + ": " + current.Message.Replace("\r", " ").Replace("\n", " "));
        return String.Join(" | ", parts);
    }

    private static int ReadDotNetRelease()
    {
        try { return Convert.ToInt32(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", 0)); }
        catch { return 0; }
    }

    private static string DescribeDotNetRelease(int release)
    {
        if (release >= 533320) return "4.8.1 或更高（Release " + release + "）";
        if (release >= 528040) return "4.8（Release " + release + "）";
        if (release >= 461808) return "4.7.2（Release " + release + "）";
        if (release >= 394802) return "4.6.2 或更高（Release " + release + "）";
        return release > 0 ? "版本过低（Release " + release + "）" : "未检测到完整安装";
    }

    private static string ReadWindowsName()
    {
        try
        {
            object product = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "Windows");
            object display = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "");
            object build = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "");
            return Convert.ToString(product) + " " + Convert.ToString(display) + "（Build " + Convert.ToString(build) + "）";
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    private static bool HasPortableFiles(string architecture)
    {
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, architecture);
        return File.Exists(Path.Combine(folder, "LinkChecker.exe")) &&
            File.Exists(Path.Combine(folder, "Microsoft.Web.WebView2.Core.dll")) &&
            File.Exists(Path.Combine(folder, "Microsoft.Web.WebView2.WinForms.dll")) &&
            File.Exists(Path.Combine(folder, "WebView2Loader.dll")) && File.Exists(Path.Combine(folder, "platform-rules.json"));
    }

    private static bool CanWriteDirectory(string directory)
    {
        string probe = "";
        try
        {
            Directory.CreateDirectory(directory);
            probe = Path.Combine(directory, ".diagnostic-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
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

    private static bool HasWebView2Runtime()
    {
        foreach (string root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "EdgeWebView", "Application")
        })
        {
            try
            {
                if (Directory.Exists(root) && Directory.GetDirectories(root)
                    .Any(directory => File.Exists(Path.Combine(directory, "msedgewebview2.exe")))) return true;
            }
            catch { }
        }
        string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        string version = Convert.ToString(Registry.GetValue(key, "pv", ""));
        return !String.IsNullOrWhiteSpace(version) && version != "0.0.0.0";
    }

    private static bool HasBrowser()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")
        }.Any(File.Exists);
    }
}
