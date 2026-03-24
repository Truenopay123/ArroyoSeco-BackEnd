namespace arroyoSeco.Application.Common.Interfaces;

public interface IStorageService
{
    Task<string> SaveFileAsync(Stream stream, string fileName, string folder, CancellationToken ct = default);
    Task DeleteFileAsync(string relativePath, CancellationToken ct = default);
    string GetPublicUrl(string relativePath);
    Task<(byte[] fileBytes, string mimeType)?> GetFileAsync(string relativePath, CancellationToken ct = default);
}
