using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace LinkDispositionChecker
{
    internal sealed class AuditCheckpointMetadata
    {
        public int FormatVersion { get; set; }
        public string InputSha256 { get; set; }
        public string CreatedAt { get; set; }
    }

    internal sealed class AuditCheckpointStore : IDisposable
    {
        private readonly bool _enabled;
        private readonly object _sync = new object();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        private StreamWriter _writer;

        public string CheckpointPath { get; private set; }
        public string MetadataPath { get; private set; }

        public AuditCheckpointStore(string outputPath, string inputSha256, bool enabled)
        {
            if (String.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output path is required.", "outputPath");
            if (String.IsNullOrWhiteSpace(inputSha256)) throw new ArgumentException("Input hash is required.", "inputSha256");

            _enabled = enabled;
            CheckpointPath = Path.GetFullPath(outputPath) + ".checkpoint.jsonl";
            MetadataPath = Path.GetFullPath(outputPath) + ".checkpoint.json";
            if (!_enabled)
            {
                DeleteIfPresent(CheckpointPath);
                DeleteIfPresent(MetadataPath);
                return;
            }

            PrepareMetadata(inputSha256);
        }

        public static string ComputeInputSha256(string path)
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha256 = SHA256.Create())
                return String.Concat(sha256.ComputeHash(stream).Select(value => value.ToString("X2")));
        }

        public Dictionary<int, CheckResult> Load(IEnumerable<CheckJob> jobs, Action<string> warning)
        {
            var recovered = new Dictionary<int, CheckResult>();
            if (!_enabled || !File.Exists(CheckpointPath)) return recovered;

            var jobsByNumber = jobs.GroupBy(job => job.Number).ToDictionary(group => group.Key, group => group.First());
            int malformed = 0;
            foreach (string line in File.ReadLines(CheckpointPath, Encoding.UTF8))
            {
                if (String.IsNullOrWhiteSpace(line)) continue;
                CheckResult result;
                try { result = _serializer.Deserialize<CheckResult>(line); }
                catch { malformed++; continue; }
                CheckJob job;
                if (result == null || !jobsByNumber.TryGetValue(result.Number, out job)) continue;
                if (!String.Equals((result.OriginalUrl ?? "").Trim(), (job.Url ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                recovered[result.Number] = result;
            }
            if (malformed > 0 && warning != null)
                warning("检查点忽略了 " + malformed + " 条不完整记录；其余已完成结果已恢复。");
            return recovered;
        }

        public void Append(CheckResult result)
        {
            if (!_enabled || result == null) return;
            string line = _serializer.Serialize(result);
            lock (_sync)
            {
                if (_writer == null)
                {
                    _writer = new StreamWriter(CheckpointPath, true, new UTF8Encoding(false));
                    _writer.AutoFlush = true;
                }
                _writer.WriteLine(line);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_writer == null) return;
                _writer.Dispose();
                _writer = null;
            }
        }

        private void PrepareMetadata(string inputSha256)
        {
            if (File.Exists(MetadataPath))
            {
                AuditCheckpointMetadata existing;
                try { existing = _serializer.Deserialize<AuditCheckpointMetadata>(File.ReadAllText(MetadataPath, Encoding.UTF8)); }
                catch (Exception ex) { throw new InvalidDataException("检查点元数据损坏，不能安全续跑。", ex); }
                if (existing == null || existing.FormatVersion != 1 ||
                    !String.Equals(existing.InputSha256, inputSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("检查点属于另一份输入数据，不能混用；请更换输出文件名后重新运行。");
                return;
            }
            if (File.Exists(CheckpointPath) && new FileInfo(CheckpointPath).Length > 0)
                throw new InvalidDataException("发现没有元数据的旧检查点，不能确认输入身份；请更换输出文件名后重新运行。");

            var metadata = new AuditCheckpointMetadata
            {
                FormatVersion = 1,
                InputSha256 = inputSha256,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            string temporary = MetadataPath + ".tmp";
            File.WriteAllText(temporary, _serializer.Serialize(metadata), new UTF8Encoding(false));
            File.Move(temporary, MetadataPath);
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
