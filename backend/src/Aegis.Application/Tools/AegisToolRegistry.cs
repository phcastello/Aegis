// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Tools;

public sealed class AegisToolRegistry(IEnumerable<IAegisTool> tools) : IAegisToolRegistry
{
    private readonly IReadOnlyList<IAegisTool> tools = tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();

    public IReadOnlyList<IAegisTool> GetAvailableTools(ToolExecutionContext context)
    {
        return tools;
    }

    public IAegisTool? Find(string name)
    {
        return tools.FirstOrDefault(tool =>
            string.Equals(tool.Name, name, StringComparison.Ordinal));
    }
}
