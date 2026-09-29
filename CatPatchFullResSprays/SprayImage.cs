using System;
using System.Security.Cryptography;

namespace CatPatchFullResSprays;

internal sealed class SprayImage
{
    public const int HardByteSizeLimit = 64 * 1024 * 1024;
    public string Id { get; }
    public byte[] Bytes { get; }
    public string Hash { get; }
    public bool BackendUploaded { get; }
    public string FileKey { get; }

    public SprayImage(string id, byte[] bytes, bool backendUploaded, string fileKey = "")
    {
        Id = Normalize(id);
        if (!IsValidId(Id) || bytes == null || bytes.Length == 0 || bytes.Length > HardByteSizeLimit)
            throw new ArgumentException("Invalid spray ID or image size.");

        Hash = ComputeHash(bytes);
        if (Id.StartsWith("custom-", StringComparison.Ordinal) && Id != "custom-" + Hash)
            throw new ArgumentException("Custom spray ID does not match its content.");

        Bytes = bytes;
        BackendUploaded = backendUploaded && !Id.StartsWith("custom-", StringComparison.Ordinal);
        FileKey = fileKey ?? "";
    }

    public static string Normalize(string id) => (id ?? "").Trim().ToLowerInvariant();

    public static bool IsValidId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128)
            return false;

        int start = id.StartsWith("custom-", StringComparison.Ordinal) ? 7 : 0;
        if (start == 7 && id.Length != 71)
            return false;

        for (int i = start; i < id.Length; i++)
            if (!((id[i] >= '0' && id[i] <= '9') || (id[i] >= 'a' && id[i] <= 'f')))
                return false;

        return true;
    }

    public static string ComputeHash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
