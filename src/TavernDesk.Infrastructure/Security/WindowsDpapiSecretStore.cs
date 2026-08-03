using System.Security.Cryptography;
using System.Text;
using TavernDesk.Core.Abstractions;
using TavernDesk.Infrastructure.Storage;

namespace TavernDesk.Infrastructure.Security;

public sealed class WindowsDpapiSecretStore : ISecretStore
{
    private const string ReferencePrefix = "dpapi:v1:";
    private const int MaximumProtectedSecretBytes = 64 * 1024;
    private readonly AppDataPaths _paths;

    public WindowsDpapiSecretStore(AppDataPaths paths)
    {
        _paths = paths;
    }

    public async Task<string> SaveAsync(
        string ownerId,
        string secret,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "TavernDesk API 密钥存储依赖 Windows DPAPI。");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("密钥不能为空。", nameof(secret));
        }

        var fileName = $"{Sha256Hex(
            $"{ownerId}\0{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}")}.secret";
        var targetPath = ResolveReferencePath(ReferencePrefix + fileName);
        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
        var plaintext = Encoding.UTF8.GetBytes(secret);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(
                plaintext,
                BuildEntropy(fileName),
                DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(
                temporaryPath,
                protectedBytes,
                cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: true);
            return ReferencePrefix + fileName;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<string?> ReadAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "TavernDesk API 密钥存储依赖 Windows DPAPI。");
        }
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var path = ResolveReferencePath(reference);
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumProtectedSecretBytes)
        {
            throw new InvalidDataException("密钥存储文件大小无效。");
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(
                protectedBytes,
                BuildEntropy(Path.GetFileName(path)),
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public Task<bool> ExistsAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            !string.IsNullOrWhiteSpace(reference)
            && File.Exists(ResolveReferencePath(reference)));
    }

    public Task DeleteAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reference))
        {
            return Task.CompletedTask;
        }

        var path = ResolveReferencePath(reference);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string ResolveReferencePath(string reference)
    {
        if (!reference.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("无法识别密钥引用格式。");
        }

        var fileName = reference[ReferencePrefix.Length..];
        if (fileName.Length != 71
            || !fileName.EndsWith(".secret", StringComparison.Ordinal)
            || fileName[..64].Any(character =>
                character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("密钥引用包含无效文件名。");
        }

        var root = Path.GetFullPath(_paths.SecretsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("密钥引用越过了安全存储目录。");
        }

        return path;
    }

    private static byte[] BuildEntropy(string fileName) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"TavernDesk.ProviderSecret.v1\0{fileName}"));

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

}
