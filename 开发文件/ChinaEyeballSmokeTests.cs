using System;
using System.Collections.Generic;
using System.Threading;
using LinkDispositionChecker;

internal static class ChinaEyeballSmokeTests
{
    public static int Main(string[] args)
    {
        if (args.Length < 1) return 2;
        var checker = new Checker();
        var result = new CheckResult
        {
            Number = 1,
            Verdict = "暂时异常",
            StatusCode = "502",
            OriginalUrl = args[0],
            FinalUrl = args[0],
            Evidence = "HTTP 502",
            Platform = "网媒",
            ContentType = "文章",
            ExpectedTitle = args.Length > 1 ? args[1] : "",
            InfrastructureKey = "IP 119.28.42.49",
            EvidenceTrail = new List<VerificationEvidence>()
        };
        result = checker.EscalateEvidenceAsync(result, CancellationToken.None)
            .GetAwaiter().GetResult();
        Console.WriteLine("VERDICT=" + result.Verdict);
        Console.WriteLine("STATUS=" + result.StatusCode);
        Console.WriteLine("TITLE=" + result.Title);
        Console.WriteLine("STAGE=" + result.EvidenceStage);
        Console.WriteLine("ATTEMPTS=" + result.AcquisitionAttempts);
        Console.WriteLine("EVIDENCE=" + result.Evidence);
        foreach (VerificationEvidence evidence in result.EvidenceTrail ?? new List<VerificationEvidence>())
            Console.WriteLine("TRAIL=" + evidence.Source + "|" + evidence.Message);
        return result.Verdict == "仍可访问" ? 0 : 1;
    }
}
