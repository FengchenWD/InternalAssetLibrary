using InternalAssetLibrary.Server.Data;

namespace InternalAssetLibrary.Server.Security;

internal static class FirstLoginPasswordRecommendation
{
    public static bool Consume(UserRecord user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.MustChangePassword)
        {
            return false;
        }

        user.MustChangePassword = false;
        user.UpdatedAt = now;
        return true;
    }
}
