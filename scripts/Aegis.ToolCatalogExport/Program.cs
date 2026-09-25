using System.Text.Json;
using Aegis.Application;
using Aegis.Application.Tools;
using Microsoft.Extensions.DependencyInjection;

// Export the registered production tools without executing them or connecting to Gmail.
// Tool constructors only capture dependencies; null values are safe until ExecuteAsync.
var registeredTypes = new ServiceCollection().AddApplication()
    .Where(descriptor => descriptor.ServiceType == typeof(IAegisTool))
    .Select(descriptor => descriptor.ImplementationType!)
    .ToList();
var tools = registeredTypes
    .Select(type =>
    {
        var constructor = type.GetConstructors().Single();
        var dependencies = new object?[constructor.GetParameters().Length];
        return (IAegisTool)constructor.Invoke(dependencies);
    })
    .OrderBy(tool => tool.Name, StringComparer.Ordinal)
    .Select(tool => new
    {
        type = "function",
        name = tool.Name,
        description = tool.Description,
        parameters = tool.ParametersSchema,
        strict = false
    })
    .ToList();

Console.WriteLine(JsonSerializer.Serialize(tools));
