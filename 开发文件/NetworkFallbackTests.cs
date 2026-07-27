using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LinkDispositionChecker;

internal static class NetworkFallbackTests
{
    public static int Main()
    {
        return RunAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        MethodInfo quality = typeof(Checker).GetMethod("ResponseQuality", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        bool responseSelectionPassed = quality != null &&
            (int)quality.Invoke(null, new object[] { new HttpResponseMessage(HttpStatusCode.Forbidden) }) >
            (int)quality.Invoke(null, new object[] { new HttpResponseMessage(HttpStatusCode.BadGateway) });
        Console.WriteLine((responseSelectionPassed ? "PASS" : "FAIL") + " later HTTP 403 outranks earlier proxy 502");

        const string url = "http://127.0.0.1:18767/original-http";
        var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18767/");
        listener.Start();
        Task server = Task.Run(async delegate
        {
            HttpListenerContext context = await listener.GetContextAsync();
            byte[] body = Encoding.UTF8.GetBytes("plain HTTP resource is available");
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, 0, body.Length);
            context.Response.Close();
        });

        try
        {
            var checker = new Checker();
            CheckResult result = await checker.CheckAsync(url, 1, "", "", false, CancellationToken.None);
            await server;
            bool passed = result.StatusCode == "200" && result.FinalUrl == url && result.Verdict == "仍可访问";
            Console.WriteLine((passed ? "PASS" : "FAIL") + " HTTP original route => " +
                result.StatusCode + " / " + result.FinalUrl + " / " + result.Verdict);
            return passed && responseSelectionPassed ? 0 : 1;
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }
}
