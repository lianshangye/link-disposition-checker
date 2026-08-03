using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LinkDispositionChecker;

internal static class RotatingSampleBuilder
{
    private sealed class Candidate
    {
        public CheckJob Job;
        public string SourceFile;
        public string GroupKey;
        public string Score;
        public string UrlKey;
    }

    private const string OutputHeader =
        "\u5e8f\u53f7,\u5e73\u53f0\u540d\u79f0,\u6807\u9898,\u6458\u8981,\u8d26\u53f7\u6635\u79f0,\u94fe\u63a5,\u5185\u5bb9\u7c7b\u578b," +
        "\u6765\u6e90\u6587\u4ef6,\u6765\u6e90\u5de5\u4f5c\u8868,\u6765\u6e90\u884c,\u62bd\u6837\u79cd\u5b50";
    private const string HistoryHeader = OutputHeader + ",\u62bd\u6837\u65f6\u95f4";

    public static int Main(string[] args)
    {
        if (args.Length < 7) return 2;
        string output = Path.GetFullPath(args[0]);
        string history = Path.GetFullPath(args[1]);
        int maximum;
        int perGroup;
        int minimumNetMedia;
        if (!Int32.TryParse(args[2], out maximum) || maximum < 1) return 2;
        if (!Int32.TryParse(args[3], out perGroup) || perGroup < 1) return 2;
        if (!Int32.TryParse(args[4], out minimumNetMedia) || minimumNetMedia < 0 || minimumNetMedia > maximum) return 2;
        string seed = args[5] ?? "";
        string[] inputs = args.Skip(6).Where(File.Exists).Select(Path.GetFullPath)
            .Where(path => !SamePath(path, output) && !SamePath(path, history))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (inputs.Length == 0) return 3;

        HashSet<string> previousUrls = ReadPreviousUrls(history);
        HashSet<string> previousSeeds = ReadPreviousSeeds(history);
        if (previousSeeds.Contains(seed))
            throw new InvalidDataException("The rotating sample seed has already been used: " + seed);
        List<Candidate> loaded = inputs.SelectMany(path => Load(path, seed)).ToList();
        foreach (string input in inputs)
        {
            int sourceRows = loaded.Count(item => SamePath(item.SourceFile, input));
            Console.WriteLine("ROTATING_SOURCE_ROWS=" + Path.GetFileName(input) + "|" + sourceRows);
            if (sourceRows == 0) Console.WriteLine("ROTATING_EMPTY_SOURCE=" + input);
        }
        List<Candidate> all = loaded
            .GroupBy(item => item.UrlKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Score, StringComparer.Ordinal).First())
            .ToList();
        List<Candidate> available = all.Where(item => !previousUrls.Contains(item.UrlKey)).ToList();
        int availableNetMedia = available.Count(IsNetMedia);
        Console.WriteLine("ROTATING_AVAILABLE_NETMEDIA=" + availableNetMedia);
        if (available.Count == 0)
        {
            Console.WriteLine("ROTATING_POOL_EXHAUSTED=1");
            Console.WriteLine("ROTATING_AVAILABLE_URLS=0");
            return 5;
        }

        if (availableNetMedia < minimumNetMedia)
        {
            Console.WriteLine("ROTATING_NETMEDIA_POOL_EXHAUSTED=1");
            Console.WriteLine("ROTATING_REQUESTED_NETMEDIA=" + minimumNetMedia);
            Console.WriteLine("ROTATING_AVAILABLE_NETMEDIA=" + availableNetMedia);
            return 7;
        }
        List<Candidate> selected = SelectDiverse(available, maximum, perGroup, minimumNetMedia);
        if (selected.Count < maximum)
        {
            Console.WriteLine("ROTATING_POOL_EXHAUSTED=1");
            Console.WriteLine("ROTATING_REQUESTED_ROWS=" + maximum);
            Console.WriteLine("ROTATING_SAMPLE_ROWS=" + selected.Count);
            Console.WriteLine("ROTATING_AVAILABLE_URLS=" + available.Count);
            return 6;
        }
        int reusedUrls = selected.Count(item => previousUrls.Contains(item.UrlKey));
        if (reusedUrls != 0)
            throw new InvalidDataException("The rotating sample contains previously used content URLs.");
        if (selected.Select(item => item.UrlKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
            throw new InvalidDataException("The rotating sample contains duplicate content URLs.");
        WriteOutput(output, selected, seed);
        AppendHistory(history, selected, seed);

        Console.WriteLine("ROTATING_SOURCE_FILES=" + inputs.Length);
        Console.WriteLine("ROTATING_TOTAL_UNIQUE_URLS=" + all.Count);
        Console.WriteLine("ROTATING_PREVIOUSLY_USED=" + previousUrls.Count);
        Console.WriteLine("ROTATING_PREVIOUS_SEEDS=" + previousSeeds.Count);
        Console.WriteLine("ROTATING_REQUESTED_ROWS=" + maximum);
        Console.WriteLine("ROTATING_AVAILABLE_URLS=" + available.Count);
        Console.WriteLine("ROTATING_SAMPLE_ROWS=" + selected.Count);
        Console.WriteLine("ROTATING_SAMPLE_PLATFORMS=" + selected.Select(item => Platform(item.Job)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Console.WriteLine("ROTATING_SAMPLE_HOSTS=" + selected.Select(item => Host(item.Job.Url)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Console.WriteLine("ROTATING_SAMPLE_SOURCES=" + selected.Select(item => item.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Console.WriteLine("ROTATING_SAMPLE_NETMEDIA=" + selected.Count(IsNetMedia));
        Console.WriteLine("ROTATING_MINIMUM_NETMEDIA=" + minimumNetMedia);
        Console.WriteLine("ROTATING_REUSED_URLS=" + reusedUrls);
        Console.WriteLine("ROTATING_POOL_EXHAUSTED=" + (selected.Count < maximum ? "1" : "0"));
        Console.WriteLine("ROTATING_OUTPUT=" + output);
        Console.WriteLine("ROTATING_HISTORY=" + history);
        return selected.Count > 0 ? 0 : 4;
    }

    private static List<Candidate> SelectDiverse(List<Candidate> available, int maximum, int perGroup, int minimumNetMedia)
    {
        List<Candidate> netMedia = available.Where(IsNetMedia).ToList();
        if (netMedia.Count < minimumNetMedia) return new List<Candidate>();

        var selected = new List<Candidate>();
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Candidate item in SelectDiverseCore(netMedia, minimumNetMedia, perGroup))
        {
            if (selectedKeys.Add(item.UrlKey)) selected.Add(item);
        }

        List<Candidate> remaining = available.Where(item => !selectedKeys.Contains(item.UrlKey)).ToList();
        foreach (Candidate item in SelectDiverseCore(remaining, maximum - selected.Count, perGroup))
        {
            if (selectedKeys.Add(item.UrlKey)) selected.Add(item);
        }
        return selected;
    }

    private static List<Candidate> SelectDiverseCore(List<Candidate> available, int maximum, int perGroup)
    {
        if (maximum <= 0) return new List<Candidate>();
        List<Queue<Candidate>> queues = available
            .GroupBy(item => item.SourceFile + "|" + item.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Queue<Candidate>(group.OrderBy(item => item.Score, StringComparer.Ordinal)))
            .OrderBy(queue => queue.Peek().Score, StringComparer.Ordinal)
            .ToList();
        var selected = new List<Candidate>();
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int pass = 0; pass < perGroup && selected.Count < maximum; pass++)
        {
            foreach (Queue<Candidate> queue in queues)
            {
                if (selected.Count >= maximum) break;
                if (queue.Count == 0) continue;
                Candidate item = queue.Dequeue();
                if (selectedKeys.Add(item.UrlKey)) selected.Add(item);
            }
        }

        if (selected.Count < maximum)
        {
            foreach (Candidate item in queues.SelectMany(queue => queue).OrderBy(item => item.Score, StringComparer.Ordinal))
            {
                if (selected.Count >= maximum) break;
                if (selectedKeys.Add(item.UrlKey)) selected.Add(item);
            }
        }
        return selected;
    }

    private static bool IsNetMedia(Candidate item)
    {
        return String.Equals(Platform(item == null ? null : item.Job), "\u7f51\u5a92", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> ReadPreviousUrls(string history)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(history)) return result;
        try
        {
            foreach (CheckJob job in MainForm.LoadCsvJobs(history))
            {
                string key = CanonicalUrl(job.Url);
                if (!String.IsNullOrWhiteSpace(key)) result.Add(key);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("The rotating sample history cannot be read: " + history, ex);
        }
        return result;
    }

    private static HashSet<string> ReadPreviousSeeds(string history)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(history)) return result;
        bool first = true;
        foreach (string line in File.ReadLines(history, Encoding.UTF8))
        {
            if (first) { first = false; continue; }
            List<string> fields = ParseCsvLine(line);
            if (fields.Count >= 11 && !String.IsNullOrWhiteSpace(fields[10]))
                result.Add(fields[10].Trim());
        }
        return result;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var value = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < (line ?? "").Length; index++)
        {
            char current = line[index];
            if (current == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else quoted = !quoted;
            }
            else if (current == ',' && !quoted)
            {
                result.Add(value.ToString());
                value.Clear();
            }
            else value.Append(current);
        }
        result.Add(value.ToString());
        return result;
    }

    private static IEnumerable<Candidate> Load(string path, string seed)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        IEnumerable<CheckJob> jobs;
        if (extension == ".csv") jobs = MainForm.LoadCsvJobs(path);
        else if (extension == ".xlsx" || extension == ".xlsm")
        {
            int number = 0;
            jobs = OpenXmlExcelBridge.LoadPlans(path).SelectMany(plan =>
                plan.Sources.Where(source => !source.ManualOnly && !String.IsNullOrWhiteSpace(source.Url)).Select(source => new CheckJob
                {
                    Number = ++number,
                    Url = source.Url,
                    ExpectedTitle = source.ExpectedTitle ?? "",
                    ExpectedExcerpt = source.ExpectedExcerpt ?? "",
                    ExpectedAuthor = source.ExpectedAuthor ?? "",
                    Platform = source.Platform ?? "",
                    ContentType = String.IsNullOrWhiteSpace(source.ContentType)
                        ? Checker.InferContentType(source.Platform, source.Url, source.ExpectedTitle) : source.ContentType,
                    SourceSheet = plan.SheetName,
                    SourceRow = source.Row
                })).ToList();
        }
        else return Enumerable.Empty<CheckJob>().Select(job => (Candidate)null);

        return jobs.Where(job => job != null && !String.IsNullOrWhiteSpace(job.Url)).Select(job =>
        {
            string key = CanonicalUrl(job.Url);
            return new Candidate
            {
                Job = job,
                SourceFile = path,
                GroupKey = Group(job),
                UrlKey = key,
                Score = Hash(seed + "|" + path + "|" + key)
            };
        }).Where(item => !String.IsNullOrWhiteSpace(item.UrlKey));
    }

    private static string Group(CheckJob job)
    {
        string platform = Platform(job);
        if (String.IsNullOrWhiteSpace(platform) || platform == "\u7f51\u5a92" ||
            platform == "\u672a\u77e5" || platform == "\u672a\u77e5\u5e73\u53f0")
            return "domain:" + Host(job.Url);
        return "platform:" + platform;
    }

    private static string Platform(CheckJob job)
    {
        return (job == null ? "" : job.Platform ?? "").Trim();
    }

    private static string Host(string url)
    {
        Uri uri;
        return Uri.TryCreate(url ?? "", UriKind.Absolute, out uri) ? uri.Host.ToLowerInvariant() : "no-domain";
    }

    private static string CanonicalUrl(string value)
    {
        Uri uri;
        if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out uri)) return "";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";

        // Content identity is independent of HTTP/HTTPS and common sharing parameters.
        // This prevents a previously tested page from re-entering a later batch through
        // a cosmetically different supplier URL.
        string authority = uri.Host.ToLowerInvariant();
        if (!uri.IsDefaultPort) authority += ":" + uri.Port;
        string path = String.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        if (path.Length > 1) path = path.TrimEnd('/');

        string query = NormalizeQuery(uri.Query);
        return authority + path + (query.Length == 0 ? "" : "?" + query);
    }

    private static string NormalizeQuery(string query)
    {
        if (String.IsNullOrWhiteSpace(query)) return "";
        var retained = new List<string>();
        foreach (string pair in query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            string rawKey = separator < 0 ? pair : pair.Substring(0, separator);
            string key;
            try { key = Uri.UnescapeDataString(rawKey.Replace("+", " ")); }
            catch { key = rawKey; }
            if (IsTrackingParameter(key)) continue;
            retained.Add(pair);
        }
        retained.Sort(StringComparer.Ordinal);
        return String.Join("&", retained);
    }

    private static bool IsTrackingParameter(string key)
    {
        string normalized = (key ?? "").Trim().ToLowerInvariant();
        if (normalized.StartsWith("utm_", StringComparison.Ordinal)) return true;
        switch (normalized)
        {
            case "spm":
            case "scm":
            case "refer_flag":
            case "share_token":
            case "share_source":
            case "share_from":
            case "isappinstalled":
                return true;
            default:
                return false;
        }
    }

    private static string Hash(string value)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "");
    }

    private static void WriteOutput(string path, IList<Candidate> rows, string seed)
    {
        string directory = Path.GetDirectoryName(path);
        if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            writer.WriteLine(OutputHeader);
            int number = 0;
            foreach (Candidate item in rows) writer.WriteLine(Row(++number, item, seed, null));
        }
    }

    private static void AppendHistory(string path, IList<Candidate> rows, string seed)
    {
        string directory = Path.GetDirectoryName(path);
        if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        bool needsHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        using (var writer = new StreamWriter(path, true, new UTF8Encoding(true)))
        {
            if (needsHeader) writer.WriteLine(HistoryHeader);
            int number = 0;
            string sampledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (Candidate item in rows) writer.WriteLine(Row(++number, item, seed, sampledAt));
        }
    }

    private static string Row(int number, Candidate item, string seed, string sampledAt)
    {
        CheckJob job = item.Job;
        var fields = new List<string>
        {
            number.ToString(), Csv(job.Platform), Csv(job.ExpectedTitle), Csv(job.ExpectedExcerpt),
            Csv(job.ExpectedAuthor), Csv(job.Url), Csv(job.ContentType), Csv(Path.GetFileName(item.SourceFile)),
            Csv(job.SourceSheet), job.SourceRow.ToString(), Csv(seed)
        };
        if (sampledAt != null) fields.Add(Csv(sampledAt));
        return String.Join(",", fields);
    }

    private static bool SamePath(string left, string right)
    {
        return String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Csv(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }
}
