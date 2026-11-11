// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
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
