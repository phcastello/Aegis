using Aegis.Domain.Entities;
using Microsoft.EntityFrameworkCore;
namespace Aegis.Infrastructure.Reminders;

public static class ReminderCompletionModelConfiguration
{
    public static void ConfigureReminderCompletion(this ModelBuilder model)
    {
        model.Entity<Reminder>(e => {
            e.Property(x => x.Revision).HasDefaultValue(1); e.Property(x => x.Generation).HasDefaultValue(1);
            e.Property(x => x.DeliveryPolicy).HasMaxLength(20).HasDefaultValue("allEligible");
            e.Property(x => x.OccurrenceKey).HasMaxLength(40);
            e.HasIndex(x => new { x.SeriesId, x.OccurrenceKey }).IsUnique().HasFilter("\"SeriesId\" IS NOT NULL");
            e.HasIndex(x => x.SourceEventId).IsUnique().HasFilter("\"SourceEventId\" IS NOT NULL");
            e.HasOne<ReminderSeries>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ReminderSeries>(e => {
            e.ToTable("reminder_series"); e.HasKey(x => x.Id);
            e.Property(x => x.Text).HasMaxLength(600); e.Property(x => x.TimeZoneId).HasMaxLength(100);
            e.Property(x => x.RuleJson).HasMaxLength(2000); e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.DeliveryPolicy).HasMaxLength(20); e.HasIndex(x => x.Status);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.SourceConversationId).OnDelete(DeleteBehavior.SetNull);
        });
        model.Entity<ReminderRecipient>(e => {
            e.ToTable("reminder_recipients"); e.HasKey(x => x.Id); e.Property(x => x.Status).HasMaxLength(20); e.Property(x => x.Transport).HasMaxLength(30);
            e.Property(x => x.WithdrawalAttempts).HasDefaultValue(0);
            e.HasIndex(x => new { x.ReminderId, x.Generation, x.NodeId }).IsUnique();
            e.HasOne<Reminder>().WithMany().HasForeignKey(x => x.ReminderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AegisNode>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.SetNull);
        });
        model.Entity<ReminderActionReceipt>(e => {
            e.ToTable("reminder_action_receipts"); e.HasKey(x => x.Id); e.Property(x => x.Action).HasMaxLength(20);
            e.Property(x => x.ResponseJson).HasMaxLength(8192); e.HasIndex(x => x.ReminderId);
            e.HasOne<Reminder>().WithMany().HasForeignKey(x => x.ReminderId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ReminderDeliveryAttempt>(e => {
            e.Property(x => x.Generation).HasDefaultValue(1);
            e.HasIndex(x => new { x.ReminderId, x.Attempt }).IsUnique(false);
            e.HasIndex(x => new { x.ReminderId, x.Generation, x.NodeId, x.Attempt }).IsUnique();
            e.HasOne<AegisNode>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
