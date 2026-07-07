using Aegis.Application.Models;

namespace Aegis.Application.Tools;

public sealed class AegisToolLoop(
    IAegisModelClient modelClient,
    IAegisToolRegistry toolRegistry) : IAegisToolLoop
{
    private const int DefaultMaxIterations = 4;

    public async Task<ModelToolResponse> RunAsync(
        ModelRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var tools = toolRegistry
            .GetAvailableTools(context)
            .Select(tool => new ModelToolDefinition(tool.Name, tool.Description, tool.ParametersSchema))
            .ToList();

        return await modelClient.RespondWithToolsAsync(
            new ModelToolRequest(request, tools, DefaultMaxIterations),
            cancellationToken);
    }
}
