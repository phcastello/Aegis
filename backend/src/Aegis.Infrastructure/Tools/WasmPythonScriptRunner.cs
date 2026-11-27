using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Aegis.Application.Tools;

namespace Aegis.Infrastructure.Tools;

public sealed class WasmPythonScriptRunner : IPythonScriptRunner
{
    private const string RuntimeDirectory = "/opt/aegis-python";
    private const string RunnerPath = RuntimeDirectory + "/runner";
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _slots = new(2, 2);

    public bool IsAvailable => OperatingSystem.IsLinux()
        && File.Exists(RunnerPath)
        && File.Exists(RuntimeDirectory + "/python.cwasm")
        && File.Exists(RuntimeDirectory + "/libwasmtime.so")
        && Directory.Exists(RuntimeDirectory + "/stdlib");

    public async Task<PythonScriptResult> RunAsync(string code, string? input, CancellationToken cancellationToken)
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
            return await ExecuteAsync(code, input, cancellationToken);
        }
        finally
        {
            _slots.Release();
        }
    }

    private static async Task<PythonScriptResult> ExecuteAsync(string code, string? input, CancellationToken cancellationToken)
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
            await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, new { code, input }, cancellationToken: timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            return process.ExitCode switch
            {
                0 => new PythonScriptResult(true, await stdout, await stderr),
                1 => new PythonScriptResult(false, await stdout, await stderr, "execution_failed"),
                3 => Failure("output_limit"),
                4 => Failure("timed_out"),
                _ => Failure("python_unavailable"),
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
}
