using Aegis.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Persistence;

internal static class MemoryModelConfiguration
{
    public static void ConfigureMemory(this ModelBuilder model)
    {
        model.Entity<MemoryRecord>(e =>
        {
            e.ToTable("memory_records", t =>
            {
                t.HasCheckConstraint("CK_memory_content", "length(btrim(\"Content\")) > 0");
                t.HasCheckConstraint("CK_memory_revision", "\"Revision\" >= 1");
                t.HasCheckConstraint("CK_memory_validity", "\"ValidFrom\" IS NULL OR \"ValidUntil\" IS NULL OR \"ValidUntil\" > \"ValidFrom\"");
                t.HasCheckConstraint("CK_memory_supersession", "\"SupersededById\" IS NULL OR \"SupersededById\" <> \"Id\"");
                t.HasCheckConstraint("CK_memory_status", "\"Status\" IN ('Active','Superseded','Forgotten')");
                t.HasCheckConstraint("CK_memory_lifecycle", "(\"Status\" = 'Active' AND \"SupersededAt\" IS NULL AND \"SupersededById\" IS NULL AND \"ForgottenAt\" IS NULL) OR (\"Status\" = 'Superseded' AND \"SupersededAt\" IS NOT NULL AND \"SupersededById\" IS NOT NULL AND \"ForgottenAt\" IS NULL) OR (\"Status\" = 'Forgotten' AND \"ForgottenAt\" IS NOT NULL AND \"SupersededAt\" IS NULL AND \"SupersededById\" IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Content).HasMaxLength(MemoryText.MaxContentLength).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.HasOne<MemoryRecord>().WithMany().HasForeignKey(x => x.SupersededById).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ContentHash).HasFilter("\"Status\" = 'Active'");
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });
        model.Entity<MemoryEvidence>(e =>
        {
            e.ToTable("memory_evidence", t => t.HasCheckConstraint("CK_memory_source_kind", "\"SourceKind\" IN ('ExplicitMemoryRequest','UserStatement','ToolObservation','Inference')")); e.HasKey(x => x.Id);
            e.Property(x => x.SourceKind).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.HasOne<MemoryRecord>().WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.SourceConversationId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.SourceMessageId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.MemoryId);
            e.HasIndex(x => new { x.MemoryId, x.SourceKind, x.SourceMessageId }).IsUnique().HasFilter("\"SourceMessageId\" IS NOT NULL");
        });
        model.Entity<MemoryEntity>(e =>
        {
            e.ToTable("memory_entities", t => { t.HasCheckConstraint("CK_memory_entity_name", "length(btrim(\"CanonicalName\")) > 0 AND length(btrim(\"NormalizedName\")) > 0"); t.HasCheckConstraint("CK_memory_entity_revision", "\"Revision\" >= 1"); });
            e.HasKey(x => x.Id);
            e.Property(x => x.CanonicalName).HasMaxLength(200).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(40);
            e.HasIndex(x => new { x.NormalizedName, x.EntityType });
        });
        model.Entity<MemoryEntityAlias>(e =>
        {
            e.ToTable("memory_entity_aliases", t => t.HasCheckConstraint("CK_memory_alias_name", "length(btrim(\"Alias\")) > 0 AND length(btrim(\"NormalizedAlias\")) > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Alias).HasMaxLength(200).IsRequired();
            e.Property(x => x.NormalizedAlias).HasMaxLength(200).IsRequired();
            e.HasOne<MemoryEntity>().WithMany().HasForeignKey(x => x.EntityId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.EntityId, x.NormalizedAlias }).IsUnique();
            e.HasIndex(x => x.NormalizedAlias);
        });
        model.Entity<MemoryRelation>(e =>
        {
            e.ToTable("memory_relations", t =>
            {
                t.HasCheckConstraint("CK_memory_relation_predicate", "\"Predicate\" ~ '^[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$'");
                t.HasCheckConstraint("CK_memory_relation_revision", "\"Revision\" >= 1");
                t.HasCheckConstraint("CK_memory_relation_validity", "\"ValidFrom\" IS NULL OR \"ValidUntil\" IS NULL OR \"ValidUntil\" > \"ValidFrom\"");
                t.HasCheckConstraint("CK_memory_relation_supersession", "\"SupersededById\" IS NULL OR \"SupersededById\" <> \"Id\"");
                t.HasCheckConstraint("CK_memory_relation_status", "\"Status\" IN ('Active','Superseded','Forgotten')");
                t.HasCheckConstraint("CK_memory_relation_lifecycle", "(\"Status\" = 'Active' AND \"SupersededAt\" IS NULL AND \"SupersededById\" IS NULL AND \"ForgottenAt\" IS NULL) OR (\"Status\" = 'Superseded' AND \"SupersededAt\" IS NOT NULL AND \"SupersededById\" IS NOT NULL AND \"ForgottenAt\" IS NULL) OR (\"Status\" = 'Forgotten' AND \"ForgottenAt\" IS NOT NULL AND \"SupersededAt\" IS NULL AND \"SupersededById\" IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Predicate).HasMaxLength(80).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.HasOne<MemoryEntity>().WithMany().HasForeignKey(x => x.SubjectEntityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MemoryEntity>().WithMany().HasForeignKey(x => x.ObjectEntityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MemoryRelation>().WithMany().HasForeignKey(x => x.SupersededById).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SubjectEntityId, x.Predicate, x.ObjectEntityId }).HasFilter("\"Status\" = 'Active'");
        });
        model.Entity<MemoryRelationEvidence>(e =>
        {
            e.ToTable("memory_relation_evidence");
            e.HasKey(x => new { x.RelationId, x.MemoryId });
            e.HasOne<MemoryRelation>().WithMany().HasForeignKey(x => x.RelationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MemoryRecord>().WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<MemoryProjectionJob>(e =>
        {
            e.ToTable("memory_projection_jobs", t =>
            {
                t.HasCheckConstraint("CK_memory_job_revision", "\"AggregateRevision\" >= 1");
                t.HasCheckConstraint("CK_memory_job_attempt", "\"Attempt\" >= 0");
                t.HasCheckConstraint("CK_memory_job_target", "\"ProjectionTarget\" IN ('Semantic','Graph')");
                t.HasCheckConstraint("CK_memory_job_aggregate", "\"AggregateType\" IN ('MemoryRecord','MemoryEntity','MemoryRelation')");
                t.HasCheckConstraint("CK_memory_job_operation", "\"Operation\" IN ('Upsert','Delete')");
                t.HasCheckConstraint("CK_memory_job_status", "\"Status\" IN ('Pending','Processing','Completed','Failed')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.ProjectionTarget).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.AggregateType).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.Operation).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => new { x.ProjectionTarget, x.AggregateType, x.AggregateId, x.AggregateRevision, x.Operation }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt, x.CreatedAt });
        });
        model.Entity<MemoryExtractionJob>(e =>
        {
            e.ToTable("memory_extraction_jobs", t =>
            {
                t.HasCheckConstraint("CK_memory_extraction_status", "\"Status\" IN ('Pending','Processing','Completed','Failed','Suppressed')");
                t.HasCheckConstraint("CK_memory_extraction_attempt", "\"Attempt\" >= 0");
                t.HasCheckConstraint("CK_memory_extraction_counts", "\"CandidatesCount\" >= 0 AND \"CreatedCount\" >= 0 AND \"ReinforcedCount\" >= 0 AND \"CorrectedCount\" >= 0 AND \"TransitionedCount\" >= 0 AND \"GraphMutationsCount\" >= 0 AND \"SkippedCount\" >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(100);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.UserMessageId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.UserMessageId).IsUnique().HasFilter("\"UserMessageId\" IS NOT NULL");
            e.HasIndex(x => new { x.Status, x.NextAttemptAt, x.CreatedAt });
        });
    }
}
