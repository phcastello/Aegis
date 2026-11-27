using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Aegis.Application.Chat;

public sealed record AttachmentMediaPreview(string Text, byte[]? Jpeg = null);

public static class LargeAttachmentMedia
{
    private const long MaxSourceBytes = 512L * 1024 * 1024;
    private const int MaxTextChars = 24_000;
    private static readonly SemaphoreSlim Slots = new(2, 2);

    public static async Task<AttachmentMediaPreview> PreviewAsync(Stream source, string contentType,
        int page, CancellationToken ct, bool includeImage = true)
    {
        var extension = contentType switch
        {
            "application/pdf" => ".pdf",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => null
        };
        if (extension is null || !OperatingSystem.IsLinux())
            return new("A prévia local deste formato está indisponível.");
        if (page < 1) return new("A numeração de páginas começa em 1.");

        await Slots.WaitAsync(ct);
        DirectoryInfo? directory = null;
        try
        {
            directory = Directory.CreateTempSubdirectory("aegis-attachment-");
            var input = Path.Combine(directory.FullName, "input" + extension);
            await using (var file = new FileStream(input, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (source.CanSeek) source.Position = 0;
                var buffer = new byte[65536];
                long size = 0;
                int bytes;
                while ((bytes = await source.ReadAsync(buffer, ct)) != 0)
                {
                    size += bytes;
                    if (size > MaxSourceBytes) return new("Esta entrada excede 512 MiB para leitura.");
                    await file.WriteAsync(buffer.AsMemory(0, bytes), ct);
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var token = timeout.Token;
            var preview = Path.Combine(directory.FullName, "preview.jpg");
            string description;
            if (extension == ".pdf")
            {
                var (valid, info) = await RunAsync("pdfinfo", [input], directory, token);
                if (!valid) return new("Não consegui abrir o PDF para a leitura por página.");
                var count = info.Split('\n').LastOrDefault(line => line.StartsWith("Pages:", StringComparison.Ordinal));
                int.TryParse(count?.Substring(6).Trim(), CultureInfo.InvariantCulture, out var pages);
                if (pages > 0 && page > pages) return new($"O PDF tem {pages} páginas.");
                description = pages > 0 ? $"Página {page} de {pages}." : $"Página {page}.";
                var n = page.ToString(CultureInfo.InvariantCulture);
                var (hasText, text) = await RunAsync("pdftotext", ["-f", n, "-l", n, "-nopgbrk", "-enc", "UTF-8", input, "-"], directory, token);
                if (hasText && !string.IsNullOrWhiteSpace(text)) description += "\n" + text.Trim();
                else description += " Sem texto extraível nesta página.";
                if (!includeImage) return new(description);
                await RunAsync("pdftoppm", ["-f", n, "-l", n, "-singlefile", "-scale-to", "1440", "-jpeg", input,
                    Path.Combine(directory.FullName, "preview")], directory, token);
            }
            else
            {
                description = "Prévia reduzida da imagem.";
                if (!includeImage) return new("Esta imagem requer leitura visual.");
                await RunAsync("vipsthumbnail", [input, "--size=1440x1440>", "--output=" + preview + "[Q=80,strip]"], directory, token);
            }
            var image = new FileInfo(preview);
            return image.Exists && image.Length is > 0 and <= 3 * 1024 * 1024
                ? new(description, await File.ReadAllBytesAsync(preview, token))
                : new(description + " A prévia visual não pôde ser gerada.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new("A geração da prévia excedeu o tempo disponível.");
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        {
            return new("Não foi possível gerar a prévia local deste arquivo.");
        }
        finally
        {
            try { directory?.Delete(recursive: true); }
            finally { Slots.Release(); }
        }
    }

    private static async Task<(bool Ok, string Text)> RunAsync(string tool, string[] arguments,
        DirectoryInfo directory, CancellationToken ct)
    {
        var start = new ProcessStartInfo("/usr/bin/prlimit")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = directory.FullName
        };
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";
        start.Environment["LC_ALL"] = "C";
        start.Environment["TMPDIR"] = directory.FullName;
        start.Environment["VIPS_CONCURRENCY"] = "1";
        foreach (var arg in new[] { "--as=1073741824", "--cpu=20", "--fsize=16777216", "--core=0", "--", "/usr/bin/" + tool })
            start.ArgumentList.Add(arg);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Conversor indisponível.");
        using var cancel = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var discard = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var buffer = new char[4096];
        var result = new StringBuilder();
        var truncated = false;
        int read;
        while ((read = await process.StandardOutput.ReadAsync(buffer, CancellationToken.None)) != 0)
        {
            var take = Math.Min(read, MaxTextChars - result.Length);
            result.Append(buffer, 0, take);
            truncated |= take < read;
        }
        await Task.WhenAll(discard, process.WaitForExitAsync());
        ct.ThrowIfCancellationRequested();
        if (truncated) result.Append("\n[Trecho abreviado.]");
        return (process.ExitCode == 0, result.ToString());
    }
}
