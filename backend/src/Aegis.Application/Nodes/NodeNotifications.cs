using System.Diagnostics.Metrics;
using System.Text;
namespace Aegis.Application.Nodes;

public sealed record NotificationShowRequest(string Title, string Body, Guid? PreferredNodeId = null, int TtlSeconds = 60);
public sealed record NodeNotificationCommand(Guid CommandId, string Capability, int CapabilityVersion, DateTimeOffset ExpiresAt, NotificationInput Input);
public sealed record NotificationInput(string Title, string Body);
public sealed record NotificationDispatchResult(NodeTargetSummary? Node, string Reachability, string? Transport, string Status, Guid? CommandId, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? DiagnosticCode = null);
public static class NotificationContract
{
    public const string Capability = "notification.show";
    public static readonly HashSet<string> Results = new(StringComparer.Ordinal) { "success", "expired", "unsupported", "permission_denied", "failed", "duplicate" };
    // Diagnostic metadata is transient: only the Nodes test endpoint exposes it.
    public const int MaximumDiagnosticLength = 48;
    public static readonly IReadOnlySet<string> DiagnosticCodes = new HashSet<string>(StringComparer.Ordinal) {
        "android_jni_unavailable",
        "android_jni_call_failed",
        "android_native_timeout",
        "android_payload_parse_failed",
        "android_expiry_parse_failed",
        "android_invalid_command_id",
        "android_invalid_notification_payload",
        "android_permission_denied",
        "android_permission_check_failed",
        "android_channel_missing",
        "android_pending_intent_failed",
        "android_notification_build_failed",
        "android_notification_post_failed",
        "android_dedupe_read_failed",
        "android_dedupe_persist_failed"
    };
    public static bool ValidDiagnostic(string? code) => code is not null && code.Length <= MaximumDiagnosticLength && DiagnosticCodes.Contains(code);
    public static void Validate(string? title, string? body, int ttl)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || body is null || body.Length > 2000 || ttl is < 5 or > 300 ||
            title.Any(char.IsControl) || body.Any(c => char.IsControl(c) && c is not ('\n' or '\t')) || Encoding.UTF8.GetByteCount(title + body) > 2800 ||
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new NotificationInput(title, body), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Length > 3000)
            throw new NodeException("invalid_notification", "Título/corpo inválido ou excessivo; expiração deve ser 5–300 segundos.");
    }
}
public sealed record NodeLiveNotificationResult(string Status, string? DiagnosticCode = null);
public interface INodeLiveNotifications { Task<NodeLiveNotificationResult> SendAsync(Guid id, NodeNotificationCommand command, CancellationToken ct); }
public interface INodeBackgroundAvailability { Task<bool> HasRouteAsync(Guid id, CancellationToken ct = default); }
public interface INodePushRegistrations : INodeBackgroundAvailability
{
    Task RegisterAsync(Guid actor, string token, CancellationToken ct);
    Task RemoveAsync(Guid actor, CancellationToken ct);
}
public interface INodePushNotifications { Task<string> SendAsync(Guid id, NodeNotificationCommand command, CancellationToken ct); }
public interface INodeNotificationDispatcher
{
    Task<NotificationDispatchResult> DispatchAsync(Guid actor, NotificationShowRequest request, CancellationToken ct = default);
    // Internal delivery of an already persisted, infrastructure-selected command/target.
    Task<NotificationDispatchResult> DispatchToNodeAsync(Guid nodeId, NodeNotificationCommand command, CancellationToken ct = default);
}
public sealed class NodeNotificationDispatcher(INodeRegistry nodes, INodeTargetResolver resolver, INodeConnections live,
    INodeLiveNotifications socket, INodePushNotifications push, INodeBackgroundAvailability background, TimeProvider clock) : INodeNotificationDispatcher
{
    private static readonly Meter Meter = new("Aegis.NodeNotifications", "1");
    private static readonly Counter<long> Commands = Meter.CreateCounter<long>("node_commands_total"), Results = Meter.CreateCounter<long>("node_command_results_total"),
        Live = Meter.CreateCounter<long>("node_notification_live_total"), Push = Meter.CreateCounter<long>("node_notification_push_total");
    public async Task<NotificationDispatchResult> DispatchAsync(Guid actor, NotificationShowRequest request, CancellationToken ct = default)
    {
        NotificationContract.Validate(request.Title, request.Body, request.TtlSeconds);
        var selection = await resolver.ResolveAsync(actor, new([new(NotificationContract.Capability, 1)], request.PreferredNodeId, "notification"), ct);
        if (selection.Node is null) return new(null, "offline", null, "no_eligible_node", null);
        var command = new NodeNotificationCommand(Guid.NewGuid(), NotificationContract.Capability, 1, clock.GetUtcNow().AddSeconds(request.TtlSeconds), new(request.Title, request.Body));
        return await Send(await nodes.ListAsync(actor, ct), selection.Node, command, ct);
    }
    public async Task<NotificationDispatchResult> DispatchToNodeAsync(Guid nodeId, NodeNotificationCommand command, CancellationToken ct = default)
    {
        NotificationContract.Validate(command.Input.Title, command.Input.Body, 300);
        if (command.CommandId == Guid.Empty || command.Capability != NotificationContract.Capability || command.CapabilityVersion != 1 || command.ExpiresAt > clock.GetUtcNow().AddSeconds(300))
            throw new NodeException("invalid_notification", "Command de notification inválido.");
        var inventory = await nodes.ListForDeliveryAsync(ct);
        var target = inventory.SingleOrDefault(n => n.Id == nodeId);
        return await Send(inventory, new(nodeId, target?.Name ?? ""), command, ct);
    }
    private async Task<NotificationDispatchResult> Send(IReadOnlyList<NodeView> inventory, NodeTargetSummary selection, NodeNotificationCommand command, CancellationToken ct)
    {
        var id = selection.Id;
        // Revalidate administrative metadata and current capability/route immediately before I/O.
        // Never fallback after a possible send; reminders decide retry from persisted outcomes.
        var target = inventory.SingleOrDefault(n => n.Id == id);
        if (target is null || !target.Enabled || target.RevokedAt is not null) return new(selection, "offline", null, "unavailable", command.CommandId);
        if (command.ExpiresAt <= clock.GetUtcNow()) return new(selection, "offline", null, "expired", command.CommandId);
        Commands.Add(1); string result, transport, availability; string? diagnostic = null;
        if (live.LiveCapabilities(id)?.Any(c => c.Name == NotificationContract.Capability && c.Version >= 1) == true)
        { transport = "live_websocket"; availability = "online"; Live.Add(1); var liveResult = await socket.SendAsync(id, command, ct); result = liveResult.Status; diagnostic = NotificationContract.ValidDiagnostic(liveResult.DiagnosticCode) ? liveResult.DiagnosticCode : null; }
        else if (target.Capabilities.Any(c => c.Name == NotificationContract.Capability && c.Version >= 1) && await background.HasRouteAsync(id, ct))
        { transport = "fcm"; availability = "backgroundReachable"; Push.Add(1); result = await push.SendAsync(id, command, ct); }
        else return new(selection, "offline", null, "unavailable", command.CommandId);
        Results.Add(1, new KeyValuePair<string, object?>("transport", transport), new KeyValuePair<string, object?>("status", result));
        return new(selection, availability, transport, result, command.CommandId, diagnostic);
    }
}
