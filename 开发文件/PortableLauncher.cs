using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("链接失效核验工具启动检查")]
[assembly: System.Reflection.AssemblyVersion("4.3.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("4.3.0.0")]

internal static class PortableLauncher
{
    private const int MinimumDotNetRelease = 394802;
    private static readonly List<string> ReportLines = new List<string>();
    private static readonly List<string> Errors = new List<string>();
    private static readonly List<string> Warnings = new List<string>();

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

    [STAThread]
    public static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string reportPath = "";
        try
        {
            ReportLines.Add("链接失效核验工具 - 启动检查报告");
            ReportLines.Add("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            ReportLines.Add("启动检查版本：4.3.0");
            ReportLines.Add("");

            if (LooksLikeArchiveTemporaryFolder(root))
                Errors.Add("检测到程序可能正在压缩包临时目录中运行。请先完整解压 ZIP，再双击“启动工具.cmd”。");

            string architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            string architectureFolder = Path.Combine(root, architecture);
            string executable = Path.Combine(architectureFolder, "LinkChecker.exe");
            bool portableLayout = Directory.Exists(architectureFolder);
            if (!portableLayout)
            {
                architectureFolder = root;
                executable = Path.Combine(root, "侵权链接处置核验工具.exe");
            }

            ReportLines.Add("系统：" + ReadWindowsName());
            ReportLines.Add("系统位数：" + (Environment.Is64BitOperatingSystem ? "64 位" : "32 位"));
            ReportLines.Add("选择程序：" + architecture + (portableLayout ? " 便携版" : " 本机开发目录"));
            int windowsBuild = ReadWindowsBuild();
            if (windowsBuild > 0 && windowsBuild < 10240)
                Warnings.Add("当前系统低于 Windows 10，不属于正式支持范围；快速核验可能可用，但浏览器组件和 TLS 兼容性无法保证。");
            string processor = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "").Trim();
            if (processor.IndexOf("ARM", StringComparison.OrdinalIgnoreCase) >= 0)
                Warnings.Add("检测到 ARM 架构 Windows。工具会尝试使用兼容模式启动，但该架构尚未作为正式验证环境。");

            int processors = Math.Max(1, Environment.ProcessorCount);
            long memoryBytes = ReadPhysicalMemoryBytes();
            ReportLines.Add("逻辑处理器：" + processors);
            ReportLines.Add("物理内存：" + (memoryBytes > 0 ? (memoryBytes / 1024d / 1024d / 1024d).ToString("0.0") + " GB" : "未能读取"));
            ReportLines.Add("建议性能模式：" + RecommendPerformance(processors, memoryBytes, Environment.Is64BitOperatingSystem));
            try
            {
                string driveRoot = Path.GetPathRoot(root);
                long free = String.IsNullOrWhiteSpace(driveRoot) ? 0 : new DriveInfo(driveRoot).AvailableFreeSpace;
                ReportLines.Add("所在磁盘剩余空间：" + (free / 1024d / 1024d / 1024d).ToString("0.0") + " GB");
                if (free > 0 && free < 500L * 1024 * 1024)
                    Warnings.Add("所在磁盘剩余空间不足 500MB，断点、浏览器缓存或导出可能失败。");
            }
            catch { ReportLines.Add("所在磁盘剩余空间：未能读取"); }

            int release = ReadDotNetRelease();
            ReportLines.Add(".NET Framework：" + DescribeDotNetRelease(release));
            if (release < MinimumDotNetRelease)
                Errors.Add("缺少 .NET Framework 4.6.2 或更高版本，主程序无法可靠启动。");

            string[] required = new[]
            {
                executable,
                Path.Combine(architectureFolder, "Microsoft.Web.WebView2.Core.dll"),
                Path.Combine(architectureFolder, "Microsoft.Web.WebView2.WinForms.dll"),
                Path.Combine(architectureFolder, "WebView2Loader.dll"),
                Path.Combine(architectureFolder, "platform-rules.json")
            };
            foreach (string file in required)
            {
                FileInfo info = null;
                try { info = new FileInfo(file); } catch { }
                if (info == null || !info.Exists || info.Length == 0)
                    Errors.Add("程序文件缺失或为空：" + Path.GetFileName(file));
            }
            ReportLines.Add("当前位数程序文件：" + (Errors.Any(item => item.IndexOf("程序文件", StringComparison.Ordinal) >= 0) ? "不完整" : "完整"));

            if (portableLayout)
            {
                string other = architecture == "x64" ? "x86" : "x64";
                if (!File.Exists(Path.Combine(root, other, "LinkChecker.exe")))
                    Warnings.Add("便携包缺少 " + other + " 程序，当前电脑仍可运行，但不适合继续转发给其他位数的电脑。");
            }

            bool tempWritable = CanWriteDirectory(Path.GetTempPath());
            bool userDataWritable = CanWriteDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkDispositionChecker"));
            bool packageWritable = CanWriteDirectory(root);
            ReportLines.Add("系统临时目录：" + (tempWritable ? "可写" : "不可写"));
            ReportLines.Add("用户进度目录：" + (userDataWritable ? "可写" : "不可写，将尝试文档或临时目录"));
            ReportLines.Add("程序所在目录：" + (packageWritable ? "可写" : "只读，结果将改存到用户文档目录"));
            if (!tempWritable) Errors.Add("系统临时目录不可写，网页请求和浏览器复核无法正常工作。");
            if (!userDataWritable) Warnings.Add("本机限制了 LocalAppData 写入，主程序会改用文档或临时目录保存断点进度。");

            try
            {
                string rootPath = Path.GetPathRoot(root);
                if (!String.IsNullOrEmpty(rootPath) && new DriveInfo(rootPath).DriveType == DriveType.Network)
                    Warnings.Add("工具位于网络盘。为避免权限、断连和杀毒拦截，建议复制到本机文档目录后运行。");
            }
            catch { }

            bool webView2 = HasWebView2Runtime();
            bool browser = FindBrowserExecutable().Length > 0;
            ReportLines.Add("Edge WebView2 Runtime：" + (webView2 ? "已检测到" : "未检测到"));
            ReportLines.Add("Edge/Chrome 浏览器：" + (browser ? "已检测到" : "未检测到"));
            ReportLines.Add("快速核验：" + (Errors.Count == 0 ? "可启动" : "不可启动"));
            ReportLines.Add("深度复核：" + (Errors.Count == 0 && webView2 ? "可启动" : "当前不可用或未验证"));
            if (!webView2)
                Warnings.Add("未检测到 Microsoft Edge WebView2 Runtime。快速核验仍可使用，浏览器深度复核不可用。");
            if (!browser)
                Warnings.Add("未检测到 Edge 或 Chrome。少数快速核验中的无界面网页补证会跳过，但普通网络核验仍可运行。");

            reportPath = WriteReport(root);
            if (Errors.Count > 0)
            {
                ShowFailure("启动检查未通过", Errors, reportPath);
                return 2;
            }
            if (Environment.GetCommandLineArgs().Any(item => String.Equals(item, "--check-only", StringComparison.OrdinalIgnoreCase)))
                return 0;

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = architectureFolder,
                UseShellExecute = true
            };
            Process process = Process.Start(startInfo);
            if (process == null)
            {
                Errors.Add("Windows 未能创建主程序进程。");
                reportPath = WriteReport(root);
                ShowFailure("无法启动", Errors, reportPath);
                return 3;
            }
            if (process.WaitForExit(1800))
            {
                Errors.Add("主程序启动后立即退出，退出代码：" + process.ExitCode + "。请把启动检查报告发给维护人员。");
                reportPath = WriteReport(root);
                ShowFailure("主程序立即退出", Errors, reportPath);
                return 4;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Errors.Add("启动检查器异常：" + Flatten(ex));
            try { reportPath = WriteReport(root); } catch { }
            ShowFailure("启动检查异常", Errors, reportPath);
            return 5;
        }
    }

    private static string WriteReport(string root)
    {
        var lines = new List<string>(ReportLines);
        lines.Add("");
        lines.Add("警告：");
        if (Warnings.Count == 0) lines.Add("- 无");
        else foreach (string warning in Warnings.Distinct()) lines.Add("- " + warning);
        lines.Add("");
        lines.Add("阻止启动的问题：");
        if (Errors.Count == 0) lines.Add("- 无");
        else foreach (string error in Errors.Distinct()) lines.Add("- " + error);
        lines.Add("");
        lines.Add("隐私说明：本报告不包含导入文件内容、待核验链接、Cookie、账号、密码、IP 或代理地址。");

        string fileName = "启动检查报告.txt";
        foreach (string directory in CandidateReportDirectories(root))
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, fileName);
                File.WriteAllLines(path, lines, new UTF8Encoding(true));
                return path;
            }
            catch { }
        }
        return "未能写入报告文件";
    }

    private static IEnumerable<string> CandidateReportDirectories(string root)
    {
        yield return root;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkDispositionChecker", "Reports");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Path.GetTempPath();
    }

    private static void ShowFailure(string title, IEnumerable<string> errors, string reportPath)
    {
        string message = String.Join("\n", errors.Distinct().Select(item => "• " + item).ToArray());
        if (!String.IsNullOrWhiteSpace(reportPath)) message += "\n\n报告位置：\n" + reportPath;
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static bool LooksLikeArchiveTemporaryFolder(string path)
    {
        string value = (path ?? "").ToLowerInvariant();
        string temp = (Path.GetTempPath() ?? "").TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        if (temp.Length == 0 || !value.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return false;
        return value.Contains("temp1_") || value.Contains(".zip") || value.Contains("rar$") ||
            value.Contains("\\7z") || value.Contains("\\wz") || value.Contains("temporary directory");
    }

    private static bool CanWriteDirectory(string directory)
    {
        if (String.IsNullOrWhiteSpace(directory)) return false;
        string probe = "";
        try
        {
            Directory.CreateDirectory(directory);
            probe = Path.Combine(directory, ".link-checker-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
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

    private static int ReadDotNetRelease()
    {
        try
        {
            object value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", 0);
            return Convert.ToInt32(value);
        }
        catch { return 0; }
    }

    private static string DescribeDotNetRelease(int release)
    {
        if (release >= 533320) return "4.8.1 或更高（Release " + release + "）";
        if (release >= 528040) return "4.8（Release " + release + "）";
        if (release >= 461808) return "4.7.2（Release " + release + "）";
        if (release >= MinimumDotNetRelease) return "4.6.2 或更高（Release " + release + "）";
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

    private static int ReadWindowsBuild()
    {
        int build;
        return Int32.TryParse(Convert.ToString(Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "")), out build)
            ? build : 0;
    }

    private static long ReadPhysicalMemoryBytes()
    {
        try
        {
            var status = new MemoryStatus();
            return GlobalMemoryStatusEx(status) ? (long)status.TotalPhysical : 0L;
        }
        catch { return 0L; }
    }

    private static string RecommendPerformance(int processors, long memory, bool is64Bit)
    {
        if (!is64Bit || processors <= 4 || (memory > 0 && memory <= 5L * 1024 * 1024 * 1024)) return "低配模式";
        if (processors <= 8 || (memory > 0 && memory <= 10L * 1024 * 1024 * 1024)) return "标准模式";
        return "高性能模式（网络受限时仍建议标准模式）";
    }

    private static bool HasWebView2Runtime()
    {
        string[] roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "EdgeWebView", "Application")
        };
        foreach (string root in roots)
        {
            try
            {
                if (Directory.Exists(root) && Directory.GetDirectories(root)
                    .Any(directory => File.Exists(Path.Combine(directory, "msedgewebview2.exe")))) return true;
            }
            catch { }
        }
        string[] registryPaths = new[]
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"HKEY_CURRENT_USER\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
        };
        foreach (string path in registryPaths)
        {
            string version = Convert.ToString(Registry.GetValue(path, "pv", ""));
            if (!String.IsNullOrWhiteSpace(version) && version != "0.0.0.0") return true;
        }
        return false;
    }

    private static string FindBrowserExecutable()
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
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (Exception current = exception; current != null && parts.Count < 5; current = current.InnerException)
            if (!String.IsNullOrWhiteSpace(current.Message))
                parts.Add(current.GetType().Name + ": " + current.Message.Replace("\r", " ").Replace("\n", " "));
        return String.Join(" | ", parts.ToArray());
    }
}
