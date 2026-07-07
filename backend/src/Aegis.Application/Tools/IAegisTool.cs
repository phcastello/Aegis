using System.Text.Json;

namespace Aegis.Application.Tools;

public interface IAegisTool
{
    string Name { get; }

    string Description { get; }

    JsonElement ParametersSchema { get; }

    Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);
}
