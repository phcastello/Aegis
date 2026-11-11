// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
using Aegis.Application.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
namespace Aegis.Api.Nodes;
public sealed class NodeApiFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        var result = await next();
        if (result.Exception is NodeException error)
        {
            result.ExceptionHandled = true;
            result.Result = new ObjectResult(new { code = error.Code, error = error.Message }) { StatusCode = error.Status };
        }
    }
}
