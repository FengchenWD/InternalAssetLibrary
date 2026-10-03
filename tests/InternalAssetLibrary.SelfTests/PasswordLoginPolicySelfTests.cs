using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Core;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Security;
using Microsoft.AspNetCore.Http;

internal static class PasswordLoginPolicySelfTests
{
    public static void PasswordRequiresTwoOfFourCharacterClasses()
    {
        foreach (var password in new[]
                 {
                     "ABCDEFG1",
                     "abcdefg1",
                     "Abcdefgh",
                     "1234567!",
                     "!!!!!!!A",
                     "密码密码密码1密码"
                 })
        {
            True(InputValidator.ValidatePassword(password).IsValid);
            True(PasswordService.IsAcceptable(password, out _));
        }

        foreach (var password in new[]
                 {
                     "12345678",
                     "abcdefgh",
                     "ABCDEFGH",
                     "!!!!!!!!",
                     "       A",
                     "Abc123!",
                     new string('x', 129)
                 })
        {
            False(InputValidator.ValidatePassword(password).IsValid);
            False(PasswordService.IsAcceptable(password, out _));
        }

        True(InputValidator.ValidatePassword("A" + new string('a', 127)).IsValid);
    }

    public static void FirstLoginRecommendationIsConsumedOnceWithoutBlockingAccess()
    {
        var originalUpdate = DateTimeOffset.UtcNow.AddDays(-1);
        var consumedAt = DateTimeOffset.UtcNow;
        var user = new UserRecord
        {
            Id = Guid.NewGuid(),
            Username = "member",
            NormalizedUsername = "MEMBER",
            PasswordHash = "hash",
            MustChangePassword = true,
            UpdatedAt = originalUpdate
        };

        True(FirstLoginPasswordRecommendation.Consume(user, consumedAt));
        False(user.MustChangePassword);
        Equal(consumedAt, user.UpdatedAt);
        False(FirstLoginPasswordRecommendation.Consume(user, consumedAt.AddMinutes(1)));
        Equal(consumedAt, user.UpdatedAt);

        var context = new DefaultHttpContext();
        var expected = new RequestIdentity(
            user.Id,
            Guid.NewGuid(),
            user.Username,
            false,
            PermissionNames.MemberDefaults());
        context.Items[BearerSessionMiddleware.IdentityKey] = expected;
        Equal(expected, AccessControl.RequireUser(context));
    }

    public static void ClientAndWebLoginOnlyRecommendPasswordChange()
    {
        var root = RepositoryRoot();
        var client = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "LoginWindow.axaml.cs"));
        var clientXaml = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "LoginWindow.axaml"));
        var webScript = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Server", "wwwroot", "app.js"));
        var webMarkup = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Server", "wwwroot", "index.html"));

        Contains("密码安全建议", client);
        Contains("首次登录成功。建议尽快", client);
        False(clientXaml.Contains("首次登录必须修改", StringComparison.Ordinal));
        Contains("passwordRecommendationDialog", webScript);
        Contains("showPasswordRecommendation", webScript);
        False(webScript.Contains("state.me.mustChangePassword || state.realtimeConnecting", StringComparison.Ordinal));
        False(webMarkup.Contains("首次登录必须设置新密码", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true, but was false.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expected}'.");
        }
    }
}
