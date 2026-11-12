using Bloom.Identity.V1;
using Grpc.Core;

namespace Bloom.Identity.Sessions;

/// <summary>The <c>bloom_rt</c> cookie (06-identity §4.3, plan I10).</summary>
public static class RefreshCookie
{
    private const string Name = "bloom_rt";

    private static CookieOptions Options(DateTimeOffset? expires) => new()
    {
        Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/auth/refresh", Expires = expires,
    };

    public static void Set(HttpContext http, string token, DateTimeOffset expires) =>
        http.Response.Cookies.Append(Name, token, Options(expires));

    public static void Clear(HttpContext http) => http.Response.Cookies.Delete(Name, Options(null));

    public static string? Read(HttpContext http) => http.Request.Cookies[Name];

    /// <summary>The answer of every token-issuing RPC: the cookie is set, the access token is the body.</summary>
    public static TokenResponse Respond(ServerCallContext ctx, IssuedSession session)
    {
        Set(ctx.GetHttpContext(), session.RefreshToken, session.RefreshExpiresAt);
        return new TokenResponse { AccessToken = session.AccessToken };
    }
}
