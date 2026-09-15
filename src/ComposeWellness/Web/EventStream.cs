using System.Text.Json;
using ComposeWellness.Models;
using ComposeWellness.Services;

namespace ComposeWellness.Web;

/// <summary>
/// Server-Sent Events endpoint. Sends the current status, replays retained log lines the client
/// has not seen yet, then streams live events until the browser disconnects.
/// </summary>
public static class EventStream
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    public static async Task WriteAsync(HttpContext context, UpdateCoordinator coordinator, long? after, JsonSerializerOptions json)
    {
        var response = context.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Tells reverse proxies such as nginx not to buffer the stream.
        response.Headers["X-Accel-Buffering"] = "no";

        var cancellationToken = context.RequestAborted;

        // Browsers resend the id of the last event they received when they reconnect automatically,
        // so the header wins over the query parameter used for the very first connection.
        var afterSequence = ParseLastEventId(context) ?? after ?? 0;

        using var subscription = coordinator.Subscribe(afterSequence);

        try
        {
            await WriteEventAsync(response, "status", subscription.Status, null, json, cancellationToken);
            foreach (var entry in subscription.Replay)
            {
                await WriteEventAsync(response, "log", entry, entry.Sequence, json, cancellationToken);
            }

            await response.Body.FlushAsync(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await WaitForEventOrKeepAliveAsync(subscription, response, cancellationToken))
                {
                    break;
                }

                while (subscription.Reader.TryRead(out var @event))
                {
                    switch (@event)
                    {
                        case LogEntry entry:
                            await WriteEventAsync(response, "log", entry, entry.Sequence, json, cancellationToken);
                            break;
                        case StatusSnapshot status:
                            await WriteEventAsync(response, "status", status, null, json, cancellationToken);
                            break;
                    }
                }

                await response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The browser went away; nothing to clean up beyond disposing the subscription.
        }
    }

    /// <summary>
    /// Waits for the next event. Sends a keep-alive comment and keeps waiting when nothing arrives
    /// within the interval. Returns false once the channel is completed.
    /// </summary>
    private static async Task<bool> WaitForEventOrKeepAliveAsync(Subscription subscription, HttpResponse response, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(KeepAliveInterval);

            try
            {
                return await subscription.Reader.WaitToReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // SSE comment line: ignored by the browser but keeps idle connections open through proxies.
                await response.WriteAsync(": keep-alive\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
            }
        }
    }

    private static long? ParseLastEventId(HttpContext context) =>
        long.TryParse(context.Request.Headers["Last-Event-ID"], out var id) ? id : null;

    private static async Task WriteEventAsync(HttpResponse response, string eventName, object payload, long? id, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        await response.WriteAsync($"event: {eventName}\n", cancellationToken);
        if (id is not null)
        {
            await response.WriteAsync($"id: {id}\n", cancellationToken);
        }

        // System.Text.Json never emits raw newlines inside a compact document, so one data line is enough.
        await response.WriteAsync($"data: {JsonSerializer.Serialize(payload, json)}\n\n", cancellationToken);
    }
}
