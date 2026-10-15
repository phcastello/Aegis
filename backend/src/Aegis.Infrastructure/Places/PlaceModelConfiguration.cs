using Aegis.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Places;

public static class PlaceModelConfiguration
{
    public static void ConfigurePlaces(this ModelBuilder model)
    {
        model.Entity<Place>(e => {
            e.ToTable("places"); e.HasKey(p => p.Id); e.Property(p => p.Name).HasMaxLength(100); e.Property(p => p.Type).HasMaxLength(16);
            e.Property(p => p.CirclesJson).HasColumnType("jsonb"); e.Property(p => p.AliasesJson).HasColumnType("jsonb");
            e.Ignore(p => p.Circles); e.Ignore(p => p.Aliases);
            e.HasOne<Place>().WithMany().HasForeignKey(p => p.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(p => p.Revision).IsConcurrencyToken();
        });
        model.Entity<NodeLocationProfile>(e => {
            e.ToTable("node_location_profiles"); e.HasKey(p => p.NodeId); e.Property(p => p.Mode).HasMaxLength(16); e.Property(p => p.Source).HasMaxLength(16);
            e.HasOne<AegisNode>().WithOne().HasForeignKey<NodeLocationProfile>(p => p.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Place>().WithMany().HasForeignKey(p => p.FixedPlaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Place>().WithMany().HasForeignKey(p => p.ResolvedPlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(p => p.IsUserSource).IsUnique().HasFilter("\"IsUserSource\" = TRUE");
        });
        model.Entity<GeographicReminderRule>(e => {
            e.ToTable("geographic_reminder_rules"); e.HasKey(r => r.Id); e.Ignore(r => r.PlaceIds);
            e.Property(r => r.PlaceIdsJson).HasColumnType("jsonb"); e.Property(r => r.Text).HasMaxLength(600);
            e.Property(r => r.Transition).HasMaxLength(8); e.Property(r => r.Status).HasMaxLength(16); e.Property(r => r.SyncStatus).HasMaxLength(32);
            e.Property(r => r.TimeZoneId).HasMaxLength(100); e.Property(r => r.DeliveryPolicy).HasMaxLength(20); e.Property(r => r.ErrorCode).HasMaxLength(64);
            e.Property(r => r.Revision).IsConcurrencyToken(); e.HasIndex(r => new { r.ObserverNodeId, r.Status });
            // Observer ID is a non-reusable identity reference. Removal cancels the rule,
            // retaining only the ID in its historical definition, never credentials.
            e.HasOne<Conversation>().WithMany().HasForeignKey(r => r.SourceConversationId).OnDelete(DeleteBehavior.SetNull);
        });
        model.Entity<GeographicReminderEvent>(e => {
            e.ToTable("geographic_reminder_events"); e.HasKey(x => x.Id); e.HasIndex(x => x.OccurrenceId).IsUnique();
            e.HasIndex(x => new { x.RuleId, x.Sequence }).IsUnique(); e.Property(x => x.Result).HasMaxLength(20);
            e.HasOne<GeographicReminderRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AegisNode>().WithMany().HasForeignKey(x => x.ObserverNodeId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
