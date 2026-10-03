using InternalAssetLibrary.Core;

namespace InternalAssetLibrary.Client.Core.Updates;

public static class ClientUpdateInstallationPolicy
{
    public const string FirstSilentUpdateVersion = "0.2.0-preview.6.8";

    public static bool ShouldUseSilentInstaller(string currentVersion, string targetVersion)
    {
        var current = SemanticVersion.Parse(currentVersion);
        var target = SemanticVersion.Parse(targetVersion);
        var baseline = SemanticVersion.Parse(FirstSilentUpdateVersion);
        return current.CompareTo(baseline) >= 0 && target.CompareTo(current) > 0;
    }
}
