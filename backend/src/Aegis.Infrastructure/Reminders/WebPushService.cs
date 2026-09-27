using Aegis.Application.Reminders;
using Microsoft.Extensions.Options;
using WebPush;
using IWebPushClient = Aegis.Application.Reminders.IWebPushClient;

namespace Aegis.Infrastructure.Reminders;

public sealed class WebPushService(HttpClient http, IOptions<WebPushOptions> options, TimeProvider clock) : IWebPushClient
{
    public bool IsConfigured => options.Value.IsConfigured;
    public async Task<PushResult> SendAsync(Aegis.Domain.Entities.PushSubscription subscription, string payload, CancellationToken ct)
    {
        if (!IsConfigured) return new PushResult(400, "push_not_configured");
        var o = options.Value;
        using var client = new WebPushClient(http);
        try
        {
            using var request = client.GenerateRequestDetails(new WebPush.PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth), payload,
                new Dictionary<string, object> { ["vapidDetails"] = new VapidDetails(o.Subject, o.PublicKey, o.PrivateKey) { Expiration = clock.GetUtcNow().AddHours(12).ToUnixTimeSeconds() }, ["TTL"] = 86400 });
            // Use the library for protocol encryption/VAPID and retain the actual HTTP
            // result ourselves. Never read or log a provider error body/endpoint.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new PushResult((int)response.StatusCode, response.IsSuccessStatusCode ? null : "push_http_error");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new PushResult(null, "push_timeout"); }
        catch (HttpRequestException) { return new PushResult(null, "push_network_error"); }
        catch (Exception e) when (e is ArgumentException or WebPushException) { return new PushResult(400, "push_invalid_subscription"); }
    }
}
