using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LinkDispositionChecker
{
    internal sealed partial class Checker
    {
        private const string GlobalpingMeasurementsEndpoint = "https://api.globalping.io/v1/measurements";
        private static readonly SemaphoreSlim ChinaEyeballProbeGate = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentDictionary<string, Task<ChinaEyeballSession>> ChinaEyeballSessions =
            new ConcurrentDictionary<string, Task<ChinaEyeballSession>>(StringComparer.OrdinalIgnoreCase);
        private static readonly JavaScriptSerializer GlobalpingJson =
            new JavaScriptSerializer { MaxJsonLength = 1200000 };
        private static readonly object GlobalpingAvailabilitySync = new object();
        private static DateTime _globalpingUnavailableUntilUtc = DateTime.MinValue;
        private static string _globalpingUnavailableCredentialMode = "";

        private sealed class ChinaEyeballSession
        {
            public string Host;
            public string SeedMeasurementId;
            public string CookieHeader;
            public string ProbeLabel;
            public DateTime ExpiresUtc;
            public string InitialTarget;
            public GlobalpingHttpResult InitialResult;
        }

        private sealed class GlobalpingHttpResult
        {
            public string MeasurementId;
            public string Status;
            public int StatusCode;
            public string Body;
            public string RawOutput;
            public string CookieHeader;
            public string ProbeLabel;
            public string Error;
        }

        internal static bool ShouldTryChinaEyeballEvidence(CheckResult result, Uri target)
        {
            if (result == null || target == null || !NetworkRestrictionCircuitBreaker.IsTransientRestriction(result))
                return false;
            string infrastructure = (result.InfrastructureKey ?? "").Trim();
            if (String.Equals(infrastructure, "IP 119.28.42.49", StringComparison.OrdinalIgnoreCase))
                return true;
            string platform = (result.Platform ?? "").Trim();
            bool genericPlatform = String.IsNullOrWhiteSpace(platform) ||
                platform == "网媒" || platform == "未知" || platform == "未知平台";
            if (!genericPlatform) return false;
            string evidence = (result.Evidence ?? "") + " " + (result.StatusCode ?? "");
            return Regex.IsMatch(evidence, @"(?:\b502\b|bad\s+gateway|http\s+error\s+502)",
                RegexOptions.IgnoreCase);
        }

        internal static bool IsChinaEyeballChallenge(int statusCode, string body, string cookieHeader)
        {
            if (statusCode != 403 && statusCode != 444) return false;
            if (String.IsNullOrWhiteSpace(cookieHeader)) return false;
            string text = body ?? "";
            return text.IndexOf("window.location.href", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("网站防火墙", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("server_name_session", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<RemoteEvidenceResponse> TryChinaEyeballEvidenceAsync(Uri target,
            CancellationToken token)
        {
            if (target == null) return new RemoteEvidenceResponse { Error = "目标地址为空" };
            await ChinaEyeballProbeGate.WaitAsync(token);
            try
            {
                ChinaEyeballSession session = null;
                Task<ChinaEyeballSession> existing;
                if (ChinaEyeballSessions.TryGetValue(target.Host, out existing))
                {
                    try { session = await existing; }
                    catch { ChinaEyeballSessions.TryRemove(target.Host, out existing); }
                    if (session != null && session.ExpiresUtc <= DateTime.UtcNow)
                    {
                        ChinaEyeballSessions.TryRemove(target.Host, out existing);
                        session = null;
                    }
                }

                if (session == null)
                {
                    Task<ChinaEyeballSession> created = CreateChinaEyeballSessionAsync(target, token);
                    existing = ChinaEyeballSessions.GetOrAdd(target.Host, created);
                    try { session = await existing; }
                    catch
                    {
                        ChinaEyeballSessions.TryRemove(target.Host, out existing);
                        throw;
                    }
                }

                if (session == null || String.IsNullOrWhiteSpace(session.CookieHeader) ||
                    String.IsNullOrWhiteSpace(session.SeedMeasurementId))
                {
                    if (session != null && session.InitialResult != null &&
                        String.Equals(session.InitialTarget, target.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                    {
                        ChinaEyeballSessions.TryRemove(target.Host, out existing);
                        return ToRemoteEvidence(session.InitialResult, target,
                            BuildGlobalpingSource(session.ProbeLabel,
                                session.SeedMeasurementId, ""));
                    }
                    return new RemoteEvidenceResponse
                    {
                        Error = "中国普通宽带探针未取得可复用的防火墙会话",
                        Source = "Globalping 中国普通宽带公开探针"
                    };
                }

                GlobalpingHttpResult proof = await RunGlobalpingHttpAsync(target,
                    session.SeedMeasurementId, session.CookieHeader, token);
                string source = BuildGlobalpingSource(session.ProbeLabel,
                    session.SeedMeasurementId, proof == null ? "" : proof.MeasurementId);
                if (proof == null)
                    return new RemoteEvidenceResponse { Error = "中国普通宽带正文测量没有返回结果", Source = source };
                if (proof.StatusCode > 0 && proof.StatusCode != 403 && proof.StatusCode != 444)
                    return ToRemoteEvidence(proof, target, source);
                if (proof.StatusCode <= 0)
                    return ToRemoteEvidence(proof, target, source);

                // Some firewalls rotate the challenge cookie after the first retry.
                // Apply the newest cookie immediately and keep using the same probe.
                string refreshedCookie = MergeCookieHeaders(session.CookieHeader, proof.CookieHeader);
                if (!String.IsNullOrWhiteSpace(refreshedCookie) &&
                    !String.Equals(refreshedCookie, session.CookieHeader, StringComparison.Ordinal))
                {
                    GlobalpingHttpResult refreshedProof = await RunGlobalpingHttpAsync(target,
                        proof.MeasurementId, refreshedCookie, token);
                    string refreshedIds = proof.MeasurementId +
                        (refreshedProof == null || String.IsNullOrWhiteSpace(refreshedProof.MeasurementId)
                            ? "" : "/" + refreshedProof.MeasurementId);
                    source = BuildGlobalpingSource(session.ProbeLabel,
                        session.SeedMeasurementId, refreshedIds);
                    if (refreshedProof == null)
                        return new RemoteEvidenceResponse
                        {
                            Error = "更新防火墙 Cookie 后未返回测量结果",
                            Source = source
                        };
                    proof = refreshedProof;
                    session.CookieHeader = refreshedCookie;
                    if (proof.StatusCode > 0 && proof.StatusCode != 403 && proof.StatusCode != 444)
                        return ToRemoteEvidence(proof, target, source);
                    if (proof.StatusCode <= 0)
                        return ToRemoteEvidence(proof, target, source);
                }

                // The cached session may belong to another path. Do not make the user
                // start a second run: establish a fresh challenge on this exact URL now.
                ChinaEyeballSessions.TryRemove(target.Host, out existing);
                ChinaEyeballSession freshSession = await CreateChinaEyeballSessionAsync(target, token);
                if (freshSession != null && freshSession.InitialResult != null &&
                    String.Equals(freshSession.InitialTarget, target.AbsoluteUri,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return ToRemoteEvidence(freshSession.InitialResult, target,
                        BuildGlobalpingSource(freshSession.ProbeLabel,
                            freshSession.SeedMeasurementId, ""));
                }
                if (freshSession != null && !String.IsNullOrWhiteSpace(freshSession.CookieHeader))
                {
                    GlobalpingHttpResult freshProof = await RunGlobalpingHttpAsync(target,
                        freshSession.SeedMeasurementId, freshSession.CookieHeader, token);
                    string freshSource = BuildGlobalpingSource(freshSession.ProbeLabel,
                        freshSession.SeedMeasurementId,
                        freshProof == null ? "" : freshProof.MeasurementId);
                    if (freshProof != null &&
                        (freshProof.StatusCode <= 0 ||
                         (freshProof.StatusCode != 403 && freshProof.StatusCode != 444)))
                    {
                        if (freshProof.StatusCode >= 200 && freshProof.StatusCode < 400)
                            ChinaEyeballSessions[target.Host] = Task.FromResult(freshSession);
                        return ToRemoteEvidence(freshProof, target, freshSource);
                    }
                    proof = freshProof ?? proof;
                    source = freshSource;
                }
                ChinaEyeballSessions.TryRemove(target.Host, out existing);
                return new RemoteEvidenceResponse
                {
                    Error = !String.IsNullOrWhiteSpace(proof.Error) ? proof.Error :
                        "刷新 Cookie 并为当前链接重建会话后仍返回 HTTP " + proof.StatusCode,
                    Source = source,
                    TargetUnreachable = false
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new RemoteEvidenceResponse
                {
                    Error = "中国普通宽带公开探针调用失败：" + FriendlyError(ex),
                    Source = "Globalping 中国普通宽带公开探针",
                    TargetUnreachable = false
                };
            }
            finally { ChinaEyeballProbeGate.Release(); }
        }

        private async Task<ChinaEyeballSession> CreateChinaEyeballSessionAsync(Uri target,
            CancellationToken token)
        {
            GlobalpingHttpResult seed = await RunGlobalpingHttpAsync(target, "China+eyeball", "", token);
            if (seed == null) return null;
            if (((seed.StatusCode >= 200 && seed.StatusCode < 300) ||
                 seed.StatusCode == 404 || seed.StatusCode == 410) &&
                (seed.StatusCode == 404 || seed.StatusCode == 410 || !String.IsNullOrWhiteSpace(seed.Body)))
            {
                // A normal first response or explicit not-found response is already
                // usable evidence and needs no challenge retry.
                return new ChinaEyeballSession
                {
                    Host = target.Host,
                    SeedMeasurementId = seed.MeasurementId,
                    CookieHeader = "",
                    ProbeLabel = seed.ProbeLabel,
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(2),
                    InitialTarget = target.AbsoluteUri,
                    InitialResult = seed
                };
            }
            if (!IsChinaEyeballChallenge(seed.StatusCode, seed.Body, seed.CookieHeader))
                throw new InvalidOperationException("探针首访 HTTP " + seed.StatusCode +
                    "，未识别为可继续的防火墙挑战" +
                    (String.IsNullOrWhiteSpace(seed.CookieHeader) ? "（未取得 Cookie）" : "（已取得 Cookie）") +
                    (String.IsNullOrWhiteSpace(seed.Error) ? "" : "：" + seed.Error));
            return new ChinaEyeballSession
            {
                Host = target.Host,
                SeedMeasurementId = seed.MeasurementId,
                CookieHeader = seed.CookieHeader,
                ProbeLabel = seed.ProbeLabel,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(30)
            };
        }

        private async Task<GlobalpingHttpResult> RunGlobalpingHttpAsync(Uri target,
            string locationMagic, string cookieHeader, CancellationToken token)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "User-Agent", "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/138.0 Mobile Safari/537.36" },
                { "Accept-Language", "zh-CN,zh;q=0.9" }
            };
            if (!String.IsNullOrWhiteSpace(cookieHeader))
            {
                headers["Cookie"] = cookieHeader;
                headers["Referer"] = target.GetLeftPart(UriPartial.Authority) + "/";
            }
            var request = new Dictionary<string, object>
            {
                { "method", "GET" },
                { "path", String.IsNullOrWhiteSpace(target.AbsolutePath) ? "/" : target.AbsolutePath },
                { "headers", headers }
            };
            string query = (target.Query ?? "").TrimStart('?');
            if (!String.IsNullOrWhiteSpace(query)) request["query"] = query;
            var payload = new Dictionary<string, object>
            {
                { "type", "http" },
                { "target", target.Host },
                { "measurementOptions", new Dictionary<string, object>
                    {
                        { "protocol", String.Equals(target.Scheme, Uri.UriSchemeHttps,
                            StringComparison.OrdinalIgnoreCase) ? "HTTPS" : "HTTP" },
                        { "ipVersion", 4 },
                        { "request", request }
                    }
                },
                { "locations", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "magic", locationMagic },
                            { "limit", 1 }
                        }
                    }
                },
                { "inProgressUpdates", false }
            };

            string measurementId = await CreateGlobalpingMeasurementAsync(payload, token);
            if (String.IsNullOrWhiteSpace(measurementId))
                return new GlobalpingHttpResult { Error = "公开探针未返回测量编号" };
            return await PollGlobalpingMeasurementAsync(measurementId, token);
        }

        private async Task<string> CreateGlobalpingMeasurementAsync(
            Dictionary<string, object> payload, CancellationToken token)
        {
            string credentialMode = String.IsNullOrWhiteSpace(GetGlobalpingToken()) ? "anonymous" : "token";
            lock (GlobalpingAvailabilitySync)
            {
                if (_globalpingUnavailableUntilUtc > DateTime.UtcNow &&
                    String.Equals(_globalpingUnavailableCredentialMode, credentialMode,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Globalping 当前达到服务额度或被限流，本次运行暂不再请求；" +
                        "可配置 GLOBALPING_API_TOKEN 后点击“继续未完成”");
                if (!String.Equals(_globalpingUnavailableCredentialMode, credentialMode,
                    StringComparison.OrdinalIgnoreCase))
                    _globalpingUnavailableUntilUtc = DateTime.MinValue;
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, GlobalpingMeasurementsEndpoint))
            {
                request.Content = new StringContent(GlobalpingJson.Serialize(payload), Encoding.UTF8, "application/json");
                AddGlobalpingAuthorization(request);
                using (HttpResponseMessage response = await _remoteEvidenceClient.SendAsync(request, token))
                {
                    string body = await ReadLimitedBodyAsync(response.Content, 200000, token);
                    if ((int)response.StatusCode == 429)
                    {
                        lock (GlobalpingAvailabilitySync)
                        {
                            _globalpingUnavailableUntilUtc = DateTime.UtcNow.AddHours(1);
                            _globalpingUnavailableCredentialMode = credentialMode;
                        }
                    }
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException("Globalping 返回 HTTP " + (int)response.StatusCode +
                            "：" + ExecutionLogWriter.Safe(body, 240));
                    Dictionary<string, object> json = AsDictionary(GlobalpingJson.DeserializeObject(body));
                    return GetString(json, "id");
                }
            }
        }

        private async Task<GlobalpingHttpResult> PollGlobalpingMeasurementAsync(
            string measurementId, CancellationToken token)
        {
            Dictionary<string, object> measurement = null;
            for (int attempt = 0; attempt < 22; attempt++)
            {
                await Task.Delay(800, token);
                using (var request = new HttpRequestMessage(HttpMethod.Get,
                    GlobalpingMeasurementsEndpoint + "/" + Uri.EscapeDataString(measurementId)))
                {
                    AddGlobalpingAuthorization(request);
                    using (HttpResponseMessage response = await _remoteEvidenceClient.SendAsync(request, token))
                    {
                        string body = await ReadLimitedBodyAsync(response.Content, 1200000, token);
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException("Globalping 查询返回 HTTP " + (int)response.StatusCode);
                        measurement = AsDictionary(GlobalpingJson.DeserializeObject(body));
                    }
                }
                if (!String.Equals(GetString(measurement, "status"), "in-progress",
                    StringComparison.OrdinalIgnoreCase)) break;
            }

            if (measurement == null)
                return new GlobalpingHttpResult { MeasurementId = measurementId, Error = "公开探针测量没有响应" };
            IList results = GetList(measurement, "results");
            if (results == null || results.Count == 0)
                return new GlobalpingHttpResult
                {
                    MeasurementId = measurementId,
                    Error = "公开探针测量未分配到中国普通宽带探针"
                };
            Dictionary<string, object> first = AsDictionary(results[0]);
            Dictionary<string, object> probe = GetDictionary(first, "probe");
            Dictionary<string, object> result = GetDictionary(first, "result");
            string city = GetString(probe, "city");
            string country = GetString(probe, "country");
            string network = GetString(probe, "network");
            string probeLabel = String.Join("/", new[] { country, city, network }
                .Where(item => !String.IsNullOrWhiteSpace(item)));
            return new GlobalpingHttpResult
            {
                MeasurementId = measurementId,
                Status = GetString(result, "status"),
                StatusCode = GetInt(result, "statusCode"),
                Body = GetString(result, "rawBody"),
                RawOutput = GetString(result, "rawOutput"),
                CookieHeader = BuildCookieHeader(GetDictionary(result, "headers")),
                ProbeLabel = probeLabel,
                Error = String.Equals(GetString(result, "status"), "failed", StringComparison.OrdinalIgnoreCase)
                    ? ExecutionLogWriter.Safe(GetString(result, "rawOutput"), 300) : ""
            };
        }

        private static RemoteEvidenceResponse ToRemoteEvidence(GlobalpingHttpResult result,
            Uri target, string source)
        {
            return new RemoteEvidenceResponse
            {
                Status = result.StatusCode,
                FinalUrl = target.AbsoluteUri,
                Html = result.Body ?? "",
                Text = result.Body ?? "",
                Source = source,
                Error = result.StatusCode <= 0 ? result.Error : "",
                TargetUnreachable = false
            };
        }

        private static string BuildGlobalpingSource(string probeLabel, string seedId, string proofId)
        {
            string label = String.IsNullOrWhiteSpace(probeLabel) ? "中国普通宽带" : probeLabel;
            string ids = String.IsNullOrWhiteSpace(proofId) ? seedId : seedId + "/" + proofId;
            return "Globalping 中国普通宽带公开探针（" + label + "；测量 " + ids + "）";
        }

        private static void AddGlobalpingAuthorization(HttpRequestMessage request)
        {
            string token = GetGlobalpingToken();
            if (!String.IsNullOrWhiteSpace(token))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        private static string GetGlobalpingToken()
        {
            string token = Environment.GetEnvironmentVariable("GLOBALPING_API_TOKEN") ?? "";
            if (String.IsNullOrWhiteSpace(token))
                token = Environment.GetEnvironmentVariable(
                    "GLOBALPING_API_TOKEN", EnvironmentVariableTarget.User) ?? "";
            return token.Trim();
        }

        private static string BuildCookieHeader(Dictionary<string, object> headers)
        {
            if (headers == null) return "";
            object raw;
            if (!headers.TryGetValue("set-cookie", out raw) || raw == null)
            {
                KeyValuePair<string, object> pair = headers.FirstOrDefault(item =>
                    String.Equals(item.Key, "set-cookie", StringComparison.OrdinalIgnoreCase));
                raw = pair.Value;
            }
            var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in FlattenStrings(raw))
            {
                string first = (value ?? "").Split(';')[0].Trim();
                int separator = first.IndexOf('=');
                if (separator <= 0) continue;
                cookies[first.Substring(0, separator).Trim()] = first.Substring(separator + 1).Trim();
            }
            return String.Join("; ", cookies.Select(item => item.Key + "=" + item.Value));
        }

        internal static string MergeCookieHeaders(params string[] headers)
        {
            var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string header in headers ?? new string[0])
            {
                foreach (string part in (header ?? "").Split(';'))
                {
                    string value = part.Trim();
                    int separator = value.IndexOf('=');
                    if (separator <= 0) continue;
                    string name = value.Substring(0, separator).Trim();
                    if (name.Length == 0) continue;
                    cookies[name] = value.Substring(separator + 1).Trim();
                }
            }
            return String.Join("; ", cookies.Select(item => item.Key + "=" + item.Value));
        }

        private static IEnumerable<string> FlattenStrings(object value)
        {
            if (value == null) yield break;
            string text = value as string;
            if (text != null) { yield return text; yield break; }
            IEnumerable values = value as IEnumerable;
            if (values == null) yield break;
            foreach (object item in values)
            {
                string itemText = Convert.ToString(item);
                if (!String.IsNullOrWhiteSpace(itemText)) yield return itemText;
            }
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            IDictionary<string, object> dictionary = value as IDictionary<string, object>;
            return dictionary == null ? null :
                new Dictionary<string, object>(dictionary, StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? AsDictionary(value) : null;
        }

        private static IList GetList(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value as IList : null;
        }

        private static string GetString(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value) : "";
        }

        private static int GetInt(Dictionary<string, object> source, string key)
        {
            int parsed;
            return Int32.TryParse(GetString(source, key), out parsed) ? parsed : 0;
        }
    }
}
