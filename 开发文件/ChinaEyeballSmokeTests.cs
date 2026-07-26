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
        var urls = new List<string>();
        foreach (string argument in args)
            if (Uri.IsWellFormedUriString(argument, UriKind.Absolute)) urls.Add(argument);
        if (urls.Count == 0) return 2;
        int failures = 0;
        int number = 0;
        foreach (string url in urls)
        {
            number++;
            var result = new CheckResult
            {
                Number = number,
                Verdict = "暂时异常",
                StatusCode = "502",
                OriginalUrl = url,
                FinalUrl = url,
                Evidence = "HTTP 502",
                Platform = "网媒",
                ContentType = "文章",
                ExpectedTitle = "",
                InfrastructureKey = "IP 119.28.42.49",
                EvidenceTrail = new List<VerificationEvidence>()
            };
            result = checker.EscalateEvidenceAsync(result, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine("URL=" + url);
            Console.WriteLine("VERDICT=" + result.Verdict);
            Console.WriteLine("STATUS=" + result.StatusCode);
            Console.WriteLine("TITLE=" + result.Title);
            Console.WriteLine("STAGE=" + result.EvidenceStage);
            Console.WriteLine("ATTEMPTS=" + result.AcquisitionAttempts);
            Console.WriteLine("EVIDENCE=" + result.Evidence);
            foreach (VerificationEvidence evidence in result.EvidenceTrail ?? new List<VerificationEvidence>())
                Console.WriteLine("TRAIL=" + evidence.Source + "|" + evidence.Message);
            if (result.Verdict != "仍可访问") failures++;
        }
        return failures == 0 ? 0 : 1;
    }
}
