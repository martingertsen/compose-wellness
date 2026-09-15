using System.Net;

namespace ComposeWellness.Tests.Fakes;

/// <summary>
/// Answers requests with a fixed status and body and records the requests it saw. Responses queued
/// through <see cref="QueueResponse"/> are returned first, one per request, before falling back to
/// the fixed status and body, so a test can make consecutive requests behave differently.
/// </summary>
public sealed class StubHttpMessageHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _queuedResponses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public void QueueResponse(HttpStatusCode queuedStatus, string queuedBody) =>
        _queuedResponses.Enqueue((queuedStatus, queuedBody));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var (responseStatus, responseBody) = _queuedResponses.Count > 0 ? _queuedResponses.Dequeue() : (status, body);
        return Task.FromResult(new HttpResponseMessage(responseStatus)
        {
            Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
        });
    }
}
