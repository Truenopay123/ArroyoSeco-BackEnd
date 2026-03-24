using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using arroyoSeco.Application.Common.Interfaces;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StorageController : ControllerBase
{
    private readonly IStorageService _storage;

    public StorageController(IStorageService storage)
    {
        _storage = storage;
    }

    [Authorize]
    [HttpPost("upload")]
    public async Task<ActionResult<string>> Upload([FromForm] IFormFile file, [FromQuery] string folder = "general", CancellationToken ct = default)
    {
        if (file == null || file.Length == 0)
            return BadRequest("Archivo vacío");

        using var stream = file.OpenReadStream();
        var relativePath = await _storage.SaveFileAsync(stream, file.FileName, folder, ct);
        var publicUrl = _storage.GetPublicUrl(relativePath);
        return Ok(new { url = publicUrl });
    }

    /// <summary>
    /// Serve public files (alojamientos, comprobantes, etc.) without authentication.
    /// Path format: GET /api/storage/public/{folder}/{guid}_{filename}
    /// Example: GET /api/storage/public/alojamientos/57ce6314-abb1-4b4d-bfdb-ccf18725566d_alojamiento1.webp
    /// </summary>
    [AllowAnonymous]
    [HttpGet("public/{folder}/{fileName}")]
    public async Task<IActionResult> GetPublicFile(string folder, string fileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(fileName))
            return BadRequest("Parámetros inválidos");

        var relativePath = $"{folder}/{fileName}";
        var fileData = await _storage.GetFileAsync(relativePath, ct);
        
        if (fileData == null)
            return NotFound();

        var (fileBytes, mimeType) = fileData.Value;
        return File(fileBytes, mimeType, fileName);
    }
}
