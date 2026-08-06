using System;
using System.IO;
using System.Linq;

namespace LinkDispositionChecker
{
    internal static class ShardedFastAuditTests
    {
        public static void Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "link-checker-shard-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string input = Path.Combine(root, "input.csv");
                string manifest = Path.Combine(root, "audit.manifest.json");
                File.WriteAllLines(input, Enumerable.Range(1, 2501).Select(i => i == 1
                    ? "url,title,excerpt"
                    : (i == 500
                        ? "\"https://example.test/" + i + "\",\"Title, with comma\",\"line one\r\nline two\""
                        : "https://example.test/" + i + ",Title,Excerpt")));
                var shards = ShardedFastAudit.Plan(input, manifest, 500);
                bool passed = shards.Count == 6 && shards.Sum(s => s.Count) == 2501 &&
                    shards.Sum(s => s.ValidCount) == 2500 &&
                    shards.First().StartLine == 1 && shards.Last().EndLine == 2501 &&
                    !ShardedFastAudit.CanResume(shards.First(), shards.First().OutputPath);
                Console.WriteLine(passed ? "PASS sharded plan" : "FAIL sharded plan");
                if (!passed) Environment.ExitCode = 1;
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
