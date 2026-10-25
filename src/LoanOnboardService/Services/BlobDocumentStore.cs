using System.Security.Cryptography;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace LoanOnboardService.Services;

public record StoredDocument(string DocType, string FileName, string ContentType, long SizeBytes, string BlobPath, string Sha256);

public class DocumentRejectedException(string message) : Exception(message);

public interface IDocumentStore
{
    Task<StoredDocument> SaveAsync(string dealerId, string ackId, string docType, IFormFile file, CancellationToken ct);
}

/// <summary>
/// Stores customer documents in Blob under {dealerId}/{ackId}/{docType}/{fileName}.
/// Uploads are validated by size, declared type and file signature, and hashed with SHA-256.
/// Replace the signature check with a Microsoft Defender for Storage scan result if you need real malware scanning.
/// </summary>
public class BlobDocumentStore(BlobServiceClient service, IConfiguration config, ILogger<BlobDocumentStore> log) : IDocumentStore
{
    private static readonly Dictionary<string, byte[]> Signatures = new()
    {
        ["application/pdf"] = "%PDF"u8.ToArray(),
        ["image/jpeg"] = [0xFF, 0xD8, 0xFF],
        ["image/png"] = [0x89, 0x50, 0x4E, 0x47]
    };

    private readonly string _containerName = config["Blob:ContainerName"] ?? "loan-documents";
    private readonly long _maxBytes = config.GetValue("Documents:MaxBytes", 10L * 1024 * 1024);
    private volatile bool _containerReady;

    public async Task<StoredDocument> SaveAsync(string dealerId, string ackId, string docType, IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0) throw new DocumentRejectedException($"'{file.FileName}' is empty.");
        if (file.Length > _maxBytes) throw new DocumentRejectedException($"'{file.FileName}' exceeds the size limit.");
        if (!Signatures.TryGetValue(file.ContentType, out var signature))
            throw new DocumentRejectedException($"'{file.FileName}' has an unsupported type '{file.ContentType}'. Use PDF, JPEG or PNG.");

        await using var stream = file.OpenReadStream();
        var header = new byte[signature.Length];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        if (read < signature.Length || !header.AsSpan().SequenceEqual(signature))
            throw new DocumentRejectedException($"'{file.FileName}' content does not match its declared type.");

        stream.Position = 0;
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        stream.Position = 0;

        var container = service.GetBlobContainerClient(_containerName);
        if (!_containerReady)
        {
            await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
            _containerReady = true;
        }

        var safeName = Path.GetFileName(file.FileName);
        var path = $"{dealerId}/{ackId}/{docType}/{Guid.NewGuid():N}-{safeName}";
        await container.GetBlobClient(path).UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = file.ContentType },
            Metadata = new Dictionary<string, string> { ["sha256"] = sha, ["ackId"] = ackId }
        }, ct);

        log.LogInformation("Stored document {Path} ({Bytes} bytes) for {AckId}", path, file.Length, ackId);
        return new StoredDocument(docType, safeName, file.ContentType, file.Length, path, sha);
    }
}

public static class BlobClientFactory
{
    /// <summary>Uses ConnectionStrings:BlobStorage when set, otherwise Blob:ServiceUri with managed identity.</summary>
    public static BlobServiceClient Create(IConfiguration config)
    {
        var cs = config.GetConnectionString("BlobStorage");
        if (!string.IsNullOrWhiteSpace(cs) && !cs.StartsWith("REPLACE_", StringComparison.Ordinal))
            return new BlobServiceClient(cs);

        var uri = config["Blob:ServiceUri"];
        if (!string.IsNullOrWhiteSpace(uri))
            return new BlobServiceClient(new Uri(uri), new DefaultAzureCredential());

        throw new InvalidOperationException(
            "Configure ConnectionStrings:BlobStorage (connection string) or Blob:ServiceUri (managed identity).");
    }
}
