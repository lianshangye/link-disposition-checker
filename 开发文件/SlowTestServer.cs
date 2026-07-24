using System;
using System.Net;
using System.Text;
using System.Threading;

internal static class SlowTestServer
{
    public static void Main()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18766/");
        listener.Start();
        byte[] body = Encoding.UTF8.GetBytes("<html><head><title>Batch Demo</title></head><body>Batch Demo content</body></html>");
        while (true)
        {
            HttpListenerContext context;
            try { context = listener.GetContext(); } catch { break; }
            Thread.Sleep(80);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.Close();
        }
    }
}
