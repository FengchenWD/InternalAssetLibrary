using System.Security.Cryptography;
using System.Text;

namespace InternalAssetLibrary.Contracts;

public static class ClientReleaseSignature
{
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE1JskSJck7qN7Qw/USkbWw6qCJFyCzzLfHpEZ6tRaq5j0anG1GpI84u0rv8kNEVL47NFqi1yFMRJeXZwnT42gag==";

    public static byte[] Payload(ClientReleaseInfo release)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(release.Version);
        writer.Write(release.MinimumCompatibleVersion);
        writer.Write(release.PublishedAt.UtcTicks);
        writer.Write(release.InstallerFileName);
        writer.Write(release.InstallerSizeBytes);
        writer.Write(release.InstallerSha256.ToUpperInvariant());
        writer.Write(release.DownloadPath);
        writer.Write(release.ReleaseNotes?.Trim() ?? "");
        writer.Flush(); return output.ToArray();
    }

    public static bool Verify(ClientReleaseInfo release, string publicKey = PublicKey)
    {
        if (string.IsNullOrWhiteSpace(release.Signature)) return false;
        try
        {
            using var algorithm = ECDsa.Create();
            algorithm.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out var read);
            if (read != Convert.FromBase64String(publicKey).Length || algorithm.KeySize != 256) return false;
            return algorithm.VerifyData(Payload(release), Convert.FromBase64String(release.Signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException) { return false; }
    }
}
