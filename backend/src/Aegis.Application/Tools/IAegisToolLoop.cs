// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
using Aegis.Application.Models;

namespace Aegis.Application.Tools;

public interface IAegisToolLoop
{
    Task<ModelToolResponse> RunAsync(
        ModelRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ModelStreamChunk> StreamAsync(
        ModelRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);
}
