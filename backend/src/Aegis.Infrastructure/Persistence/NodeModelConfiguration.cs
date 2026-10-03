using Aegis.Domain.Entities;
using Microsoft.EntityFrameworkCore;
namespace Aegis.Infrastructure.Persistence;

internal static class NodeModelConfiguration
{
    public static void ConfigureNodes(this ModelBuilder model)
    {
        model.Entity<AegisNode>(e =>
        {
            e.ToTable("aegis_nodes", t => { t.HasCheckConstraint("ck_node_platform", "\"Platform\" IN ('Android', 'Windows')");
                t.HasCheckConstraint("ck_node_protocol", "\"ProtocolVersion\" = 1");
                t.HasCheckConstraint("ck_node_name", "length(trim(\"Name\")) BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_node_revoked_disabled", "\"RevokedAt\" IS NULL OR NOT \"Enabled\""); });
            e.HasKey(n => n.Id); e.Property(n => n.Name).HasMaxLength(100).IsRequired();
            e.Property(n => n.Platform).HasConversion<string>().HasMaxLength(16);
            e.Property(n => n.AppVersion).HasMaxLength(80).IsRequired(); e.HasIndex(n => new { n.Enabled, n.RevokedAt });
        });
        model.Entity<NodeCredential>(e =>
        {
            e.ToTable("node_credentials", t => t.HasCheckConstraint("ck_node_credential_hash", "octet_length(\"SecretHash\") = 32"));
            e.HasKey(c => c.NodeId); e.Property(c => c.SecretHash).IsRequired();
            e.HasOne<AegisNode>().WithOne().HasForeignKey<NodeCredential>(c => c.NodeId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<NodePairingCode>(e =>
        {
            e.ToTable("node_pairing_codes", t => { t.HasCheckConstraint("ck_node_code_hash", "octet_length(\"CodeHash\") = 32");
                t.HasCheckConstraint("ck_node_code_attempts", "\"FailedAttempts\" BETWEEN 0 AND 5"); });
            e.HasKey(c => c.Id); e.Property(c => c.Selector).HasMaxLength(8).IsRequired(); e.Property(c => c.CodeHash).IsRequired();
            e.HasIndex(c => c.Selector).IsUnique(); e.HasIndex(c => c.ExpiresAt);
            e.HasOne<AegisNode>().WithMany().HasForeignKey(c => c.IssuerNodeId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<NodePairingAttempt>(e =>
        {
            e.ToTable("node_pairing_attempts", t => { t.HasCheckConstraint("ck_node_attempt_platform", "\"Platform\" IN ('Android', 'Windows')");
                t.HasCheckConstraint("ck_node_attempt_hashes", "octet_length(\"RecoveryHash\") = 32 AND octet_length(\"CredentialHash\") = 32"); });
            e.HasKey(a => a.Id); e.HasIndex(a => a.NodeId).IsUnique(); e.HasIndex(a => a.PairingCodeId).IsUnique(); e.HasIndex(a => a.ExpiresAt);
            e.Property(a => a.Name).HasMaxLength(100).IsRequired(); e.Property(a => a.Platform).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.AppVersion).HasMaxLength(80).IsRequired(); e.Property(a => a.EncryptedReceipt).IsRequired();
            e.HasOne<NodePairingCode>().WithMany().HasForeignKey(a => a.PairingCodeId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
