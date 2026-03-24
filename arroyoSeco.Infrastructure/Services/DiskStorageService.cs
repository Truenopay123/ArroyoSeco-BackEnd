using Microsoft.Extensions.Options;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Infrastructure.Storage;

namespace arroyoSeco.Infrastructure.Services;

public class DiskStorageService : IStorageService
{
    private readonly StorageOptions _options;

    public DiskStorageService(IOptions<StorageOptions> options)
    {
        _options = options.Value;
    }

    public async Task<string> SaveFileAsync(Stream stream, string fileName, string folder, CancellationToken ct = default)
    {
        if (stream == null || stream.Length == 0)
            throw new ArgumentException("Stream vacío o inválido");

        var basePath = _options.ComprobantesPath ?? throw new InvalidOperationException("ComprobantesPath no configurado");
        var targetFolder = Path.Combine(basePath, folder);
        Directory.CreateDirectory(targetFolder);

        var safeFileName = $"{Guid.NewGuid()}_{Path.GetFileName(fileName)}";
        var fullPath = Path.Combine(targetFolder, safeFileName);

        using (var fileStream = new FileStream(fullPath, FileMode.Create))
        {
            await stream.CopyToAsync(fileStream, ct);
        }

        return $"{folder}/{safeFileName}";
    }

    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        var basePath = _options.ComprobantesPath ?? throw new InvalidOperationException("ComprobantesPath no configurado");
        var fullPath = Path.Combine(basePath, relativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public string GetPublicUrl(string relativePath)
    {
        return $"/comprobantes/{relativePath.Replace("\\", "/")}";
    }

    public async Task<(byte[] fileBytes, string mimeType)?> GetFileAsync(string relativePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var basePath = _options.ComprobantesPath ?? throw new InvalidOperationException("ComprobantesPath no configurado");
        var fullPath = Path.Combine(basePath, relativePath);

        // Prevent path traversal attacks
        var resolvedPath = Path.GetFullPath(fullPath);
        if (!resolvedPath.StartsWith(Path.GetFullPath(basePath)))
            return null;

        if (!File.Exists(resolvedPath))
            return null;

        try
        {
            var fileBytes = await File.ReadAllBytesAsync(resolvedPath, ct);
            var mimeType = GetMimeType(resolvedPath);
            return (fileBytes, mimeType);
        }
        catch
        {
            return null;
        }
    }

    private static string GetMimeType(string filePath) 
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream"
        };
    }
}
