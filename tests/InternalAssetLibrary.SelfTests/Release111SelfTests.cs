using System.Security.Cryptography;
using System.Text.Json.Nodes;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Core;

internal static class Release111SelfTests
{
    public static void ReleaseSignaturesRejectTampering()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = new ClientReleaseInfo("1.1.1", "0.2.0-preview.6.3", DateTimeOffset.UtcNow,
            "InternalAssetLibrary.Client.Setup.exe", 100, new string('A',64), "/api/client/releases/latest/download", "更新\nNotes");
        var signature = Convert.ToBase64String(key.SignData(ClientReleaseSignature.Payload(release), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        release = release with { Signature = signature };
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        Check(ClientReleaseSignature.Verify(release, publicKey));
        foreach (var changed in new[] { release with { Version = "1.1.2" }, release with { InstallerSizeBytes = 101 },
                     release with { InstallerSha256 = new string('B',64) }, release with { ReleaseNotes = "不同说明" },
                     release with { DownloadPath = "/other.exe" }, release with { Signature = "bad" } })
            Check(!ClientReleaseSignature.Verify(changed, publicKey));
        Check(!ClientReleaseSignature.Verify(release)); // An arbitrary signer cannot replace the pinned key.
    }

    public static async Task FirstUpdateRecoveryPreservesUserData()
    {
        var root = Directory.CreateTempSubdirectory("ial-update-recovery-").FullName;
        try
        {
            var install = Path.Combine(root, "program"); var snapshot = Path.Combine(root, "recovery");
            Directory.CreateDirectory(Path.Combine(install,"runtime"));
            await File.WriteAllTextAsync(Path.Combine(install,"InternalAssetLibrary.Client.exe"),"old-client");
            await File.WriteAllTextAsync(Path.Combine(install,"runtime","ffmpeg.exe"),"old-runtime");
            await File.WriteAllTextAsync(Path.Combine(install,"my-media.mp4"),"original-media");
            await ClientRecoverySnapshot.CreateAsync(install,snapshot);
            Check(!File.Exists(Path.Combine(snapshot,"my-media.mp4")));
            await File.WriteAllTextAsync(Path.Combine(install,"InternalAssetLibrary.Client.exe"),"new-client");
            await ClientRecoverySnapshot.RestoreAsync(install,snapshot);
            Check(await File.ReadAllTextAsync(Path.Combine(install,"InternalAssetLibrary.Client.exe")) == "old-client");
            Check(await File.ReadAllTextAsync(Path.Combine(install,"my-media.mp4")) == "original-media");
            await File.WriteAllTextAsync(Path.Combine(snapshot,"runtime","ffmpeg.exe"),"tampered");
            await File.WriteAllTextAsync(Path.Combine(install,"InternalAssetLibrary.Client.exe"),"keep-on-failure");
            try { await ClientRecoverySnapshot.RestoreAsync(install,snapshot); throw new Exception("Damaged backup accepted"); }
            catch (InvalidDataException) { }
            Check(await File.ReadAllTextAsync(Path.Combine(install,"InternalAssetLibrary.Client.exe")) == "keep-on-failure");
        }
        finally { Directory.Delete(root,true); }
    }

    public static async Task RealUploadTranscodingPreservesAlphaAndOriginals()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName,"InternalAssetLibrary.slnx"))) directory=directory.Parent;
        var ffmpeg=Path.Combine(directory!.FullName,"artifacts","media-runtime","win-x64","ffmpeg.exe");
        if (!File.Exists(ffmpeg)) { Console.WriteLine("INFO  bundled FFmpeg transcoding check skipped: runtime not present."); return; }
        var runner=new InternalAssetLibrary.Client.Core.MediaAnalysis.ProcessMediaToolRunner();
        var root=Directory.CreateTempSubdirectory("ial-transcode-").FullName;
        var outputs=new List<string>();
        try
        {
            var inputs=new[] { (Name:"transparent.webm",Args:new[]{"-f","lavfi","-i","color=c=red@0.25:s=16x16:d=0.2,format=rgba","-c:v","libvpx-vp9","-auto-alt-ref","0","-pix_fmt","yuva420p"}),
                (Name:"opaque.mkv",Args:new[]{"-f","lavfi","-i","color=c=blue:s=16x16:d=0.2","-c:v","libx264"}),
                (Name:"sound.flac",Args:new[]{"-f","lavfi","-i","sine=frequency=440:duration=0.2","-c:a","flac"}) };
            var preprocessor=new InternalAssetLibrary.Client.Core.Transfers.CloudUploadPreprocessor(ffmpeg);
            foreach (var sample in inputs)
            {
                var input=Path.Combine(root,sample.Name);
                var generated=await runner.RunAsync(new(ffmpeg,["-v","error","-nostdin",..sample.Args,"-y",input]));
                Check(generated.Succeeded);
                var hash=SHA256.HashData(await File.ReadAllBytesAsync(input));
                var prepared=await preprocessor.PrepareAsync(input); outputs.Add(prepared.UploadPath);
                Check(prepared.IsTemporary && File.Exists(prepared.UploadPath));
                var originalAfter = SHA256.HashData(await File.ReadAllBytesAsync(input));
                Check(hash.SequenceEqual(originalAfter));
                Check(Path.GetExtension(prepared.UploadPath)==(sample.Name.StartsWith("transparent")?".mov":sample.Name.EndsWith("mkv")?".mp4":".mp3"));
            }
        }
        finally
        {
            foreach (var path in outputs) InternalAssetLibrary.Client.Core.Transfers.CloudUploadPreprocessor.TryDelete(path);
            Directory.Delete(root,true);
        }
    }
    private static void Check(bool value) { if (!value) throw new Exception("1.1.1 regression assertion failed"); }
}
