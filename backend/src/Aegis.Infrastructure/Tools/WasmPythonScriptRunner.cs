using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Aegis.Application.Tools;

namespace Aegis.Infrastructure.Tools;

public sealed class WasmPythonScriptRunner(ScriptWorkspace workspace) : IPythonScriptRunner
{
    private const string RuntimeDirectory = "/opt/aegis-python";
    private const string RunnerPath = RuntimeDirectory + "/runner";
    private const string RevisionPath = RuntimeDirectory + "/revision";
    private const string AuditPrefix = "AEGIS_SCRIPT_SHAPE:";
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _slots = new(2, 2);

    public bool IsAvailable => OperatingSystem.IsLinux()
        && File.Exists(RunnerPath)
        && File.Exists(RuntimeDirectory + "/python.cwasm")
        && File.Exists(RuntimeDirectory + "/libwasmtime.so")
        && File.Exists(RevisionPath)
        && Directory.Exists(RuntimeDirectory + "/stdlib")
        && workspace.HasBoundedScratch;

    public async Task<PythonScriptResult> RunAsync(string code, string? input, IReadOnlyList<string> inputFiles,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || Encoding.UTF8.GetByteCount(code) > PythonScriptTool.MaximumCodeBytes
            || (input is not null && Encoding.UTF8.GetByteCount(input) > PythonScriptTool.MaximumInputBytes))
        {
            return Failure("invalid_input");
        }

        if (!IsAvailable)
        {
            return Failure("python_unavailable");
        }

        if (!await _slots.WaitAsync(ExecutionTimeout, cancellationToken))
        {
            return Failure("busy");
        }

        try
        {
            var revision = (await File.ReadAllTextAsync(RevisionPath, cancellationToken)).Trim();
            var execution = new ScriptExecution(revision, new ScriptProgram(code, input),
                (program, dirs, token) => ExecuteAsync(program.Code, program.Input, dirs, audit: false, token));
            return await workspace.RunAsync(context, inputFiles,
                (dirs, token) => ExecuteAsync(code, input, dirs, audit: true, token), cancellationToken, execution);
        }
        finally
        {
            _slots.Release();
        }
    }

    private static async Task<PythonScriptResult> ExecuteAsync(string code, string? input, ScriptDirectories dirs, bool audit,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExecutionTimeout);
        timeout.Token.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(RunnerPath)
        {
            WorkingDirectory = RuntimeDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(dirs.Inputs);
        startInfo.ArgumentList.Add(dirs.Work);
        startInfo.ArgumentList.Add(dirs.Outputs);
        startInfo.Environment.Clear();
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Failure("python_unavailable");
            }
        }
        catch (Win32Exception)
        {
            return Failure("python_unavailable");
        }

        // The trusted launcher limits both streams before they reach these pipe readers.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, new { code, input, workspace = true, audit },
                cancellationToken: timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            var errors = await stderr;
            var sanitized = audit ? ReadAudit(ref errors) : null;
            return process.ExitCode switch
            {
                0 => new PythonScriptResult(true, await stdout, errors) { SanitizedCode = sanitized },
                1 => new PythonScriptResult(false, await stdout, errors, "execution_failed") { SanitizedCode = sanitized },
                3 => Failure("output_limit") with { SanitizedCode = sanitized },
                4 => Failure("timed_out") with { SanitizedCode = sanitized },
                _ => Failure("python_unavailable") with { SanitizedCode = sanitized },
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("timed_out");
        }
        catch (IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure("python_unavailable");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The child completed between the status check and the kill request.
                }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            try
            {
                await Task.WhenAll(stdout, stderr);
            }
            catch (IOException)
            {
                // The process was reaped. There is no output to recover from a failed pipe.
            }
        }
    }

    private static PythonScriptResult Failure(string errorCode) => new(false, string.Empty, string.Empty, errorCode);

    private static string? ReadAudit(ref string errors)
    {
        if (!errors.StartsWith(AuditPrefix, StringComparison.Ordinal)) return null;
        var end = errors.IndexOf('\n');
        if (end < 0) return null;
        var metadata = errors[AuditPrefix.Length..end];
        errors = errors[(end + 1)..];
        try { return JsonSerializer.Deserialize<string>(metadata); }
        catch (JsonException) { return null; }
    }
}
