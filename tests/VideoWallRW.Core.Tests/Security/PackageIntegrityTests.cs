using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoWallRW.App.Security;
using Xunit;

namespace VideoWallRW.Core.Tests.Security;

public sealed class PackageIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vwrw-integrity-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(2048);

    public PackageIntegrityTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "program.dll"), "original binary fixture");
        Seal();
    }

    private void Seal(Dictionary<string, string>? manifest = null)
    {
        manifest ??= new() { ["program.dll"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "program.dll")))) };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));
        File.WriteAllBytes(Path.Combine(_root, "package-integrity.json"), bytes);
        File.WriteAllBytes(Path.Combine(_root, "package-integrity.sig"), _key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }

    private string? Verify() => PackageIntegrity.Verify(_root, _key.ExportSubjectPublicKeyInfoPem());

    [Fact] public void OriginalPackagePasses() => Assert.Null(Verify());

    [Fact] public void ChangedBinaryFails()
    {
        File.AppendAllText(Path.Combine(_root, "program.dll"), "modified");
        Assert.NotNull(Verify());
    }

    [Fact] public void MissingBinaryFails()
    {
        File.Delete(Path.Combine(_root, "program.dll"));
        Assert.NotNull(Verify());
    }

    [Fact] public void MissingManifestFails()
    {
        File.Delete(Path.Combine(_root, "package-integrity.json"));
        Assert.NotNull(Verify());
    }

    [Fact] public void EditedManifestFailsEvenWhenContentUnchanged()
    {
        File.AppendAllText(Path.Combine(_root, "package-integrity.json"), " ");
        Assert.NotNull(Verify());
    }

    [Fact] public void DifferentSigningKeyFails()
    {
        using var otherKey = RSA.Create(2048);
        Assert.NotNull(PackageIntegrity.Verify(_root, otherKey.ExportSubjectPublicKeyInfoPem()));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("C:/outside.dll")]
    [InlineData("program.dll:stream")]
    public void SignedUnsafePathFails(string path)
    {
        Seal(new() { [path] = new string('0', 64) });
        Assert.NotNull(Verify());
    }

    [Fact] public void EmptySignedInventoryFails()
    {
        Seal(new());
        Assert.NotNull(Verify());
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
