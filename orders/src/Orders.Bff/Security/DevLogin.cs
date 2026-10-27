using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Orders.Bff.Security;

/// <summary>
/// Development sign-in, a placeholder for a real identity provider: two fixed users whose passwords come from the
/// environment (<c>.env</c>). Off unless <c>Bff:DevLogin:Enabled</c> is true, which only the development compose sets.
/// </summary>
public sealed record DevLoginOptions
{
    public const string SectionName = "Bff:DevLogin";

    public bool Enabled { get; init; }

    /// <summary>Password of <c>shopper</c>; empty means the user cannot sign in.</summary>
    public string ShopperPassword { get; init; } = string.Empty;

    /// <summary>Password of <c>admin</c>; empty means the user cannot sign in.</summary>
    public string AdminPassword { get; init; } = string.Empty;
}

/// <summary>One signed-in identity: Orders reads the shopper's client id from <c>sub</c>, so subjects are fixed GUIDs.</summary>
internal sealed record DevUser(string UserName, Guid Subject, string Role)
{
    public static readonly DevUser Shopper = new("shopper", Guid.Parse("5b0a3c1e-7d42-4f6a-9c1b-2e8f0d6a4b11"), BffAuthentication.ShopperRole);
    public static readonly DevUser Admin = new("admin", Guid.Parse("a1d3e5f7-2b4c-4d6e-8f01-3c5e7a9b0d22"), BffAuthentication.AdminRole);

    public ClaimsPrincipal ToPrincipal() => new(new ClaimsIdentity(
        [new Claim(BffAuthentication.SubjectClaim, Subject.ToString()), new Claim(BffAuthentication.NameClaim, UserName), new Claim(ClaimTypes.Role, Role)],
        BffAuthentication.CookieScheme,
        BffAuthentication.NameClaim,
        ClaimTypes.Role));
}

internal static class DevLoginUsers
{
    /// <summary>The user whose password matches, or null. Passwords are compared in fixed time; an empty one never matches.</summary>
    public static DevUser? Verify(DevLoginOptions options, string? userName, string? password)
    {
        var (user, expected) = userName switch
        {
            "shopper" => (DevUser.Shopper, options.ShopperPassword),
            "admin" => (DevUser.Admin, options.AdminPassword),
            _ => (null, string.Empty),
        };

        var matches = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(password ?? string.Empty)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

        return user is not null && expected.Length > 0 && matches ? user : null;
    }
}
