using System.Net.WebSockets;
using System.Text.Json;
namespace Aegis.Api.Nodes.Transport;
public sealed record NodeMessage(int ProtocolVersion, string Type, Guid MessageId, DateTimeOffset SentAt, JsonElement? Payload);
public sealed class NodeProtocolException(string reason) : Exception(reason);
public static class NodeProtocol
{
    public static async Task<NodeMessage?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var bytes = new byte[NodeTransportOptions.MaxMessageBytes + 1]; var count = 0; var fragments = 0;
        while (true)
        {
            if (++fragments > 32) throw new NodeProtocolException("too_many_fragments");
            var frame = await socket.ReceiveAsync(bytes.AsMemory(count), ct);
            if (frame.MessageType == WebSocketMessageType.Close) return null;
            count += frame.Count;
            if (frame.MessageType != WebSocketMessageType.Text) throw new NodeProtocolException("text_required");
            if (count > NodeTransportOptions.MaxMessageBytes) throw new NodeProtocolException("message_too_large");
            if (frame.EndOfMessage) break;
        }
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object || json.EnumerateObject().Any(p => p.Name is not ("protocolVersion" or "type" or "messageId" or "sentAt" or "payload")) ||
                json.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)) throw new NodeProtocolException("invalid_envelope");
            if (json.GetProperty("protocolVersion").GetInt32() != 1) throw new NodeProtocolException("protocol_mismatch");
            var type = json.GetProperty("type").GetString();
            if (type is not ("hello" or "heartbeat" or "command_result")) throw new NodeProtocolException("unknown_message");
            var id = json.GetProperty("messageId").GetGuid();
            if (id == Guid.Empty) throw new NodeProtocolException("invalid_message_id");
            var sentAt = json.GetProperty("sentAt").GetDateTimeOffset(); // diagnostic only
            JsonElement? payload = json.TryGetProperty("payload", out var value) ? value.Clone() : null;
            if (type == "heartbeat" && payload is not null) throw new NodeProtocolException("unexpected_payload");
            if (type == "hello" && (payload is null || payload.Value.ValueKind != JsonValueKind.Object ||
                payload.Value.EnumerateObject().Any(p => p.Name is not ("appVersion" or "capabilities")) || payload.Value.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1) || !payload.Value.TryGetProperty("appVersion", out var version) ||
                version.ValueKind != JsonValueKind.String || version.GetString()!.Length > 80)) throw new NodeProtocolException("invalid_hello");
            if (type == "command_result") {
                if (payload is null || payload.Value.ValueKind != JsonValueKind.Object || payload.Value.EnumerateObject().Count() != 2 ||
                    payload.Value.EnumerateObject().Any(p => p.Name is not ("commandId" or "status")) ||
                    payload.Value.GetProperty("commandId").GetGuid() == Guid.Empty || !Aegis.Application.Nodes.NotificationContract.Results.Contains(payload.Value.GetProperty("status").GetString()!)) throw new NodeProtocolException("invalid_command_result");
            }
            return new(1, type, id, sentAt, payload);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new NodeProtocolException("invalid_json"); }
    }
    public static IReadOnlyList<Aegis.Application.Nodes.NodeCapability> Capabilities(JsonElement payload, out int unknown)
    {
        unknown = 0;
        if (!payload.TryGetProperty("capabilities", out var array)) return Array.Empty<Aegis.Application.Nodes.NodeCapability>();
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > Aegis.Application.Nodes.NodeCapabilityCatalog.MaximumCount)
            throw new NodeProtocolException("invalid_capabilities");
        try
        {
            var items = new List<Aegis.Application.Nodes.NodeCapability>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
                    item.EnumerateObject().Any(p => p.Name is not ("name" or "version"))) throw new NodeProtocolException("invalid_capabilities");
                items.Add(new(item.GetProperty("name").GetString()!, item.GetProperty("version").GetInt32()));
            }
            return Aegis.Application.Nodes.NodeCapabilityCatalog.Validate(items, out unknown);
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new NodeProtocolException("invalid_capabilities"); }
    }
    public static Task SendAsync(WebSocket socket, string type, Guid messageId, DateTimeOffset now, object? payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, type, messageId, sentAt = now, payload }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (bytes.Length > 4096) throw new Aegis.Application.Nodes.NodeException("invalid_notification", "Payload serializado excessivo.");
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
    public static int CloseCode(string reason) => reason switch {
        "node_revoked" => 4001, "node_disabled" => 4003, "protocol_mismatch" => 4006,
        "heartbeat_timeout" => 4008, "replaced" => 4000, "server_shutdown" => 1001,
        "message_too_large" => 1009, "disconnected" => 1000, _ => 1008 };
}
