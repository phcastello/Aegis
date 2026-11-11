// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Tools;

public interface IAegisToolRegistry
{
    IReadOnlyList<IAegisTool> GetAvailableTools(ToolExecutionContext context);

    IAegisTool? Find(string name);
}
