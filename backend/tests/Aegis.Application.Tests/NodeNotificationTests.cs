using Aegis.Application.Nodes;
using Aegis.Api.Nodes.Transport;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using Xunit;
namespace Aegis.Application.Tests;
public sealed class NodeNotificationTests
{
    private sealed class Credentials(bool configured = true) : INodeFcmCredentials {
        public bool Configured => configured;
        public Task<string> AccessTokenAsync(CancellationToken ct) => Task.FromResult("fixture-authorization");
    }
    private sealed class Delivery : INodePushNotifications, INodeLiveNotifications {
        public int Calls; public string Result = "success"; public string? Diagnostic;
        Task<NodeLiveNotificationResult> INodeLiveNotifications.SendAsync(Guid id, NodeNotificationCommand c, CancellationToken ct) { Calls++; return Task.FromResult(new NodeLiveNotificationResult(Result, Diagnostic)); }
        public Task<string> SendAsync(Guid id, NodeNotificationCommand c, CancellationToken ct) { Calls++; return Task.FromResult(Result); }
    }
    private sealed class Fixture : IDisposable {
        public AegisDbContext Db = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Actor = Guid.NewGuid(), Phone = Guid.NewGuid();
        public NodeConnectionRegistry Live = new(TimeProvider.System,Options.Create(new NodeTransportOptions()),NullLogger<NodeConnectionRegistry>.Instance);
        public NodePushRegistrations Push; public NodeRegistry Nodes; public NodeTargetResolver Resolver;
        public Fixture(bool configured=true) {
            foreach(var id in new[]{Actor,Phone}) { Db.Nodes.Add(new(id,"fixture",id==Phone?NodePlatform.Android:NodePlatform.Windows,"0.7.0-stage.8",1,DateTimeOffset.UtcNow));Db.NodeCredentials.Add(new(id,new byte[32],DateTimeOffset.UtcNow)); }
            Db.SaveChanges();Push=new(Db,new EphemeralDataProtectionProvider(),new Credentials(configured),TimeProvider.System);Nodes=new(Db,TimeProvider.System,Live,Push);Resolver=new(Nodes,Live,Push);
        }
        public async Task Announce() => await new NodeTransportHistory(Db).AnnouncedAsync(Phone,"0.7.0-stage.8",DateTimeOffset.UtcNow,[new("notification.show",1),new("audio.output",1)],default);
        public void Dispose() { Live.Dispose();Db.Dispose(); }
    }
    [Theory][InlineData("", "body",60)][InlineData("title","body",0)][InlineData("title","body",301)][InlineData("bad\n","body",60)]
    public void InvalidNotificationRejected(string title,string body,int ttl) => Assert.Throws<NodeException>(()=>NotificationContract.Validate(title,body,ttl));
    [Fact] public void BoundedUnicodeAndJsonEscaping() {
        NotificationContract.Validate("Aegis","Olá",60);
        Assert.Throws<NodeException>(()=>NotificationContract.Validate(new string('x',121),"body",60));
        Assert.Throws<NodeException>(()=>NotificationContract.Validate("title",new string('x',2001),60));
        Assert.Throws<NodeException>(()=>NotificationContract.Validate("title",new string('"',2000),60));
    }
    [Fact] public async Task RegistrationRefreshEncryptionAndAdministrativeLifecycle() {
        using var f=new Fixture();await f.Push.RegisterAsync(f.Phone,"fixture-token-1",default);Assert.True(await f.Push.HasRouteAsync(f.Phone));
        var first=await f.Db.NodePushRegistrations.SingleAsync();Assert.DoesNotContain("fixture-token",first.EncryptedToken);Assert.Equal("fixture-token-1",f.Push.Unprotect(first));
        var oldHash=first.TokenHash.ToArray();await f.Push.RegisterAsync(f.Phone,"fixture-token-2",default);await f.Push.InvalidateAsync(f.Phone,oldHash,default);Assert.Single(f.Db.NodePushRegistrations);
        await f.Nodes.SetEnabledAsync(f.Actor,f.Phone,false);Assert.False(await f.Push.HasRouteAsync(f.Phone));
        await Assert.ThrowsAsync<NodeException>(()=>f.Push.RegisterAsync(f.Phone,"fixture-token-3",default));
        await f.Nodes.SetEnabledAsync(f.Actor,f.Phone,true);Assert.True(await f.Push.HasRouteAsync(f.Phone));
        await f.Nodes.RevokeAsync(f.Actor,f.Phone);Assert.Empty(f.Db.NodePushRegistrations);Assert.False(await f.Push.HasRouteAsync(f.Phone));
        await Assert.ThrowsAsync<NodeException>(()=>f.Push.RegisterAsync(f.Phone,"fixture-token-3",default));
    }
    [Fact] public async Task UnconfiguredFcmNeverCreatesBackgroundReachable() {
        using var f=new Fixture(false);await f.Push.RegisterAsync(f.Phone,"fixture-token",default);Assert.False(await f.Push.HasRouteAsync(f.Phone));Assert.Equal("offline",(await f.Nodes.MeAsync(f.Phone)).Availability);
    }
    [Fact] public async Task OnlineWinsBackgroundAndAudioKeepsLiveRequirement() {
        using var f=new Fixture();await f.Announce();await f.Push.RegisterAsync(f.Phone,"fixture-token",default);
        Assert.Equal("backgroundReachable",(await f.Nodes.MeAsync(f.Phone)).Availability);
        Assert.Null((await f.Resolver.ResolveAsync(f.Actor,new([new("audio.output",1)]))).Node);
        Assert.Equal(f.Phone,(await f.Resolver.ResolveAsync(f.Actor,new([new("notification.show",1)],null,"notification"))).Node?.Id);
        await Assert.ThrowsAsync<NodeException>(()=>f.Resolver.ResolveAsync(f.Actor,new([new("audio.output",1)],null,"notification")));
        f.Live.Register(f.Phone,capabilities:[new("notification.show",1)]);Assert.Equal("online",(await f.Nodes.MeAsync(f.Phone)).Availability);
        f.Live.Disconnect(f.Phone,"test");Assert.Equal("backgroundReachable",(await f.Nodes.MeAsync(f.Phone)).Availability);
        await f.Push.RemoveAsync(f.Phone,default);Assert.Equal("offline",(await f.Nodes.MeAsync(f.Phone)).Availability);
    }
    [Theory][InlineData(true,"success")][InlineData(true,"permission_denied")][InlineData(true,"timeout")][InlineData(false,"accepted")]
    public async Task DispatchSelectsOneRouteAndNeverRetriesAmbiguousLive(bool online,string status) {
        using var f=new Fixture();await f.Announce();await f.Push.RegisterAsync(f.Phone,"fixture-token",default);
        if(online) f.Live.Register(f.Phone,capabilities:[new("notification.show",1)]);
        var live=new Delivery {Result=status};var push=new Delivery {Result=status};var dispatcher=new NodeNotificationDispatcher(f.Nodes,f.Resolver,f.Live,live,push,f.Push,TimeProvider.System);
        var result=await dispatcher.DispatchAsync(f.Actor,new("Aegis","fixture",f.Phone));
        Assert.Equal(status,result.Status);Assert.Equal(online?"live_websocket":"fcm",result.Transport);Assert.Equal(online?1:0,live.Calls);Assert.Equal(online?0:1,push.Calls);Assert.NotEqual(Guid.Empty,result.CommandId);
        await f.Nodes.SetEnabledAsync(f.Actor,f.Phone,false);Assert.Equal("no_eligible_node",(await dispatcher.DispatchAsync(f.Actor,new("Aegis","fixture",f.Phone))).Status);
    }
    [Theory]
    [InlineData("android_notification_build_failed", "android_notification_build_failed")]
    [InlineData("arbitrary-secret-exception-prose", null)]
    [InlineData(null, null)]
    public async Task DiagnosticTestEndpointPropagatesOnlyKnownCodes(string? supplied, string? expected) {
        using var f = new Fixture(); await f.Announce(); f.Live.Register(f.Phone, capabilities: [new("notification.show", 1)]);
        var live = new Delivery { Result = "failed", Diagnostic = supplied };
        var dispatcher = new NodeNotificationDispatcher(f.Nodes, f.Resolver, f.Live, live, new Delivery(), f.Push, TimeProvider.System);
        var result = await dispatcher.DispatchAsync(f.Actor, new("Aegis", "fixture", f.Phone));
        Assert.Equal("failed", result.Status); Assert.Equal(expected, result.DiagnosticCode);
        var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("arbitrary-secret", json);
        if (expected is null) Assert.DoesNotContain("diagnosticCode", json);
    }
    private sealed class Handler(HttpStatusCode status,bool invalid):HttpMessageHandler {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct) {
            Calls++;Assert.Equal("https://fcm.googleapis.com/v1/projects/fixture-project/messages:send",r.RequestUri!.ToString());Assert.Equal("Bearer",r.Headers.Authorization!.Scheme);
            var json=await r.Content!.ReadAsStringAsync(ct);Assert.Contains("notification.show",json);Assert.DoesNotContain("credential",json);Assert.Contains("nodeId",json);
            return new(status){Content=new StringContent(invalid?"{\"error\":{\"details\":[{\"@type\":\"type.googleapis.com/google.firebase.fcm.v1.FcmError\",\"errorCode\":\"UNREGISTERED\"}]}}":"{}")};
        }
    }
    private sealed class Clients(Handler handler):IHttpClientFactory {public HttpClient CreateClient(string name)=>new(handler,false);}
    private sealed class RejectedCredentials : INodeFcmCredentials {
        public bool Configured => true;
        public Task<string> AccessTokenAsync(CancellationToken ct) => throw new global::Google.Apis.Auth.OAuth2.Responses.TokenResponseException(
            new() { Error = "invalid_grant", ErrorDescription = "fixture-provider-detail-not-for-client" });
    }
    [Fact] public async Task OAuthRejectionReturnsKnownFailureWithoutSendingOrRemovingRegistration() {
        using var f = new Fixture();
        await f.Push.RegisterAsync(f.Phone, "fixture-token", default);
        var handler = new Handler(HttpStatusCode.OK, false);
        var sender = new NodeFcmTransport(f.Db, f.Push, new RejectedCredentials(), new Clients(handler),
            Options.Create(new NodeFcmOptions { ProjectId = "fixture-project" }), TimeProvider.System, NullLogger<NodeFcmTransport>.Instance);
        var command = new NodeNotificationCommand(Guid.NewGuid(), "notification.show", 1,
            DateTimeOffset.UtcNow.AddSeconds(60), new("Aegis", "fixture"));
        Assert.Equal("failed", await sender.SendAsync(f.Phone, command, default));
        Assert.Equal(0, handler.Calls);
        Assert.True(await f.Push.HasRouteAsync(f.Phone));
    }
    [Fact] public void PrivateCredentialConfigurationRejectsWrongProjectWithoutExposingFile() {
        Assert.False(new NodeFcmCredentials(Options.Create(new NodeFcmOptions())).Configured);
        var file = Path.GetTempFileName();
        try {
            File.WriteAllText(file, "{\"type\":\"service_account\",\"project_id\":\"different-project\",\"private_key\":\"fixture-private-material\"}");
            var error = Assert.Throws<InvalidOperationException>(() => new NodeFcmCredentials(
                Options.Create(new NodeFcmOptions { ProjectId = "fixture-project", ServiceAccountFile = file })));
            Assert.DoesNotContain("fixture-private-material", error.Message);
            Assert.DoesNotContain(file, error.Message);
            Assert.Null(error.InnerException);
        } finally { File.Delete(file); }
    }
    [Theory][InlineData(200,false,"accepted")][InlineData(404,true,"unavailable")][InlineData(403,false,"failed")]
    public async Task HttpV1SenderAcceptedIsNotDisplayedAndInvalidTokensAreRemoved(int status,bool invalid,string expected) {
        using var f=new Fixture();await f.Push.RegisterAsync(f.Phone,"fixture-token",default);var handler=new Handler((HttpStatusCode)status,invalid);
        var sender=new NodeFcmTransport(f.Db,f.Push,new Credentials(),new Clients(handler),Options.Create(new NodeFcmOptions{ProjectId="fixture-project"}),TimeProvider.System,NullLogger<NodeFcmTransport>.Instance);
        var c=new NodeNotificationCommand(Guid.NewGuid(),"notification.show",1,DateTimeOffset.UtcNow.AddSeconds(60),new("Aegis","fixture"));
        Assert.Equal(expected,await sender.SendAsync(f.Phone,c,default));Assert.Equal(!invalid,await f.Push.HasRouteAsync(f.Phone));Assert.Equal(1,handler.Calls);
        Assert.Equal("expired",await sender.SendAsync(f.Phone,c with {ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1)},default));
    }
    [NodePostgresTests.PostgresFact] public Task RealPostgresPushMigrationRefreshUniqueConstraintAndRevoke() => NodePostgresTests.WithDatabase(async create=> {
        await using var db=create();var nodes=new NodeRegistry(db,TimeProvider.System);var request=NodeIdentityTests.Request((await nodes.CreateCodeAsync(null)).Code,"android");var receipt=await nodes.PairAsync(request);var phone=await nodes.FinalizeAsync(new(request.AttemptId,receipt.Credential));
        var push=new NodePushRegistrations(db,new EphemeralDataProtectionProvider(),new Credentials(),TimeProvider.System);
        await push.RegisterAsync(phone.Id,"fixture-token-1",default);var old=(await db.NodePushRegistrations.AsNoTracking().SingleAsync()).TokenHash;
        await push.RegisterAsync(phone.Id,"fixture-token-2",default);await push.InvalidateAsync(phone.Id,old,default);Assert.True(await push.HasRouteAsync(phone.Id));
        var request2=NodeIdentityTests.Request((await nodes.CreateCodeAsync(phone.Id)).Code,"android");var receipt2=await nodes.PairAsync(request2);var second=await nodes.FinalizeAsync(new(request2.AttemptId,receipt2.Credential));
        await Assert.ThrowsAsync<NodeException>(()=>push.RegisterAsync(second.Id,"fixture-token-2",default));
        await nodes.RevokeAsync(phone.Id,phone.Id);Assert.False(await push.HasRouteAsync(phone.Id));Assert.Empty(await db.NodePushRegistrations.ToArrayAsync());
    });

}
