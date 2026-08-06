using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace LinkDispositionChecker
{
    internal sealed class AuditShard
    {
        public int Index { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public int Count { get; set; }
        public int ValidCount { get; set; }
        public int NumberOffset { get; set; }
        public string Status { get; set; }
        public string OutputPath { get; set; }
    }

    /// <summary>
    /// Plans resumable input shards without loading a multi-hundred-thousand-row
    /// file into memory.  Network checking remains in FastAuditRunner; this
    /// class only owns deterministic partitioning and manifest state.
    /// </summary>
    internal static class ShardedFastAudit
    {
        internal static List<AuditShard> Plan(string inputPath, string manifestPath, int rowsPerShard)
        {
            if (String.IsNullOrWhiteSpace(inputPath)) throw new ArgumentException("Input path is required.", "inputPath");
            if (rowsPerShard < 100) throw new ArgumentOutOfRangeException("rowsPerShard");
            string fullInput = Path.GetFullPath(inputPath);
            string fullManifest = Path.GetFullPath(manifestPath);
            if (!File.Exists(fullInput)) throw new FileNotFoundException("Input file not found.", fullInput);

            var shards = new List<AuditShard>();
            int record = 0;
            int shardIndex = 0;
            int start = 1;
            int count = 0;
            int validCount = 0;
            using (var parser = new TextFieldParser(fullInput, Encoding.UTF8, true))
            {
                parser.TextFieldType = FieldType.Delimited;
                parser.SetDelimiters(",");
                parser.HasFieldsEnclosedInQuotes = true;
                parser.TrimWhiteSpace = false;
                while (!parser.EndOfData)
                {
                    string[] fields = parser.ReadFields();
                    if (fields == null || fields.All(String.IsNullOrWhiteSpace)) continue;
                    record++;
                    count++;
                    if (record > 1 && IsValidUrlRow(fields)) validCount++;
                    if (count >= rowsPerShard)
                    {
                        shards.Add(CreateShard(shardIndex++, start, record, count, validCount, fullManifest));
                        start = record + 1;
                        count = 0;
                        validCount = 0;
                    }
                }
            }
            if (count > 0 || shards.Count == 0)
                shards.Add(CreateShard(shardIndex, start, record, count, validCount, fullManifest));
            return shards;
        }

        internal static bool CanResume(AuditShard shard, string outputPath)
        {
            return shard != null && String.Equals(shard.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrWhiteSpace(outputPath) && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }

        internal static IEnumerable<AuditShard> Pending(IEnumerable<AuditShard> shards)
        {
            return (shards ?? Enumerable.Empty<AuditShard>()).Where(shard => !CanResume(shard, shard.OutputPath));
        }

        private static bool IsValidUrlRow(IEnumerable<string> fields)
        {
            foreach (string field in fields ?? Enumerable.Empty<string>())
            {
                Uri uri;
                if (Uri.TryCreate(field.Trim().Trim('"'), UriKind.Absolute, out uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return true;
            }
            return false;
        }

        private static AuditShard CreateShard(int index, int start, int end, int count, int validCount, string manifest)
        {
            string folder = Path.GetDirectoryName(manifest) ?? AppDomain.CurrentDomain.BaseDirectory;
            string stem = Path.GetFileNameWithoutExtension(manifest);
            return new AuditShard
            {
                Index = index,
                StartLine = start,
                EndLine = end,
                Count = count,
                ValidCount = validCount,
                Status = "pending",
                OutputPath = Path.Combine(folder, stem + ".part-" + index.ToString("D5") + ".csv")
            };
        }
    }
}

