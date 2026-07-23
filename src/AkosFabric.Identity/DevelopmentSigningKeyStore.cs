using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace AkosFabric.Identity;

public static class DevelopmentSigningKeyStore
{
    private const int KeySizeInBits = 3072;

    public static RsaSecurityKey LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The signing-key path has no parent directory.");
        Directory.CreateDirectory(directory);
        RestrictDirectoryPermissions(directory);

        if (!File.Exists(fullPath))
        {
            CreateKeyAtomically(fullPath);
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(fullPath));

        var keyIdBytes = SHA256.HashData(rsa.ExportSubjectPublicKeyInfo());
        return new RsaSecurityKey(rsa)
        {
            KeyId = Base64UrlEncoder.Encode(keyIdBytes),
        };
    }

    private static void CreateKeyAtomically(string path)
    {
        using var rsa = RSA.Create(KeySizeInBits);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(rsa.ExportPkcs8PrivateKeyPem());
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            RestrictFilePermissions(temporaryPath);

            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process created the shared development key first.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void RestrictDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite);
        }
    }
}
