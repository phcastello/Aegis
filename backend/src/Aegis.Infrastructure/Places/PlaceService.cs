using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Application.Nodes;
using Aegis.Application.Places;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Places;

public sealed class PlaceService(AegisDbContext db, TimeProvider clock, IOptions<LocationContextOptions> contextOptions, INodeDomainSignals? signals = null) : IPlaceService
{
    private DateTimeOffset Now => clock.GetUtcNow();
    private DbSet<Place> Places => db.Set<Place>();
    private DbSet<NodeLocationProfile> Profiles => db.Set<NodeLocationProfile>();
    private DbSet<GeographicReminderRule> Rules => db.Set<GeographicReminderRule>();
    private DbSet<GeographicReminderEvent> Events => db.Set<GeographicReminderEvent>();
    private async Task RequireObserver(Guid actor, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == actor, ct);
        if (node is null || node.RevokedAt is not null) throw new NodeException("node_revoked", "Identidade de dispositivo inválida.", 401);
        if (!node.Enabled) throw new NodeException("node_disabled", "Dispositivo desativado.", 403);
        if (node.Platform != NodePlatform.Android) throw new NodeException("unsupported_location", "Observações geográficas exigem o adaptador Android.", 403);
    }
    private async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql()) {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7070003)", ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7070011)", ct);
        }
        db.ChangeTracker.Clear(); // Each domain mutation owns its complete unit of work.
        try { var result = await action(); await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct); return result; }
        catch (ArgumentException e) { db.ChangeTracker.Clear(); throw new NodeException("invalid_place_request", e.Message); }
        catch (TimeZoneNotFoundException) { db.ChangeTracker.Clear(); throw new NodeException("invalid_timezone", "Escolha um fuso IANA válido."); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); throw new NodeException("revision_conflict", "O registro mudou. Recarregue antes de salvar.", 409); }
        catch { db.ChangeTracker.Clear(); throw; }
    }
    public static PlaceView View(Place p, IReadOnlyList<PlaceCircle>? geometry = null) => new(p.Id, p.Name, p.Type, p.ParentId, p.Revision, geometry ?? p.Circles, p.Aliases);
    private GeographicRuleView View(GeographicReminderRule r) => new(r.Id, r.Text, r.PlaceIds, r.ObserverNodeId, r.Revision, r.Transition,
        r.Repeat, r.StartsAt, r.EndsAt, r.TimeZoneId, r.DeliveryPolicy, r.Status, r.SyncStatus, r.LeaseUntil, r.SyncedAt, r.ErrorCode);
    public async Task<IReadOnlyList<PlaceView>> ListAsync(CancellationToken ct = default) => (await Places.AsNoTracking().OrderBy(p => p.Name).ToArrayAsync(ct)).Select(p => View(p)).ToArray();
    public async Task<PlaceView> SaveAsync(Guid? id, PlaceWrite request, CancellationToken ct = default)
    {
        var affected = new HashSet<Guid>();
        var result = await Locked(async () => {
            var all = await Places.ToDictionaryAsync(p => p.Id, ct);
            var incomingCircleIds = request.Circles.Select(c => c.Id).ToHashSet();
            if (all.Values.Any(p => p.Id != id && p.Circles.Any(c => incomingCircleIds.Contains(c.Id)))) throw new NodeException("circle_id_conflict", "Uma área já pertence a outro lugar. Crie uma nova área ou use a hierarquia para herdar a existente.", 409);
            if (all.Count >= 500 && id is null) throw new NodeException("place_limit", "Limite de 500 lugares atingido.");
            if (request.ParentId is { } parent && !all.ContainsKey(parent)) throw new NodeException("place_not_found", "O lugar pai não existe.", 404);
            Place item;
            if (id is { } target) {
                if (!all.TryGetValue(target, out item!)) throw new NodeException("place_not_found", "Lugar não encontrado.", 404);
                if (request.ExpectedRevision != item.Revision) throw new NodeException("revision_conflict", "O lugar mudou. Recarregue antes de salvar.", 409);
                if (PlaceGeometry.WouldCycle(target, request.ParentId, all.ToDictionary(p => p.Key, p => p.Value.ParentId))) throw new NodeException("place_cycle", "Um lugar não pode ser descendente dele mesmo.");
                item.Change(request.Name, request.Type, request.ParentId, request.Circles, request.Aliases, Now);
            } else { item = new(request.Name, request.Type, request.ParentId, request.Circles, request.Aliases, Now); Places.Add(item); all[item.Id] = item; }
            var changedPlaces = new HashSet<Guid> { item.Id };
            for (var pass = 0; pass < all.Count; pass++) {
                var added = false; foreach (var child in all.Values.Where(p => p.ParentId is { } parent && changedPlaces.Contains(parent))) added |= changedPlaces.Add(child.Id);
                if (!added) break;
            }
            foreach (var rule in await Rules.Where(r => r.Status == "active" || r.Status == "paused").ToArrayAsync(ct)) {
                if (!rule.PlaceIds.Any(changedPlaces.Contains)) continue;
                if (rule.PlaceIds.Any(placeId => all[placeId].Type == "room")) throw new NodeException("indoor_location_unsupported", "GPS não identifica entrada em cômodos. Cancele ou altere a regra antes de transformar seu lugar em cômodo.", 409);
                foreach (var placeId in rule.PlaceIds) if (PlaceGeometry.EffectiveCircles(all[placeId], all).Count == 0) throw new NodeException("place_in_use", "Uma regra ativa precisa da área deste lugar. Pause/cancele ou defina outra área antes.", 409);
                rule.InvalidateGeometry(Now); affected.Add(rule.ObserverNodeId);
            }
            foreach (var observer in affected) {
                var active = await Rules.Where(r => r.ObserverNodeId == observer && r.Status == "active").ToArrayAsync(ct);
                if (active.SelectMany(r => r.PlaceIds).Distinct().SelectMany(p => PlaceGeometry.EffectiveCircles(all[p], all)).DistinctBy(c => c.Id).Count() > 100)
                    throw new NodeException("geofence_region_limit", "Esta edição ultrapassa 100 regiões no celular. Reduza áreas ou pause regras antes de salvar.", 409);
            }
            return View(item);
        }, ct);
        foreach (var observer in affected) await Signal(observer, "geofence.monitor", new { operation = "sync" }, ct);
        return result;
    }
    public async Task DeleteAsync(Guid id, int expectedRevision, CancellationToken ct = default) => await Locked(async () => {
        var place = await Places.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new NodeException("place_not_found", "Lugar não encontrado.", 404);
        if (place.Revision != expectedRevision) throw new NodeException("revision_conflict", "O lugar mudou. Recarregue antes de excluir.", 409);
        if (await Places.AnyAsync(p => p.ParentId == id, ct) || await Profiles.AnyAsync(p => p.FixedPlaceId == id, ct) ||
            (await Rules.Where(r => r.Status == "active" || r.Status == "paused").ToArrayAsync(ct)).Any(r => r.PlaceIds.Contains(id)))
            throw new NodeException("place_in_use", "O lugar possui filhos, dispositivo fixo ou lembrete ativo. Atualize essas referências antes de excluir.", 409);
        foreach (var profile in await Profiles.Where(p => p.ResolvedPlaceId == id).ToArrayAsync(ct)) {
            // FK SetNull applies in PostgreSQL; no location history is copied.
            db.Entry(profile).Property(p => p.ResolvedPlaceId).CurrentValue = null;
        }
        Places.Remove(place); return true;
    }, ct);
    private LocationObservationView? Observation(NodeLocationProfile? p)
    {
        if (p?.Latitude is not { } latitude || p.Longitude is not { } longitude || p.CapturedAt is not { } captured || p.ReceivedAt is not { } received) return null;
        return new(p.NodeId, latitude, longitude, p.AccuracyMeters!.Value, captured, received, p.Source!, p.ResolvedPlaceId, Math.Max(0, (Now - captured).TotalSeconds));
    }
    public async Task<LocationSettingsView> SettingsAsync(CancellationToken ct = default)
    {
        var profiles = await Profiles.AsNoTracking().ToArrayAsync(ct); var source = profiles.SingleOrDefault(p => p.IsUserSource);
        return new(source?.NodeId, profiles.Select(p => new LocationProfileView(p.NodeId, p.Mode, p.FixedPlaceId)).ToArray(), Observation(source));
    }
    public Task<LocationSettingsView> ConfigureAsync(LocationSettingsWrite request, CancellationToken ct = default) => Locked(async () => {
        if (request.SourceNodeId is { } selected) {
            var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == selected && n.Enabled && n.RevokedAt == null, ct);
            if (node?.Platform != NodePlatform.Android) throw new NodeException("invalid_location_source", "Escolha um celular Android habilitado.");
            foreach (var old in await Profiles.Where(p => p.IsUserSource).ToArrayAsync(ct)) old.Select(false);
            await db.SaveChangesAsync(ct); // release unique source slot before selecting the new one
            var profile = await Profiles.SingleOrDefaultAsync(p => p.NodeId == selected, ct);
            if (profile is null) { profile = new(selected); Profiles.Add(profile); }
            profile.Select(true);
        }
        if (request.NodeId is { } id) {
            var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == id && n.RevokedAt == null, ct) ?? throw new NodeException("node_not_found", "Dispositivo não encontrado.", 404);
            if (request.Mode == "dynamic" && node.Platform != NodePlatform.Android) throw new NodeException("unsupported_location", "Localização dinâmica está disponível no Android.");
            if (request.FixedPlaceId is { } place && !await Places.AnyAsync(p => p.Id == place, ct)) throw new NodeException("place_not_found", "Lugar não encontrado.", 404);
            if (request.Mode != "dynamic" && await Rules.AnyAsync(r => r.ObserverNodeId == id && r.Status == "active", ct)) throw new NodeException("location_in_use", "Pause os lembretes geográficos deste celular antes de mudar o modo.", 409);
            var profile = Profiles.Local.SingleOrDefault(p => p.NodeId == id) ?? await Profiles.SingleOrDefaultAsync(p => p.NodeId == id, ct);
            if (profile is null) { profile = new(id); Profiles.Add(profile); }
            profile.Configure(request.Mode ?? "none", request.FixedPlaceId);
        }
        await db.SaveChangesAsync(ct); return await SettingsAsync(ct);
    }, ct);
    public async Task ObserveAsync(Guid actor, LocationObservationWrite request, CancellationToken ct = default) => await Locked(async () => {
        await RequireObserver(actor, ct);
        var profile = await Profiles.SingleOrDefaultAsync(p => p.NodeId == actor, ct) ?? throw new NodeException("location_not_configured", "Ative localização dinâmica neste celular.", 409);
        var places = await Places.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        var containing = places.Values.Where(p => p.Type != "room" && PlaceGeometry.Classify(PlaceGeometry.EffectiveCircles(p, places), request.Latitude, request.Longitude, request.AccuracyMeters) == "inside").ToArray();
        profile.Observe(request.Latitude, request.Longitude, request.AccuracyMeters, request.CapturedAt, request.Source, containing.Length == 1 ? containing[0].Id : null, Now, request.RequestId);
        return true;
    }, ct);
    private async Task<string> Signal(Guid node, string capability, object input, CancellationToken ct)
    {
        if (signals is null) return "unavailable";
        try { return await signals.SendAsync(node, capability, input, ct); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { return "pending"; }
    }
    public async Task<object> RefreshAsync(CancellationToken ct = default)
    {
        var settings = await SettingsAsync(ct);
        if (settings.SourceNodeId is not { } node) return new { status = "source_required", latest = settings.Latest };
        var requestId = Guid.NewGuid(); var status = await Signal(node, "location.current", new { operation = "refresh", requestId }, ct);
        var deadline = clock.GetUtcNow().AddSeconds(contextOptions.Value.RefreshTimeoutSeconds);
        // A bounded, explicit consumer request, not an idle poll. DB is read at most 15 times.
        if (status is "success" or "accepted") for (var i = 0; i < contextOptions.Value.RefreshTimeoutSeconds && clock.GetUtcNow() < deadline; i++) {
            var current = await Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.NodeId == node, ct);
            if (current?.LastRequestId == requestId) return new { status = "updated", latest = Observation(current) };
            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
        }
        return new { status = status == "unavailable" ? "source_unreachable" : "pending", latest = (await SettingsAsync(ct)).Latest };
    }
    public async Task<object> RequestGeofenceSyncAsync(CancellationToken ct = default)
    {
        var source = (await SettingsAsync(ct)).SourceNodeId;
        if (source is null) return new { status = "source_required" };
        var status = await Signal(source.Value, "geofence.monitor", new { operation = "sync" }, ct);
        return new { status = status is "success" or "accepted" ? "pending_confirmation" : "source_unreachable" };
    }
    public async Task<PlaceResolution> ResolveAsync(string reference, bool allCandidates, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 100) throw new NodeException("invalid_place_reference", "Informe um nome ou apelido de lugar.");
        var all = await Places.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        var named = all.Values.Where(p => string.Equals(p.Name, reference.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 1) return new("resolved", [View(named[0], PlaceGeometry.EffectiveCircles(named[0], all))], named[0].Id, "explicit_name", null);
        var candidates = (named.Length > 0 ? named : all.Values.Where(p => p.Aliases.Contains(reference.Trim(), StringComparer.OrdinalIgnoreCase))).Select(p => View(p, PlaceGeometry.EffectiveCircles(p, all))).ToArray();
        if (candidates.Length == 0) return new("not_found", [], null, "place_required", null);
        if (allCandidates) return new("resolved_set", candidates, null, "explicit_set", null);
        if (candidates.Length == 1) return new("resolved", candidates, candidates[0].Id, "unique_alias", null);
        var latest = (await SettingsAsync(ct)).Latest;
        var resolved = PlaceGeometry.ResolveContext(candidates, latest, contextOptions.Value);
        if (resolved is null) {
            await RefreshAsync(ct); latest = (await SettingsAsync(ct)).Latest; resolved = PlaceGeometry.ResolveContext(candidates, latest, contextOptions.Value);
        }
        return new(resolved is null ? "clarification_required" : "resolved", candidates, resolved, resolved is null ? "ambiguous_or_stale" : "recent_personal_phone_context", latest?.AgeSeconds);
    }
    public async Task<IReadOnlyList<GeographicRuleView>> RulesAsync(CancellationToken ct = default) => (await Rules.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(500).ToArrayAsync(ct)).Select(r => {
        var view = View(r);
        return r.Status == "active" && (r.LeaseUntil <= Now || r.SyncedAt is { } synced && synced.AddDays(7) <= Now)
            ? view with { SyncStatus = "recoverable_error", ErrorCode = "local_lease_expired" } : view;
    }).ToArray();
    public async Task<GeographicRuleView> SaveRuleAsync(Guid? id, GeographicRuleWrite request, Guid? conversationId = null, CancellationToken ct = default)
    {
        var result = await Locked(async () => {
            await RequireRuleObserver(request.ObserverNodeId, ct);
            var places = await Places.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
            RequireRulePlaces(request.PlaceIds, places);
            GeographicReminderRule rule;
            if (id is { } target) {
                rule = await Rules.SingleOrDefaultAsync(r => r.Id == target, ct) ?? throw new NodeException("geofence_not_found", "Lembrete por lugar não encontrado.", 404);
                if (request.ExpectedRevision != rule.Revision) throw new NodeException("revision_conflict", "O lembrete mudou. Recarregue antes de salvar.", 409);
                if (rule.Status is "cancelled" or "completed") throw new NodeException("rule_finished", "Esta regra já foi encerrada.", 409);
                if (rule.ObserverNodeId != request.ObserverNodeId) throw new NodeException("observer_change_requires_new_rule", "Cancele a regra e crie outra no novo celular para evitar cópias offline.", 409);
                rule.Change(request.Text, request.PlaceIds, request.ObserverNodeId, request.Transition, request.Repeat, request.StartsAt, request.EndsAt, request.TimeZoneId, Now);
            } else { rule = new(request.Text, request.PlaceIds, request.ObserverNodeId, request.Transition, request.Repeat, request.StartsAt, request.EndsAt, request.TimeZoneId, Now, conversationId); Rules.Add(rule); }
            await RequireRuleCapacity(rule, places, ct);
            return View(rule);
        }, ct);
        await Signal(result.ObserverNodeId, "geofence.monitor", new { operation = "sync" }, ct); return result;
    }
    public async Task<GeographicRuleView> ActOnRuleAsync(Guid id, int revision, string action, CancellationToken ct = default)
    {
        var result = await Locked(async () => {
            var rule = await Rules.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new NodeException("geofence_not_found", "Lembrete por lugar não encontrado.", 404);
            if (revision != rule.Revision) throw new NodeException("revision_conflict", "O lembrete mudou. Recarregue antes de alterar.", 409);
            if (action == "resume") {
                if (rule.Status is "cancelled" or "completed") throw new NodeException("rule_finished", "Esta regra já foi encerrada.", 409);
                if (rule.EndsAt <= Now) throw new NodeException("rule_expired", "A validade deste lembrete terminou. Crie outro ou edite a validade antes de retomar.", 409);
                await RequireRuleObserver(rule.ObserverNodeId, ct);
                var places = await Places.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
                RequireRulePlaces(rule.PlaceIds, places);
                await RequireRuleCapacity(rule, places, ct);
            }
            rule.SetStatus(action, Now); return View(rule);
        }, ct);
        await Signal(result.ObserverNodeId, "geofence.monitor", new { operation = "sync" }, ct); return result;
    }
    private async Task RequireRuleObserver(Guid observerId, CancellationToken ct)
    {
        var observer = await db.Nodes.SingleOrDefaultAsync(n => n.Id == observerId && n.Enabled && n.RevokedAt == null && n.Platform == NodePlatform.Android, ct);
        var settings = await Profiles.SingleOrDefaultAsync(p => p.NodeId == observerId, ct);
        if (observer is null || !observer.ReceiveReminders || settings?.Mode != "dynamic") throw new NodeException("location_source_required", "Escolha e configure o celular com localização dinâmica e recebimento de lembretes ativado.", 409);
        if (!await db.NodeCapabilities.AnyAsync(c => c.NodeId == observerId && c.Name == "geofence.monitor" && c.Version >= 1, ct)) throw new NodeException("geofence_unsupported", "Atualize/abra o celular para registrar suporte a lembretes por lugar.", 409);
    }
    private static void RequireRulePlaces(IReadOnlyList<Guid>? ids, IReadOnlyDictionary<Guid, Place> places)
    {
        if (ids is null || ids.Any(p => !places.ContainsKey(p))) throw new NodeException("place_not_found", "Resolva o lugar antes de criar o lembrete.");
        if (ids.Any(p => places[p].Type == "room")) throw new NodeException("indoor_location_unsupported", "GPS não identifica entrada em cômodos. Escolha a propriedade ou área externa.");
        if (ids.Any(p => PlaceGeometry.EffectiveCircles(places[p], places).Count == 0)) throw new NodeException("place_area_required", "Confirme a área do lugar no mapa antes de ativar o lembrete.");
    }
    private async Task RequireRuleCapacity(GeographicReminderRule rule, IReadOnlyDictionary<Guid, Place> places, CancellationToken ct)
    {
        var active = (await Rules.Where(r => r.ObserverNodeId == rule.ObserverNodeId && r.Status == "active").ToArrayAsync(ct)).Append(rule).DistinctBy(r => r.Id).ToArray();
        if (active.Length > 100) throw new NodeException("geofence_rule_limit", "Limite de 100 lembretes geográficos ativos por celular.", 409);
        var regionCount = active.SelectMany(r => r.PlaceIds).Distinct().SelectMany(p => PlaceGeometry.EffectiveCircles(places[p], places)).DistinctBy(c => c.Id).Count();
        if (regionCount > 100) throw new NodeException("geofence_region_limit", "As regras deste celular ultrapassam 100 regiões. Reduza áreas ou pause regras.", 409);
    }
    private static string ManifestRevision(IEnumerable<GeographicReminderRule> rules, IEnumerable<Place> places) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join(';', rules.OrderBy(r => r.Id).Select(r => $"{r.Id}:{r.Revision}:{r.Status}")) + "/" + string.Join(';', places.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.Revision}"))))).ToLowerInvariant();
    public Task<object> ManifestAsync(Guid actor, CancellationToken ct = default) => Locked<object>(async () => {
        await RequireObserver(actor, ct);
        var rules = await Rules.Where(r => r.ObserverNodeId == actor && (r.Status == "active" || r.Status == "paused") && (r.EndsAt == null || r.EndsAt > Now)).ToArrayAsync(ct);
        var places = await Places.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        foreach (var rule in rules) rule.Renew(Now);
        var referenced = rules.SelectMany(r => r.PlaceIds).Distinct().Where(places.ContainsKey).Select(p => places[p]).ToArray();
        return new { revision = ManifestRevision(rules, referenced), generatedAt = Now, leaseUntil = Now.AddDays(7), observerNodeId = actor,
            places = referenced.Select(p => View(p, PlaceGeometry.EffectiveCircles(p, places))), rules = rules.Select(View) };
    }, ct);
    public Task<object> ConfirmAsync(Guid actor, GeographicSyncWrite request, CancellationToken ct = default) => Locked<object>(async () => {
        await RequireObserver(actor, ct);
        if (request.Rules is null || request.Rules.Count > 500 || request.Rules.Select(r => r.Id).Distinct().Count() != request.Rules.Count) throw new NodeException("invalid_manifest_ack", "Confirmação inválida.");
        var rules = await Rules.Where(r => r.ObserverNodeId == actor).ToArrayAsync(ct);
        var current = rules.Where(r => (r.Status == "active" || r.Status == "paused") && (r.EndsAt == null || r.EndsAt > Now)).ToArray();
        var ids = current.SelectMany(r => r.PlaceIds).Distinct().ToArray();
        var referenced = await Places.AsNoTracking().Where(p => ids.Contains(p.Id)).ToArrayAsync(ct);
        if (request.ManifestRevision != ManifestRevision(current, referenced)) return new { synchronized = false, status = "stale_manifest" };
        foreach (var ack in request.Rules) {
            var rule = rules.SingleOrDefault(r => r.Id == ack.Id);
            if (rule is null) throw new NodeException("invalid_manifest_ack", "Regra não pertence ao dispositivo.", 403);
            rule.Confirm(ack.Revision, ack.Status, ack.ErrorCode, Now);
        }
        // A full replacement manifest confirms removal of cancelled rules omitted locally.
        foreach (var rule in rules.Where(r => r.Status == "cancelled" && !request.Rules.Any(a => a.Id == r.Id))) rule.Confirm(rule.Revision, "completed", null, Now);
        return new { synchronized = true };
    }, ct);
    public Task<object> EventAsync(Guid actor, GeographicEventWrite request, CancellationToken ct = default) => Locked<object>(async () => {
        await RequireObserver(actor, ct);
        if (request.EventId == Guid.Empty || request.OccurrenceId == Guid.Empty || request.Sequence <= 0 || request.PostedAt > Now.AddMinutes(2) || request.PostedAt < request.OccurredAt) throw new NodeException("invalid_geofence_event", "Evento inválido.");
        var existing = await Events.SingleOrDefaultAsync(e => e.Id == request.EventId, ct);
        if (existing is not null) {
            if (existing.ObserverNodeId != actor || existing.OccurrenceId != request.OccurrenceId || existing.RuleId != request.RuleId) throw new NodeException("event_conflict", "Identificador de evento já utilizado.", 409);
            return new { status = existing.Result, occurrenceId = existing.OccurrenceId, duplicate = true };
        }
        if (await Events.AnyAsync(e => e.OccurrenceId == request.OccurrenceId || e.RuleId == request.RuleId && e.Sequence == request.Sequence, ct) || await db.Reminders.AnyAsync(r => r.Id == request.OccurrenceId, ct)) throw new NodeException("event_conflict", "Ocorrência já utilizada.", 409);
        var rule = await Rules.SingleOrDefaultAsync(r => r.Id == request.RuleId && r.ObserverNodeId == actor, ct) ?? throw new NodeException("geofence_not_found", "Regra não pertence ao dispositivo.", 403);
        var accepted = rule.AcceptEvent(request.RuleRevision, request.Transition, request.Sequence, request.OccurredAt, Now);
        var result = !accepted ? "superseded" : request.OccurredAt.AddMinutes(5) <= Now ? "expired" : "accepted";
        Events.Add(new(request.EventId, request.OccurrenceId, request.RuleId, actor, request.RuleRevision, request.Sequence, request.OccurredAt, request.PostedAt, result, Now));
        if (accepted) {
            var reminder = Reminder.CreateGeographic(request.OccurrenceId, request.EventId, actor, rule.Text, request.OccurredAt, rule.TimeZoneId, Now, rule.DeliveryPolicy, request.PostedAt);
            db.Reminders.Add(reminder);
            if (request.PostedAt is { } posted) db.Set<ReminderRecipient>().Add(ReminderRecipient.LocalPosted(reminder.Id, reminder.Generation, actor, posted));
        }
        return new { status = result, occurrenceId = request.OccurrenceId, duplicate = false };
    }, ct);
    public async Task RememberAsync(Guid conversationId, string scope, IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        foreach (var id in ids.Distinct()) db.ToolContextEntries.Add(new(conversationId, scope, "reference", id.ToString(), "{}", scope + "_list", Now.AddMinutes(30), Now));
        await db.SaveChangesAsync(ct);
    }
    public async Task RequireObservedAsync(Guid conversationId, string scope, IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        foreach (var id in ids.Distinct()) { var key = id.ToString(); if (!await db.ToolContextEntries.AnyAsync(e => e.ConversationId == conversationId && e.Scope == scope && e.Key == key && e.ExpiresAt > Now && e.ReplacedAt == null, ct)) throw new NodeException("unobserved_reference", "Consulte os lugares/lembretes antes de usar essa referência."); }
    }
}
