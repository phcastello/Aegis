using Aegis.Domain.Entities;

namespace Aegis.Application.Places;

public sealed record PlaceView(Guid Id, string Name, string Type, Guid? ParentId, int Revision,
    IReadOnlyList<PlaceCircle> Circles, IReadOnlyList<string> Aliases);
public sealed record PlaceWrite(string Name, string Type, Guid? ParentId, IReadOnlyList<PlaceCircle> Circles, IReadOnlyList<string> Aliases, int? ExpectedRevision = null);
public sealed record LocationSettingsWrite(Guid? SourceNodeId = null, Guid? NodeId = null, string? Mode = null, Guid? FixedPlaceId = null);
public sealed record LocationProfileView(Guid NodeId, string Mode, Guid? FixedPlaceId);
public sealed record LocationObservationView(Guid NodeId, double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset CapturedAt,
    DateTimeOffset ReceivedAt, string Source, Guid? PlaceId, double AgeSeconds);
public sealed record LocationSettingsView(Guid? SourceNodeId, IReadOnlyList<LocationProfileView> Nodes, LocationObservationView? Latest);
public sealed record LocationObservationWrite(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset CapturedAt, string Source, Guid? RequestId = null);
public sealed record GeographicRuleWrite(string Text, IReadOnlyList<Guid> PlaceIds, Guid ObserverNodeId, string Transition, bool Repeat = false,
    DateTimeOffset? StartsAt = null, DateTimeOffset? EndsAt = null, string TimeZoneId = "America/Sao_Paulo", int? ExpectedRevision = null);
public sealed record GeographicRuleView(Guid Id, string Text, IReadOnlyList<Guid> PlaceIds, Guid ObserverNodeId, int Revision, string Transition,
    bool Repeat, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string TimeZoneId, string DeliveryPolicy, string Status,
    string SyncStatus, DateTimeOffset LeaseUntil, DateTimeOffset? SyncedAt, string? ErrorCode);
public sealed record GeographicSyncItem(Guid Id, int Revision, string Status, string? ErrorCode = null);
public sealed record GeographicSyncWrite(string ManifestRevision, IReadOnlyList<GeographicSyncItem> Rules);
public sealed record GeographicEventWrite(Guid EventId, Guid OccurrenceId, Guid RuleId, int RuleRevision, string Transition, DateTimeOffset OccurredAt, long Sequence, DateTimeOffset? PostedAt = null);
public sealed record PlaceResolution(string Status, IReadOnlyList<PlaceView> Candidates, Guid? PlaceId, string Reason, double? LocationAgeSeconds);
public sealed record MapSearchResult(string Name, double Latitude, double Longitude);
public sealed record MapSearchView(bool Configured, IReadOnlyList<MapSearchResult> Results);

public interface IPlaceService
{
    Task<IReadOnlyList<PlaceView>> ListAsync(CancellationToken ct = default);
    Task<PlaceView> SaveAsync(Guid? id, PlaceWrite request, CancellationToken ct = default);
    Task DeleteAsync(Guid id, int expectedRevision, CancellationToken ct = default);
    Task<LocationSettingsView> SettingsAsync(CancellationToken ct = default);
    Task<LocationSettingsView> ConfigureAsync(LocationSettingsWrite request, CancellationToken ct = default);
    Task ObserveAsync(Guid actor, LocationObservationWrite request, CancellationToken ct = default);
    Task<PlaceResolution> ResolveAsync(string reference, bool allCandidates, CancellationToken ct = default);
    Task<object> RefreshAsync(CancellationToken ct = default);
    Task<object> RequestGeofenceSyncAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GeographicRuleView>> RulesAsync(CancellationToken ct = default);
    Task<GeographicRuleView> SaveRuleAsync(Guid? id, GeographicRuleWrite request, Guid? conversationId = null, CancellationToken ct = default);
    Task<GeographicRuleView> ActOnRuleAsync(Guid id, int revision, string action, CancellationToken ct = default);
    Task<object> ManifestAsync(Guid actor, CancellationToken ct = default);
    Task<object> ConfirmAsync(Guid actor, GeographicSyncWrite request, CancellationToken ct = default);
    Task<object> EventAsync(Guid actor, GeographicEventWrite request, CancellationToken ct = default);
    Task RememberAsync(Guid conversationId, string scope, IEnumerable<Guid> ids, CancellationToken ct = default);
    Task RequireObservedAsync(Guid conversationId, string scope, IEnumerable<Guid> ids, CancellationToken ct = default);
}

public sealed class LocationContextOptions
{
    public int MaximumAgeSeconds { get; set; } = 900;
    public double MaximumCityAccuracyMeters { get; set; } = 2000;
    public double LocalResidenceMeters { get; set; } = 50000;
    public double CandidateMarginMeters { get; set; } = 100000;
    public double CandidateDistanceRatio { get; set; } = 3;
    public int RefreshTimeoutSeconds { get; set; } = 15;
    public bool IsValid() => MaximumAgeSeconds is >= 30 and <= 3600 && MaximumCityAccuracyMeters is >= 100 and <= 10000 &&
        LocalResidenceMeters is >= 1000 and <= 100000 && CandidateMarginMeters >= LocalResidenceMeters && CandidateDistanceRatio >= 2 && RefreshTimeoutSeconds is >= 1 and <= 30;
}
