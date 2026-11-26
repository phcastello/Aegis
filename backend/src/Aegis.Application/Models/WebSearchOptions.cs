namespace Aegis.Application.Models;

public sealed class WebSearchOptions
{
    public const string SectionName = "AegisWebSearch";
    public bool Enabled { get; set; } = true;
    public int MaxSearchActionsPerTurn { get; set; } = 20;
}
