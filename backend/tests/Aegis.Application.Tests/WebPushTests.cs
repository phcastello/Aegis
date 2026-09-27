using System.Net;
using System.Security.Cryptography;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Reminders;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class WebPushTests
{
    [Theory]
    [InlineData(201, true)] [InlineData(202, true)] [InlineData(404, false)] [InlineData(410, false)] [InlineData(429, false)] [InlineData(503, false)]
    public async Task RealProtocolIsEncryptedSignedAndPreservesActualHttpResultWithoutRealPush(int status, bool accepted)
    {
        using var vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256); var v = vapid.ExportParameters(true);
        using var device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256); var d = device.ExportParameters(false);
        var options = new WebPushOptions { Subject = "mailto:eval@example.test", PublicKey = Public(v.Q), PrivateKey = WebEncoders.Base64UrlEncode(v.D!) };
        var subscription = new PushSubscription("https://fcm.googleapis.com/push/test", Public(d.Q), WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)), Guid.NewGuid(), null, TimeProvider.System.GetUtcNow());
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("86400", Assert.Single(request.Headers.GetValues("TTL")));
            var bytes = await request.Content!.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 16);
            Assert.DoesNotContain("stored reminder text", System.Text.Encoding.UTF8.GetString(bytes));
            return new HttpResponseMessage((HttpStatusCode)status);
        }));
        var service = new WebPushService(http, Options.Create(options), TimeProvider.System);
        // Same client can send to multiple devices; it is not disposed by the protocol wrapper.
        for (var i = 0; i < 2; i++)
        {
            var result = await service.SendAsync(subscription, "stored reminder text", default);
            Assert.Equal(status, result.HttpStatus); Assert.Equal(accepted, result.Accepted);
            Assert.Equal(status is 404 or 410, result.PermanentSubscriptionFailure);
        }
    }
    private static string Public(ECPoint point) => WebEncoders.Base64UrlEncode(new byte[] { 4 }.Concat(point.X!).Concat(point.Y!).ToArray());
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
