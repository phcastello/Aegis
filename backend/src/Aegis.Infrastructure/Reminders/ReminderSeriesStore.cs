using System.Text.Json;
using Aegis.Application.Reminders;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Aegis.Infrastructure.Reminders;

public sealed class ReminderSeriesStore(AegisDbContext db, TimeProvider clock) : IReminderSeriesStore
{
    public async Task AddAsync(ReminderSeries series, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        db.Set<ReminderSeries>().Add(series); await Materialize(series, ct); await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<ReminderSeries>> ListAsync(CancellationToken ct) => (await PageAsync(0, ct)).Series;
    public async Task<ReminderSeriesPage> PageAsync(int offset, CancellationToken ct)
    {
        if (offset is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(offset));
        var rows = await db.Set<ReminderSeries>().AsNoTracking().Where(s => s.Status != "cancelled")
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).Skip(offset).Take(101).ToArrayAsync(ct);
        var hasMore = rows.Length > 100; var page = rows.Take(100).ToArray(); var ids = page.Select(s => s.Id).ToArray();
        var upcoming = await db.Reminders.AsNoTracking().Where(r => r.SeriesId != null && ids.Contains(r.SeriesId.Value) && r.AcknowledgedAt == null &&
                r.TriggeredAt == null && (r.Status == ReminderStatus.Scheduled || r.Status == ReminderStatus.Processing))
            .GroupBy(r => r.SeriesId!.Value).Select(g => new { Id = g.Key, DueAt = g.Min(r => r.DueAtUtc) }).ToDictionaryAsync(r => r.Id, r => r.DueAt, ct);
        return new(page, hasMore, hasMore ? offset + 100 : null, upcoming);
    }
    private async Task<ReminderSeries> LoadLocked(Guid id, CancellationToken ct)
    {
        var item = db.Database.IsNpgsql() ? (await db.Set<ReminderSeries>().FromSqlInterpolated($"SELECT * FROM reminder_series WHERE \"Id\" = {id} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault() : await db.Set<ReminderSeries>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        if (item is null) throw new ReminderException("series_not_found", "Série não encontrada.");
        var existing = db.ChangeTracker.Entries<ReminderSeries>().FirstOrDefault(e => e.Entity.Id == id); if (existing is not null) existing.State = EntityState.Detached;
        db.Attach(item); return item;
    }
    public async Task<ReminderSeries> ChangeAsync(Guid id, int revision, string operation, string? text, ReminderRecurrenceRule? rule, string? policy, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var series = await LoadLocked(id, ct); series.CheckRevision(revision); var now = clock.GetUtcNow();
        var future = await db.Reminders.Where(r => r.SeriesId == id && r.Status == ReminderStatus.Scheduled && r.Generation == 1 && r.TriggeredAt == null).OrderBy(r => r.DueAtUtc).ToListAsync(ct);
        if (operation == "skip")
        {
            if (future.Count == 0) throw new ArgumentException("Não há próxima ocorrência para pular.");
            future[0].CancelFutureForSeries(now, exception: true); series.MarkSkipped(now);
        }
        else
        {
            if (operation == "update") series.Change(revision, text, rule, policy, now);
            else series.SetState(revision, operation switch { "pause" => "paused", "resume" => "active", "cancel" => "cancelled", _ => throw new ArgumentException("Operação inválida.") }, now);
            foreach (var occurrence in future) occurrence.CancelFutureForSeries(now);
        }
        await db.SaveChangesAsync(ct); await Materialize(series, ct); await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct); return series;
    }
    private async Task<bool> Materialize(ReminderSeries series, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); if (series.Status != "active") return false;
        if (await db.Reminders.AnyAsync(r => r.SeriesId == series.Id && r.Status == ReminderStatus.Scheduled && r.Generation == 1 && !r.IsException && r.DueAtUtc > now, ct)) return false;
        var next = series.Next(now);
        // A completed calendar date or explicit occurrence exception survives editing the series.
        // Cancelled replacements created by a series edit are not individual exceptions.
        var skipped = 0;
        for (; next is not null && skipped < 32; skipped++)
        {
            var dateKey = ":" + next.LocalKey[..10] + "T";
            if (!await db.Reminders.AnyAsync(r => r.SeriesId == series.Id && r.OccurrenceKey != null && r.OccurrenceKey.Contains(dateKey) && (r.TriggeredAt != null || r.Generation > 1 || r.IsException), ct)) break;
            series.Materialized(next, now); next = series.Next(now);
        }
        if (skipped == 32) return false;
        if (next is null) { series.Complete(now); return false; }
        if (!await db.Reminders.AnyAsync(r => r.SeriesId == series.Id && r.OccurrenceKey == "r" + series.Revision + ":" + next.LocalKey, ct)) db.Reminders.Add(Reminder.ForSeries(series, next, now));
        series.Materialized(next, now); return true;
    }
    public async Task MaterializePendingAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Apply the bounded batch to work that is actually pending. Otherwise
        // the oldest series with future occurrences can permanently occupy it.
        // Snoozed presentations and individually changed occurrences do not
        // replace the remaining normal series schedule.
        var ids = await db.Set<ReminderSeries>().AsNoTracking().Where(s => s.Status == "active" &&
            !db.Reminders.Any(r => r.SeriesId == s.Id && r.Status == ReminderStatus.Scheduled && r.Generation == 1 && !r.IsException && r.DueAtUtc > now))
            .OrderBy(s => s.UpdatedAt).ThenBy(s => s.Id).Select(s => s.Id).Take(100).ToArrayAsync(ct);
        foreach (var id in ids)
        {
            await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            var series = await LoadLocked(id, ct); await Materialize(series, ct); await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }
    }
    public async Task RememberAsync(Guid conversationId, IReadOnlyList<ReminderSeries> series, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var s in series)
        {
            var key = s.Id.ToString(); var old = await db.ToolContextEntries.Where(e => e.ConversationId == conversationId && e.Scope == "reminder_series" && e.Key == key && e.ReplacedAt == null).ToListAsync(ct);
            foreach (var e in old) e.Replace(now);
            db.ToolContextEntries.Add(new ToolContextEntry(conversationId, "reminder_series", "series_reference", key, JsonSerializer.Serialize(ReminderSeriesService.View(s), ReminderRecurrenceRule.JsonOptions), "reminder_series_list", now.AddMinutes(30), now));
        }
        await db.SaveChangesAsync(ct);
    }
    public Task<bool> WasObservedAsync(Guid conversationId, Guid seriesId, DateTimeOffset now, CancellationToken ct) => db.ToolContextEntries.AnyAsync(e => e.ConversationId == conversationId && e.Scope == "reminder_series" && e.Key == seriesId.ToString() && e.ReplacedAt == null && e.ExpiresAt > now, ct);
}
