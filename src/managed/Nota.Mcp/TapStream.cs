// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// GET /audio/tap on the DAW's loopback server: the live master mix as a stream, for a local host program (Agent One) that
// shares it. A browser page cannot reach it: any request with an Origin header is refused, and so is any Host other than
// loopback (DNS rebinding). One consumer at a time, because the tap has one reader.
//
// Wire format: first a single JSON line {"format":"f32le","channels":2,"sampleRate":N,"capacityFrames":M}\n, then messages
//   u8 type, u32 little-endian payload length, payload
//   type 1: audio, payload = interleaved stereo float32 little-endian frames
//   type 2: drop notice, payload = u64 total frames dropped so far (sent when it changes): a gap in the audio
// The stream ends when the client disconnects; the tap is then switched off.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nota.Application;

namespace Nota.Mcp;

public static class TapStream
{
    private static int _busy;
    private const int ChunkFrames = 1024;
    /// <summary>A listener that has not accepted what was sent for this long is dropped, so a stalled or half-dead connection cannot
    /// hold the single listener slot (a closed connection is otherwise noticed only when TCP gives up). Settable for tests.</summary>
    public static int WriteTimeoutMs = 10_000;

    public static void Map(WebApplication app, IOutputTapSource tap)
    {
        app.MapGet("/audio/tap", async (HttpContext ctx) =>
        {
            var host = ctx.Request.Host.Host;
            if (ctx.Request.Headers.ContainsKey("Origin") || !(host == "127.0.0.1" || host == "localhost" || host == "[::1]" || host == "::1"))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("The audio tap is for local programs only.");
                return;
            }
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsync("The audio tap already has a listener.");
                return;
            }
            try
            {
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.Headers.CacheControl = "no-store";
                var header = JsonSerializer.Serialize(new { format = "f32le", channels = 2, sampleRate = tap.SampleRate, capacityFrames = 131072 }) + "\n";
                await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(header), ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                tap.EnableOutputTap(true);
                var frames = new float[ChunkFrames * 2];
                var bytes = new byte[5 + frames.Length * 4];
                ulong lastDropped = 0;
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    // One deadline for everything this iteration writes: a client that stops reading cannot stall us past it.
                    using var write = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    write.CancelAfter(WriteTimeoutMs);
                    var ct = write.Token;
                    int n = tap.ReadOutputTap(frames, ChunkFrames);
                    if (n > 0)
                    {
                        bytes[0] = 1;
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1, 4), (uint)(n * 8));
                        for (int i = 0; i < n * 2; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(5 + i * 4, 4), frames[i]);
                        await ctx.Response.Body.WriteAsync(bytes.AsMemory(0, 5 + n * 8), ct);
                    }
                    var dropped = tap.OutputTapDropped;
                    if (dropped != lastDropped)
                    {
                        lastDropped = dropped;
                        var notice = new byte[13]; notice[0] = 2;
                        BinaryPrimitives.WriteUInt32LittleEndian(notice.AsSpan(1, 4), 8);
                        BinaryPrimitives.WriteUInt64LittleEndian(notice.AsSpan(5, 8), dropped);
                        await ctx.Response.Body.WriteAsync(notice, ct);
                    }
                    if (n == 0) { await ctx.Response.Body.FlushAsync(ct); await Task.Delay(4, ct); }
                }
            }
            catch (OperationCanceledException) { /* the listener went away, or stopped reading for WriteTimeoutMs: either way it is dropped */ }
            finally
            {
                try { tap.EnableOutputTap(false); } catch (Exception) { }
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }
}
