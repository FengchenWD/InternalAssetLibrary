using InternalAssetLibrary.Core;

namespace InternalAssetLibrary.Server.Security;

internal sealed class PasswordService
{
    private readonly PasswordHasher _hasher;
    private readonly string _dummyHash;

    public PasswordService(IConfiguration configuration)
    {
        var iterations = configuration.GetValue("Security:PasswordIterations", PasswordHasher.DefaultIterations);
        _hasher = new PasswordHasher(iterations);
        _dummyHash = _hasher.Hash("This password is never accepted 847291");
    }

    public string Hash(string password) => _hasher.Hash(password);

    public bool Verify(string password, string encodedHash) =>
        _hasher.Verify(encodedHash, password) != PasswordVerificationResult.Failed;

    public bool VerifyKnownOrDummy(string password, string? encodedHash, out bool needsRehash)
    {
        var result = _hasher.Verify(encodedHash ?? _dummyHash, password);
        needsRehash = encodedHash is not null && result == PasswordVerificationResult.SuccessRehashNeeded;
        return encodedHash is not null && result != PasswordVerificationResult.Failed;
    }

    public static bool IsAcceptable(string password, out string? error)
    {
        var validation = InputValidator.ValidatePassword(password);
        if (validation.IsValid)
        {
            error = null;
            return true;
        }

        if (validation.Errors.Any(item => item.Code == "length"))
        {
            error = "密码长度必须为 8 至 128 个字符。";
            return false;
        }

        error = "密码必须至少包含大写字母、小写字母、数字、特殊符号中的两种；空白字符不计为特殊符号。";
        return false;
    }
}
