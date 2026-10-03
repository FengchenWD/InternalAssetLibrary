using System.Globalization;
using System.Security.Cryptography;

namespace InternalAssetLibrary.Core;

public enum PasswordVerificationResult
{
    Failed,
    Success,
    SuccessRehashNeeded
}

public sealed class PasswordHasher
{
    public const int DefaultIterations = 600_000;
    public const int MaximumIterations = 2_000_000;
    public const int SaltSize = 16;
    public const int HashSize = 32;
    private const string Algorithm = "pbkdf2-sha256";

    private readonly int _iterations;

    public PasswordHasher(int iterations = DefaultIterations)
    {
        if (iterations is < 100_000 or > MaximumIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                $"PBKDF2 iterations must be between 100,000 and {MaximumIterations:N0}.");
        }

        _iterations = iterations;
    }

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            _iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Algorithm}${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public PasswordVerificationResult Verify(string? encodedHash, string? password)
    {
        if (string.IsNullOrEmpty(encodedHash) || password is null)
        {
            return PasswordVerificationResult.Failed;
        }

        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) ||
            iterations is < 100_000 or > MaximumIterations)
        {
            return PasswordVerificationResult.Failed;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return PasswordVerificationResult.Failed;
        }

        if (salt.Length != SaltSize || expected.Length != HashSize)
        {
            return PasswordVerificationResult.Failed;
        }

        byte[] actual;
        try
        {
            actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
        }
        catch (ArgumentOutOfRangeException)
        {
            return PasswordVerificationResult.Failed;
        }

        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerificationResult.Failed;
        }

        return iterations < _iterations
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }
}
