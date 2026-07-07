namespace Aegis.Application.Tools;

public interface IAegisToolRegistry
{
    IReadOnlyList<IAegisTool> GetAvailableTools(ToolExecutionContext context);

    IAegisTool? Find(string name);
}
