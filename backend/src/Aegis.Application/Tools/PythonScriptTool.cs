using Aegis.Application.Chat;
using System.Text;
using System.Text.Json;

namespace Aegis.Application.Tools;

public sealed class PythonScriptTool(IPythonScriptRunner runner) : IAegisTool
{
    public const string ToolName = "run_python";
    public const int MaximumCodeBytes = 16 * 1024;
    public const int MaximumInputBytes = 256 * 1024;

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            code = new
            {
                type = "string",
                description = "Programa Python completo usando a biblioteca padrão. Use print para devolver o resultado."
            },
            input = new
            {
                type = new[] { "string", "null" },
                description = "Dados de entrada opcionais para sys.stdin, como texto, JSON, CSV ou outra representação. Use null quando a execução não precisar de dados."
            },
            input_files = new
            {
                type = "array", items = new { type = "string" }, maxItems = ChatUploadLimits.MaxFiles,
                description = "Nomes exatos ou IDs dos anexos desta conversa para abrir nesta execução. Em caso de ambiguidade, consulte o índice /inputs/index.json e selecione por ID."
            }
        },
        required = new[] { "code", "input" },
        additionalProperties = false
    });

    public string Name => ToolName;
    public string Description =>
        "Execute Python isolado para calcular, sortear, transformar ou analisar dados. Para sorteio de inteiro entre a e b inclusive, " +
        "use secrets.randbelow(b - a + 1) + a e imprima o valor. Use o valor obtido na resposta. " +
        "Para trabalhar com arquivos use caminhos absolutos. /inputs/index.json lista os anexos da conversa (id, name, size, path); " +
        "passe input_files para que os originais selecionados possam ser abertos pelo path indicado. " +
        "Guarde intermediários em /work entre execuções e turnos desta conversa. Para entregar arquivos ao usuário, " +
        "escreva os arquivos finais diretamente em /outputs; após sucesso ficam anexados à resposta, dispense links de caminho interno.";
    public JsonElement ParametersSchema => Schema;
    public bool IsAvailable => runner.IsAvailable;

    public async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("code", out var codeValue) || codeValue.ValueKind != JsonValueKind.String ||
            !arguments.TryGetProperty("input", out var inputValue) || inputValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
            arguments.EnumerateObject().Any(p => p.Name is not ("code" or "input" or "input_files")) ||
            arguments.EnumerateObject().Select(p => p.Name).Distinct().Count() != arguments.EnumerateObject().Count())
            return Error("invalid_tool_arguments", "Informe code como texto e input como texto ou null.");

        List<string> files = [];
        if (arguments.TryGetProperty("input_files", out var selection))
        {
            if (selection.ValueKind != JsonValueKind.Array || selection.GetArrayLength() > ChatUploadLimits.MaxFiles ||
                selection.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()) || v.GetString()!.Length > 255))
                return Error("invalid_tool_arguments", "input_files aceita até 20 nomes ou IDs de anexos.");
            files.AddRange(selection.EnumerateArray().Select(v => v.GetString()!));
        }

        var code = codeValue.GetString()!;
        var input = inputValue.GetString();
        if (string.IsNullOrWhiteSpace(code) || Encoding.UTF8.GetByteCount(code) > MaximumCodeBytes ||
            (input is not null && Encoding.UTF8.GetByteCount(input) > MaximumInputBytes))
            return Error("invalid_tool_arguments", "O código precisa ter até 16 KiB, e a entrada, até 256 KiB de texto UTF-8.");

        if (!runner.IsAvailable)
            return Error("python_unavailable", "O ambiente Python está indisponível.");

        var result = await runner.RunAsync(code, input, files, context, cancellationToken);
        var error = result.Success ? null : result.ErrorCode ?? "python_execution_failed";
        return new AegisToolResult(result.Success,
            JsonSerializer.Serialize(new { success = result.Success, stdout = result.Stdout, stderr = result.Stderr,
                generated_files = result.GeneratedFiles ?? [], error }),
            error);
    }

    private static AegisToolResult Error(string code, string message) =>
        new(false, JsonSerializer.Serialize(new { error = code, message }), code);
}
