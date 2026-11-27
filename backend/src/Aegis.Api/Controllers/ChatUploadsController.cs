using Aegis.Application.Chat;
using Aegis.Infrastructure.Chat;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/chat/uploads")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ChatUploadsController(ChatUploadStore store) : ControllerBase
{
    public sealed record CreateUploadRequest(string FileName, string? ContentType, long SizeBytes);

    [HttpPost, RequestSizeLimit(4096)]
    public async Task<IActionResult> Create(CreateUploadRequest request, CancellationToken ct)
    {
        if (request.SizeBytes > ChatUploadLimits.MaxFileBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "O tamanho máximo é de 512 MiB por arquivo." });
        if (request.SizeBytes <= 0) return BadRequest(new { error = "O arquivo está vazio." });
        try
        {
            var upload = await store.CreateAsync(request.FileName, request.ContentType, (int)request.SizeBytes, ct);
            return StatusCode(StatusCodes.Status201Created, new { upload.Id, chunkSize = ChatUploadLimits.ChunkSize, upload.SizeBytes });
        }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (ChatUploadCapacityException error)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = error.Message });
        }
    }

    [HttpPut("{id:guid}/chunks/{index:int}"), RequestSizeLimit(ChatUploadLimits.ChunkSize)]
    public async Task<IActionResult> PutChunk(Guid id, int index, CancellationToken ct)
    {
        var result = await store.WriteChunkAsync(id, index, Request.Body, Request.ContentLength, ct);
        return result switch
        {
            ChunkWriteResult.Saved => NoContent(),
            ChunkWriteResult.NotFound => NotFound(new { error = "O envio expirou ou já foi utilizado." }),
            ChunkWriteResult.Conflict => Conflict(new { error = "Este bloco já foi enviado com conteúdo diferente." }),
            _ => BadRequest(new { error = "O tamanho ou a posição do bloco não corresponde ao arquivo." })
        };
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Discard(Guid id, CancellationToken ct)
    {
        await store.DiscardAsync(id, ct);
        return NoContent();
    }
}
