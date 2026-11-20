// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// GET /audio/tap against the real engine and the real loopback server (AGENT DAW Listen Together). Alone: `--tap-stream`.

using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using Nota.Infrastructure;
using Nota.Mcp;

namespace Nota.SmokeTest;

internal static class TapStreamTests
{
    public static IEnumerable<(bool ok, string label)> Run()
    {
        var results = new List<(bool, string)>();
        string wav = Path.Combine(Path.GetTempPath(), "nota_tapstream_330.wav");
        WavWriter.WriteSine(wav, seconds: 6.0, freq: 330, sampleRate: 44100);
        using var e = new NotaEngine();
        bool started = true;
        try { e.Start(); } catch (NotaEngineException) { started = false; }
        if (!started || !e.IsRunning) { Console.WriteLine("  [SKIP] tap stream: no audio output device could be opened here"); yield break; }
        int t = e.AddAudioTrack(); e.AddAudioClip(t, wav, 0.0);

        int port = 21000 + Random.Shared.Next(2000);
        var server = new NotaMcpServer();
        server.StartAsync(e, new SyncDispatch(), new NoRefresh(), port).GetAwaiter().GetResult();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        string url = $"http://127.0.0.1:{port}/audio/tap";
        try
        {
            // refusals first: a web page, a rebinding host
            var withOrigin = new HttpRequestMessage(HttpMethod.Get, url); withOrigin.Headers.Add("Origin", "https://evil.example");
            var viaOrigin = http.SendAsync(withOrigin).GetAwaiter().GetResult();
            var badHost = new HttpRequestMessage(HttpMethod.Get, url); badHost.Headers.Host = "evil.example";
            var viaHost = http.SendAsync(badHost).GetAwaiter().GetResult();
            results.Add((viaOrigin.StatusCode == HttpStatusCode.Forbidden && viaHost.StatusCode == HttpStatusCode.Forbidden, "a request with an Origin header, or for another Host, is refused (a web page cannot listen in)"));

            // the stream itself
            using var cts = new CancellationTokenSource();
            var resp = http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
            using var body = resp.Content.ReadAsStream(cts.Token);
            var headerBytes = new List<byte>(); int b;
            while ((b = body.ReadByte()) >= 0 && b != '\n') headerBytes.Add((byte)b);
            var header = JsonDocument.Parse(headerBytes.ToArray()).RootElement;
            double sr = header.GetProperty("sampleRate").GetDouble();
            results.Add((resp.StatusCode == HttpStatusCode.OK && header.GetProperty("format").GetString() == "f32le" && header.GetProperty("channels").GetInt32() == 2 && Math.Abs(sr - e.SampleRate) < 1,
                $"the stream announces its format (f32le, 2 channels, {sr} Hz)"));

            // a second listener is refused while the first is connected
            var second = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            results.Add((second.StatusCode == HttpStatusCode.Conflict, "a second listener is refused while one is connected"));
            second.Dispose();

            e.Seek(0); e.Play();
            var left = new List<float>(); ulong droppedNotice = 0; int audioMsgs = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var head = new byte[5];
            while (sw.ElapsedMilliseconds < 1800)
            {
                if (!ReadFull(body, head, 5, cts.Token)) break;
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1, 4));
                var payload = new byte[len];
                if (!ReadFull(body, payload, len, cts.Token)) break;
                if (head[0] == 1) { audioMsgs++; for (int i = 0; i + 7 < len; i += 8) left.Add(BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(i, 4))); }
                else if (head[0] == 2) droppedNotice = BinaryPrimitives.ReadUInt64LittleEndian(payload);
            }
            e.StopTransport();

            int first = left.FindIndex(v => Math.Abs(v) > 0.01f);
            var seg = first >= 0 ? left.Skip(first).ToList() : new List<float>();
            var rising = new List<int>(); for (int i = 1; i < seg.Count; i++) if (seg[i - 1] < 0 && seg[i] >= 0) rising.Add(i);
            double hz = rising.Count > 2 ? (rising.Count - 1) / ((rising[^1] - rising[0]) / sr) : 0;
            int maxGap = 0; for (int i = 1; i < rising.Count; i++) maxGap = Math.Max(maxGap, rising[i] - rising[i - 1]);
            results.Add((audioMsgs > 10 && left.Count > sr * 0.5, $"audio arrives over HTTP: {left.Count} frames ({left.Count / sr:F2} s) in {audioMsgs} messages"));
            results.Add((Math.Abs(hz - 330) < 1.0, $"it is the tone that was played: {hz:F2} Hz (expected 330)"));
            results.Add((rising.Count > 100 && maxGap <= sr / 330.0 * 1.5, $"continuous: longest gap between crossings {maxGap} frames"));
            results.Add((droppedNotice == 0, "a listener that keeps up gets no drop notices"));
            cts.Cancel();
            body.Dispose();      // the connection only closes once the response stream is disposed, not merely cancelled
            resp.Dispose();

            // after the listener leaves, the tap is released and a new listener is accepted
            HttpStatusCode last = 0; var waited = System.Diagnostics.Stopwatch.StartNew();
            while (waited.ElapsedMilliseconds < 5000)
            {
                using var again = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                last = again.StatusCode;
                if (last == HttpStatusCode.OK) break;
                Thread.Sleep(100);
            }
            results.Add((last == HttpStatusCode.OK, "once the listener disconnects (within 5 s: the server notices a closed connection late), a new one is accepted"));
        }
        finally { server.StopAsync().GetAwaiter().GetResult(); }

        // ---- a listener that connects and then stops reading must not hold the slot forever ------------------------------
        int port2 = 23000 + Random.Shared.Next(2000);
        var server2 = new NotaMcpServer();
        int savedTimeout = TapStream.WriteTimeoutMs;
        TapStream.WriteTimeoutMs = 1000;
        server2.StartAsync(e, new SyncDispatch(), new NoRefresh(), port2).GetAwaiter().GetResult();
        try
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, ct) =>
                {
                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { ReceiveBufferSize = 1024, NoDelay = true };
                    await socket.ConnectAsync(ctx.DnsEndPoint, ct);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                },
            };
            using var stalled = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            string url2 = $"http://127.0.0.1:{port2}/audio/tap";
            e.Seek(0); e.Play();
            using var first = stalled.GetAsync(url2, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();   // connected, headers read, and now never read again
            using var other = new HttpClient();
            var waited = System.Diagnostics.Stopwatch.StartNew();
            HttpStatusCode second = 0;
            while (waited.ElapsedMilliseconds < 25_000)
            {
                using var again = other.GetAsync(url2, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                second = again.StatusCode;
                if (second == HttpStatusCode.OK) break;
                Thread.Sleep(250);
            }
            e.StopTransport();
            results.Add((first.StatusCode == HttpStatusCode.OK && second == HttpStatusCode.OK, $"a listener that stops reading is dropped after the write timeout and the slot is freed (took {waited.ElapsedMilliseconds / 1000.0:F1} s)"));
        }
        finally { TapStream.WriteTimeoutMs = savedTimeout; server2.StopAsync().GetAwaiter().GetResult(); }
        foreach (var r in results) yield return r;
    }

    private static bool ReadFull(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int got = 0;
        while (got < count) { int n = s.Read(buf, got, count - got); if (n <= 0) return false; got += n; }
        return true;
    }
}
