using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aegis.Application.Nodes;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace Aegis.Infrastructure.Nodes;

public sealed class NodeFcmOptions
{
    public string ProjectId { get; set; } = "";
    public string ServiceAccountFile { get; set; } = "";
}
public interface INodeFcmCredentials { bool Configured { get; } Task<string> AccessTokenAsync(CancellationToken ct); }
public sealed class NodeFcmCredentials : INodeFcmCredentials
{
    private readonly GoogleCredential? credential;
    public bool Configured => credential is not null;
    public NodeFcmCredentials(IOptions<NodeFcmOptions> options)
    {
        var o = options.Value;
        if (string.IsNullOrEmpty(o.ProjectId) && string.IsNullOrEmpty(o.ServiceAccountFile)) return;
        if (!Regex.IsMatch(o.ProjectId, "\\A[a-z][a-z0-9-]{4,62}\\z")) throw new InvalidOperationException("Invalid FCM project configuration.");
        try {
            // Validate trusted configured file type/project before official OAuth credential loading.
            using var input = File.OpenRead(o.ServiceAccountFile); using var json = JsonDocument.Parse(input);
            if (json.RootElement.GetProperty("type").GetString() != "service_account" || json.RootElement.GetProperty("project_id").GetString() != o.ProjectId)
                throw new InvalidOperationException();
            credential = CredentialFactory.FromJson<ServiceAccountCredential>(json.RootElement.GetRawText()).ToGoogleCredential().CreateScoped("https://www.googleapis.com/auth/firebase.messaging");
        } catch { throw new InvalidOperationException("Invalid FCM service-account configuration; inspect the private file without logging its contents."); }
    }
    public Task<string> AccessTokenAsync(CancellationToken ct) => credential is null ? throw new InvalidOperationException("FCM not configured.") : credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: ct);
}
public sealed class NodePushRegistrations(AegisDbContext db, IDataProtectionProvider protection, INodeFcmCredentials credentials, TimeProvider clock) : INodePushRegistrations
{
    private static readonly Meter RegistrationMeter = new("Aegis.NodePushRegistration", "1");
    private static readonly Counter<long> Registered = RegistrationMeter.CreateCounter<long>("node_push_registrations_total");
    private readonly IDataProtector protector = protection.CreateProtector("Aegis.Node.FcmToken.v1");
    public async Task<bool> HasRouteAsync(Guid id, CancellationToken ct = default) => credentials.Configured &&
        await db.NodePushRegistrations.AsNoTracking().AnyAsync(r => r.NodeId == id && r.ExpiresAt > clock.GetUtcNow(), ct) &&
        await db.Nodes.AsNoTracking().AnyAsync(n => n.Id == id && n.Enabled && n.RevokedAt == null, ct);
    public async Task RegisterAsync(Guid actor, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048 || token.Any(c => c <= ' ' || c > '~')) throw new NodeException("invalid_push_registration", "Registration inválida.");
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7070003)", ct);
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == actor, ct);
        if (node is null || node.RevokedAt is not null) throw new NodeException("node_revoked", "Identidade revogada.", 401);
        if (!node.Enabled) throw new NodeException("node_disabled", "Node desativado.", 403);
        if (node.Platform != NodePlatform.Android) throw new NodeException("unsupported_push_provider", "Runtime não suporta este provider.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        if (await db.NodePushRegistrations.AnyAsync(r => r.NodeId != actor && r.TokenHash == hash, ct)) throw new NodeException("push_registration_conflict", "Registration já vinculada.", 409);
        var current = await db.NodePushRegistrations.SingleOrDefaultAsync(r => r.NodeId == actor, ct);
        if (current is null) db.NodePushRegistrations.Add(new(actor, protector.Protect(token), hash, clock.GetUtcNow()));
        else current.Refresh(protector.Protect(token), hash, clock.GetUtcNow());
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); Registered.Add(1);
    }
    public async Task RemoveAsync(Guid actor, CancellationToken ct) { db.NodePushRegistrations.RemoveRange(await db.NodePushRegistrations.Where(r => r.NodeId == actor).ToArrayAsync(ct)); await db.SaveChangesAsync(ct); }
    public string Unprotect(NodePushRegistration registration) => protector.Unprotect(registration.EncryptedToken);
    public async Task InvalidateAsync(Guid id, byte[] observedHash, CancellationToken ct)
    {
        // A response for an old token must not remove a refreshed registration.
        if (db.Database.IsRelational()) { await db.NodePushRegistrations.Where(r => r.NodeId == id && r.TokenHash == observedHash).ExecuteDeleteAsync(ct); return; }
        var current = await db.NodePushRegistrations.SingleOrDefaultAsync(r => r.NodeId == id, ct);
        if (current is not null && current.TokenHash.SequenceEqual(observedHash)) { db.NodePushRegistrations.Remove(current); await db.SaveChangesAsync(ct); }
    }
}
public sealed class NodeFcmTransport(AegisDbContext db, NodePushRegistrations registrations, INodeFcmCredentials credentials,
    IHttpClientFactory clients, IOptions<NodeFcmOptions> options, TimeProvider clock, ILogger<NodeFcmTransport> logger) : INodePushNotifications
{
    private static readonly Meter Meter = new("Aegis.NodePush", "1");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("node_push_failures_total");
    public async Task<string> SendAsync(Guid id, NodeNotificationCommand command, CancellationToken ct)
    {
        if (command.ExpiresAt <= clock.GetUtcNow()) return "expired";
        if (!await registrations.HasRouteAsync(id, ct)) return "unavailable";
        var registration = await db.NodePushRegistrations.AsNoTracking().SingleOrDefaultAsync(r => r.NodeId == id, ct);
        if (registration is null) return "unavailable";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{options.Value.ProjectId}/messages:send");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await credentials.AccessTokenAsync(deadline.Token));
            request.Content = JsonContent.Create(new { message = new { token = registrations.Unprotect(registration),
                data = new Dictionary<string, string> { ["nodeId"] = id.ToString(), ["type"] = "notification.show", ["version"] = "1", ["commandId"] = command.CommandId.ToString(), ["expiresAt"] = command.ExpiresAt.ToString("O"), ["title"] = command.Input.Title, ["body"] = command.Input.Body },
                android = new { priority = "HIGH", ttl = Math.Clamp((int)Math.Ceiling((command.ExpiresAt - clock.GetUtcNow()).TotalSeconds), 1, 300) + "s", restricted_package_name = "com.aegis.node" } } });
            if (!await registrations.HasRouteAsync(id, deadline.Token)) return "unavailable";
            using var response = await clients.CreateClient("node-fcm").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.IsSuccessStatusCode) { logger.LogInformation("Node notification accepted NodeId={NodeId} CommandId={CommandId} Transport=fcm", id, command.CommandId); return "accepted"; }
            // Inspect only known typed FCM codes; never log the response body/token/content.
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token); var bytes = new byte[8193]; var length = 0;
            while (length < bytes.Length) { var count = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token); if (count == 0) break; length += count; }
            var invalid = false;
            if (length <= 8192) try { using var json = JsonDocument.Parse(bytes.AsMemory(0, length)); if (json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("details", out var details))
                invalid = details.EnumerateArray().Any(d => d.TryGetProperty("@type", out var type) && type.GetString() == "type.googleapis.com/google.firebase.fcm.v1.FcmError" && d.TryGetProperty("errorCode", out var code) && code.GetString() == "UNREGISTERED"); } catch (JsonException) { }
            if (invalid) await registrations.InvalidateAsync(id, registration.TokenHash, deadline.Token);
            Failures.Add(1); logger.LogWarning("Node push failed NodeId={NodeId} CommandId={CommandId} HttpStatus={HttpStatus} Reason={Reason}", id, command.CommandId, (int)response.StatusCode, invalid ? "unregistered" : "provider_rejected"); return invalid ? "unavailable" : "failed";
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Failures.Add(1); return "timeout"; }
        catch (Exception e) when (e is HttpRequestException or CryptographicException or InvalidOperationException) { Failures.Add(1); logger.LogWarning("Node push failed NodeId={NodeId} CommandId={CommandId} Reason=sender_failed", id, command.CommandId); return "failed"; }
    }
}
