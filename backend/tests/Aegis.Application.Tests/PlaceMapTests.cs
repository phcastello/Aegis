using System.Net;
using System.Text;
using Aegis.Application.Nodes;
using Aegis.Infrastructure.Places;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

[CollectionDefinition("Place map provider", DisableParallelization = true)]
public sealed class PlaceMapProviderCollection { }

[Collection("Place map provider")]
public sealed class PlaceMapTests
{
    private sealed class Handler(Queue<string> responses) : HttpMessageHandler
    {
        public readonly List<Uri> Requested = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) { Assert.Equal("place-geocoder", name); return client; } }

    [Fact]
    public async Task PhotonSkipsMalformedFeaturesPreservesCoordinateOrderAndReturnsSafeUnavailableForInvalidRoot()
    {
        var handler = new Handler(new(new[] {
            """
            {"features":[
              {"geometry":{"type":"Point","coordinates":[]},"properties":{"name":"Empty coordinate"}},
              {"geometry":{"type":"Polygon","coordinates":[1,2]},"properties":{"name":"Wrong geometry"}},
              {"geometry":{"type":"Point","coordinates":["20",10]},"properties":{"name":"Wrong scalar"}},
              {"geometry":{"type":"Point","coordinates":[200,10]},"properties":{"name":"Out of bounds"}},
              {"geometry":{"type":"Point","coordinates":[20.25,10.5]},"properties":{"name":"Synthetic search result","city":"Synthetic city"}}
            ]}
            """,
            "[]", "{\"features\":{}}"
        }));
        using var client = new HttpClient(handler); var service = new PlaceMaps(new Factory(client), Options.Create(new PlaceMapOptions()));
        var result = await service.SearchAsync("Synthetic street & county", default); Assert.True(result.Configured);
        var point = Assert.Single(result.Results); Assert.Equal(10.5, point.Latitude); Assert.Equal(20.25, point.Longitude); Assert.Equal("Synthetic search result, Synthetic city", point.Name);
        Assert.Contains("q=Synthetic%20street%20%26%20county", handler.Requested.Single().AbsoluteUri);
        for (var i = 0; i < 2; i++) {
            await Task.Delay(1050); // Exercise production provider pacing without real network access.
            var error = await Assert.ThrowsAsync<NodeException>(() => service.SearchAsync("Synthetic", default)); Assert.Equal("address_search_unavailable", error.Code); Assert.Equal(503, error.Status);
        }
        Assert.Equal(3, handler.Requested.Count);
    }
}
