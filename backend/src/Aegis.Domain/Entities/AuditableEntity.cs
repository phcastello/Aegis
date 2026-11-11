// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Domain.Entities;

public abstract class AuditableEntity
{
    public Guid Id { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public DateTimeOffset UpdatedAt { get; protected set; }

    protected void InitializeAudit(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;

        Id = Guid.NewGuid();
        CreatedAt = timestamp;
        UpdatedAt = timestamp;
    }

    protected void Touch(DateTimeOffset? now = null)
    {
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
    }
}
