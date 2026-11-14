using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace VideoWallRW.App.Security;

/// <summary>
/// Detects damaged/changed release files. The embedded public key authenticates the
/// inventory; folder ACLs are the security boundary against non-admin modification.
/// This does not prevent an administrator from replacing the executable/verifier.
/// </summary>
internal static class PackageIntegrity
{
    internal const string KeyResource = "VideoWallRW.PackagePublicKey";

    internal static string? VerifyCurrentPackage()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(KeyResource);
        if (resource is null)
        {
#if PROTECTED_PACKAGE
            return "A chave de integridade desta versão está ausente. Reinstale o programa.";
#else
            return null; // Ordinary developer builds are intentionally not sealed.
#endif
        }
        using var reader = new StreamReader(resource);
        return Verify(AppContext.BaseDirectory, reader.ReadToEnd());
    }

    internal static string? Verify(string directory, string publicKey)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, "package-integrity.json"));
            var signature = File.ReadAllBytes(Path.Combine(directory, "package-integrity.sig"));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKey);
            if (!rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                return "A assinatura do pacote é inválida. Reinstale o programa.";

            var inventory = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes);
            if (inventory is null || inventory.Count == 0)
                return "O inventário do pacote está vazio. Reinstale o programa.";
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var (relative, expectedHash) in inventory)
            {
                var path = Path.GetFullPath(Path.Combine(root, relative));
                if (Path.IsPathRooted(relative) || relative.Contains(':') ||
                    !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return "O inventário contém um caminho inválido. Reinstale o programa.";
                using var file = File.OpenRead(path);
                if (!CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(file), Convert.FromHexString(expectedHash)))
                    return $"Arquivo alterado ou danificado: {relative}. Reinstale o programa.";
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   CryptographicException or JsonException or ArgumentException or FormatException)
        {
            return "Não foi possível verificar os arquivos do programa. Reinstale a versão original.";
        }
    }
}
