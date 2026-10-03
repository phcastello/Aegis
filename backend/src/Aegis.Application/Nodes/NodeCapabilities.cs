using System.Text.RegularExpressions;
namespace Aegis.Application.Nodes;

public sealed record NodeCapability(string Name, int Version);
public static class NodeCapabilityCatalog
{
    public const int MaximumCount = 32, MaximumNameLength = 64;
    private static readonly HashSet<string> names = new(StringComparer.Ordinal) { "audio.input", "audio.output" };
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(names.Order(StringComparer.Ordinal).ToArray());
    public static bool IsKnown(string name) => names.Contains(name);
    public static bool IsValidName(string? name) => name is { Length: > 0 and <= MaximumNameLength } &&
        Regex.IsMatch(name, @"\A[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    // Validate every item before filtering unknown names; duplicate names are always ambiguous.
    public static IReadOnlyList<NodeCapability> Validate(IEnumerable<NodeCapability>? advertised, out int unknown)
    {
        unknown = 0; var seen = new HashSet<string>(StringComparer.Ordinal); var accepted = new List<NodeCapability>();
        if (advertised is null) return Array.AsReadOnly(Array.Empty<NodeCapability>());
        var count = 0;
        foreach (var capability in advertised)
        {
            if (++count > MaximumCount || capability is null || !IsValidName(capability.Name) || capability.Version < 1 || !seen.Add(capability.Name))
                throw new NodeException("invalid_capabilities", "Conjunto de capabilities inválido.");
            if (IsKnown(capability.Name)) accepted.Add(capability); else unknown++;
        }
        return Array.AsReadOnly(accepted.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray());
    }
}
public sealed record RequiredCapability(string Name, int MinimumVersion);
public sealed record NodeTargetRequest(IReadOnlyList<RequiredCapability> RequiredCapabilities, Guid? PreferredNodeId = null);
public sealed record NodeTargetSummary(Guid Id, string Name);
public sealed record NodeTargetResult(NodeTargetSummary? Node, string? Code, int OnlineNodes, int CapabilityCompatibleNodes);
public interface INodeTargetResolver
{
    Task<NodeTargetResult> ResolveAsync(Guid actor, NodeTargetRequest request, CancellationToken ct = default);
}
public sealed class NodeTargetResolver(INodeRegistry nodes, INodeConnections connections) : INodeTargetResolver
{
    public async Task<NodeTargetResult> ResolveAsync(Guid actor, NodeTargetRequest request, CancellationToken ct = default)
    {
        var required = request.RequiredCapabilities;
        if (required is null || required.Count is < 1 or > NodeCapabilityCatalog.MaximumCount || request.PreferredNodeId == Guid.Empty ||
            required.Any(r => r is null || !NodeCapabilityCatalog.IsValidName(r.Name) || !NodeCapabilityCatalog.IsKnown(r.Name) || r.MinimumVersion < 1) ||
            required.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != required.Count)
            throw new NodeException("invalid_target_requirements", "Informe capabilities conhecidas, únicas e versões positivas.");
        // Administrative metadata is a DB snapshot; live capabilities are read atomically from
        // the current healthy lease, never from persisted history or caller/platform preference.
        var inventory = await nodes.ListAsync(actor, ct); var candidates = new List<NodeView>(); var online = 0;
        foreach (var node in inventory.Where(n => n.Enabled && n.RevokedAt is null))
        {
            var live = connections.LiveCapabilities(node.Id); if (live is null) continue; online++;
            if (required.All(r => live.Any(c => c.Name == r.Name && c.Version >= r.MinimumVersion))) candidates.Add(node);
        }
        var chosen = candidates.FirstOrDefault(n => n.Id == request.PreferredNodeId) ?? candidates
            .OrderByDescending(n => n.TargetPriority).ThenBy(n => n.CreatedAt).ThenBy(n => n.Id).FirstOrDefault();
        return new(chosen is null ? null : new(chosen.Id, chosen.Name), chosen is null ? "no_eligible_node" : null, online, candidates.Count);
    }
}
