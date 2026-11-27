using Aegis.Application.Prompts;
using Aegis.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Persistence;

public sealed class OwnerInstructionsStore(AegisDbContext db) : IOwnerInstructions
{
    public Task<InstructionSettings> ReadAsync(CancellationToken ct) =>
        db.OwnerPreferences.Where(p => p.Id == OwnerPreferences.SingletonId)
            .Select(p => new InstructionSettings(p.CustomInstructions, p.Revision)).SingleAsync(ct);

    public async Task<InstructionSettings?> UpdateAsync(string text, Guid expectedRevision, CancellationToken ct)
    {
        var preferences = await db.OwnerPreferences.SingleAsync(p => p.Id == OwnerPreferences.SingletonId, ct);
        if (preferences.CustomInstructions == text.Trim()) return new(preferences.CustomInstructions, preferences.Revision);
        if (preferences.Revision != expectedRevision) return null;
        preferences.Update(text);
        try
        {
            await db.SaveChangesAsync(ct);
            return new(preferences.CustomInstructions, preferences.Revision);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }
}
