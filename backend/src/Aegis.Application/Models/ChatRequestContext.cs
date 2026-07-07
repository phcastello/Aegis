namespace Aegis.Application.Models;

public sealed record ChatRequestContext(
    string UserContent,
    bool RequiresTools = false,
    bool HasPendingAction = false);
