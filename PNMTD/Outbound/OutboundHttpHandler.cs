using System.Diagnostics;
using Microsoft.Extensions.Options;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Outbound;

/// <summary>
/// On the <c>notifications</c> client (docs/redmine-queue-outbound-plan.md §6.3): every Pushover message and HTTP notification
/// into the notification log, with its headers and bodies. The answer is read up to <c>Outbound__MaxBodyKiB</c> and given to the
/// provider as read; neither provider needs more than a short answer. A failure is recorded and passed on unchanged.
/// </summary>
public sealed class OutboundHttpHandler(OutboundLog log, IOptionsMonitor<OutboundOptions> options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = OutboundScope.Current;
        var channel = context?.Channel ?? OutboundChannel.Http;
        var started = Clock.UtcNow;
        var watch = Stopwatch.StartNew();
        // Buffered: what PNMT sends is short, and the content is sent from the buffer afterwards.
        var requestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (channel == OutboundChannel.Pushover)
        {
            requestBody = OutboundRedactor.PushoverBody(requestBody);
        }
        var entry = new OutboundNotificationEntity
        {
            StartedAt = started, Channel = channel, Method = request.Method.Method, Target = request.RequestUri?.ToString() ?? "",
            Recipient = context?.Recipient, Summary = context?.Summary ?? "", Purpose = context?.Purpose ?? "(not a notification)",
            AskedBy = context?.AskedBy ?? "PNMT",
            RequestHeaders = OutboundRedactor.Json(OutboundRedactor.Headers(request.Headers, request.Content?.Headers)),
        };
        if (context is not null)
        {
            context.Recorded = true;
        }
        var running = log.Begin(OutboundLog.NotificationsLog, channel.ToString(), entry.Method, entry.Target, entry.Purpose, entry.AskedBy);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var (text, _) = await ReadAsync(response, options.CurrentValue.MaxBodyBytes, cancellationToken);
            entry.StatusCode = (int)response.StatusCode;
            entry.Outcome = response.IsSuccessStatusCode ? OutboundOutcome.Ok : OutboundOutcome.Failed;
            entry.Error = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            entry.ResponseHeaders = OutboundRedactor.Json(OutboundRedactor.Headers(response.Headers, response.Content.Headers));
            entry.DurationMs = (int)watch.ElapsedMilliseconds;
            log.Record(entry, requestBody, text);
            return response;
        }
        catch (Exception e)
        {
            entry.Outcome = e is OperationCanceledException && cancellationToken.IsCancellationRequested ? OutboundOutcome.Cancelled : OutboundOutcome.Failed;
            entry.Error = $"{e.GetType().Name}: {e.Message}";
            entry.DurationMs = (int)watch.ElapsedMilliseconds;
            log.Record(entry, requestBody, null);
            throw;
        }
        finally
        {
            log.End(running);
        }
    }

    /// <summary>The answer's text up to <paramref name="max"/> bytes; the response then carries what was read.</summary>
    private static async Task<(string Text, bool Truncated)> ReadAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (buffer.Length <= max && (read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        var content = new ByteArrayContent(bytes);
        foreach (var (name, values) in response.Content.Headers.Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
        {
            content.Headers.TryAddWithoutValidation(name, values);
        }
        var truncated = bytes.Length > max;
        response.Content = content;
        return (System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, max)), truncated);
    }
}
