using System.Text.Json;

namespace Aegis.Domain.Entities;

public sealed record PlaceCircle(Guid Id, double Latitude, double Longitude, double RadiusMeters)
{
    public void Validate()
    {
        if (Id == Guid.Empty || !double.IsFinite(Latitude) || Latitude is < -90 or > 90 ||
            !double.IsFinite(Longitude) || Longitude is < -180 or > 180 ||
            !double.IsFinite(RadiusMeters) || RadiusMeters is < 50 or > 100000)
            throw new ArgumentException("Cada área precisa de ponto válido e raio entre 50 m e 100 km.");
    }
}

public sealed record PlaceMonitoringEnvelope(int Revision, bool Enabled, double PaddingMeters, DateTimeOffset? ApprovedAt, string GeometryFingerprint);

public sealed class Place : AuditableEntity
{
    private Place() { }
    public Place(string name, string type, Guid? parentId, IReadOnlyList<PlaceCircle> circles, IReadOnlyList<string> aliases, DateTimeOffset now)
    { InitializeAudit(now); Change(name, type, parentId, circles, aliases, now, false); }
    public string Name { get; private set; } = "";
    public string Type { get; private set; } = "other";
    public Guid? ParentId { get; private set; }
    public int Revision { get; private set; } = 1;
    public string CirclesJson { get; private set; } = "[]";
    public string AliasesJson { get; private set; } = "[]";
    // Additive configuration: never replaces or expands the user's real geometry.
    public string? MonitoringEnvelopeJson { get; private set; }
    public PlaceMonitoringEnvelope? MonitoringEnvelope => MonitoringEnvelopeJson is null ? null : JsonSerializer.Deserialize<PlaceMonitoringEnvelope>(MonitoringEnvelopeJson);
    public void ConfigureMonitoringEnvelope(bool enabled, double paddingMeters, string geometryFingerprint, DateTimeOffset now)
    {
        if (!double.IsFinite(paddingMeters) || paddingMeters is < 50 or > 1000)
            throw new ArgumentException("Use uma extensão de proximidade entre 50 m e 1 km.");
        MonitoringEnvelopeJson = JsonSerializer.Serialize(new PlaceMonitoringEnvelope((MonitoringEnvelope?.Revision ?? 0) + 1, enabled, paddingMeters, now, geometryFingerprint));
        Touch(now);
    }
    public IReadOnlyList<PlaceCircle> Circles => JsonSerializer.Deserialize<PlaceCircle[]>(CirclesJson)!;
    public IReadOnlyList<string> Aliases => JsonSerializer.Deserialize<string[]>(AliasesJson)!;
    public void Change(string name, string type, Guid? parentId, IReadOnlyList<PlaceCircle> circles, IReadOnlyList<string> aliases, DateTimeOffset now, bool increment = true)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || name.Any(char.IsControl) ||
            type is not ("property" or "room" or "outdoor" or "other") || parentId == Guid.Empty || parentId == Id)
            throw new ArgumentException("Nome, tipo ou lugar pai inválido.");
        if (circles is null || circles.Count > 25 || circles.Select(c => c.Id).Distinct().Count() != circles.Count)
            throw new ArgumentException("Use até 25 áreas circulares distintas por lugar.");
        foreach (var circle in circles) circle.Validate();
        if (aliases is null || aliases.Count > 20 || aliases.Any(a => string.IsNullOrWhiteSpace(a) || a.Trim().Length > 80 || a.Any(char.IsControl)))
            throw new ArgumentException("Apelidos inválidos.");
        Name = name.Trim(); Type = type; ParentId = parentId;
        CirclesJson = JsonSerializer.Serialize(circles);
        AliasesJson = JsonSerializer.Serialize(aliases.Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        if (increment) Revision++;
        Touch(now);
    }
}

// Exactly one latest observation per Node; never a continuous location trail.
public sealed class NodeLocationProfile
{
    private NodeLocationProfile() { }
    public NodeLocationProfile(Guid nodeId) { NodeId = nodeId; }
    public Guid NodeId { get; private set; }
    public string Mode { get; private set; } = "none";
    public Guid? FixedPlaceId { get; private set; }
    public bool IsUserSource { get; private set; }
    public double? Latitude { get; private set; }
    public double? Longitude { get; private set; }
    public double? AccuracyMeters { get; private set; }
    public DateTimeOffset? CapturedAt { get; private set; }
    public DateTimeOffset? ReceivedAt { get; private set; }
    public string? Source { get; private set; }
    public Guid? ResolvedPlaceId { get; private set; }
    public Guid? LastRequestId { get; private set; }
    public void Select(bool selected) => IsUserSource = selected;
    public void Configure(string mode, Guid? fixedPlaceId)
    {
        if (mode is not ("none" or "fixed" or "dynamic") || mode == "fixed" && fixedPlaceId is null || mode != "fixed" && fixedPlaceId is not null)
            throw new ArgumentException("Localização fixa requer um lugar; outros modos não usam lugar fixo.");
        Mode = mode; FixedPlaceId = fixedPlaceId;
        if (mode != "dynamic") { Latitude = Longitude = AccuracyMeters = null; CapturedAt = ReceivedAt = null; Source = null; ResolvedPlaceId = null; }
    }
    public bool Observe(double latitude, double longitude, double accuracyMeters, DateTimeOffset capturedAt, string source, Guid? resolvedPlaceId, DateTimeOffset now, Guid? requestId = null)
    {
        if (Mode != "dynamic") throw new ArgumentException("Ative localização dinâmica neste celular antes de compartilhar posição.");
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90 || !double.IsFinite(longitude) || longitude is < -180 or > 180 ||
            !double.IsFinite(accuracyMeters) || accuracyMeters is <= 0 or > 100000 || source is not ("fused" or "gps" or "network" or "passive") ||
            capturedAt > now.AddMinutes(2) || capturedAt < now.AddDays(-1)) throw new ArgumentException("Observação de localização inválida ou antiga demais.");
        if (CapturedAt is { } previous && capturedAt <= previous) return false;
        Latitude = latitude; Longitude = longitude; AccuracyMeters = accuracyMeters;
        CapturedAt = capturedAt.ToUniversalTime(); ReceivedAt = now; Source = source; ResolvedPlaceId = resolvedPlaceId; LastRequestId = requestId;
        return true;
    }
}
