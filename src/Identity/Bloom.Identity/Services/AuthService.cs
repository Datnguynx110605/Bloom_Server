using Bloom.Identity.Context;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.Sessions;
using Bloom.Identity.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Services;

/// <summary>LLD 06-identity §3 component <c>Auth</c>.</summary>
public sealed class AuthService(SessionService sessions, IdentityDbContext db) : Auth.AuthBase
{
    public override async Task<TokenResponse> Refresh(Empty request, ServerCallContext context)
    {
        var http = context.GetHttpContext();
        http.Response.Headers.CacheControl = "no-store"; // on errors too (I11)
        return RefreshCookie.Respond(context, await sessions.RefreshAsync(RefreshCookie.Read(http)));
    }

    public override async Task<Empty> SignOut(SignOutRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context);
        if (!Guid.TryParse(request.Sid, out var familyId))
            throw IdentityErrors.Validation("sid", "sid must be the sid claim of the access token.");
        await sessions.RevokeFamilyAsync(caller.UserId, familyId);
        RefreshCookie.Clear(context.GetHttpContext());
        return new Empty();
    }

    public override async Task<Empty> SignOutEverywhere(Empty request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context);
        await using var tx = await db.Database.BeginTransactionAsync(context.CancellationToken);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == caller.UserId, context.CancellationToken)
            ?? throw IdentityErrors.Error(IdentityErrors.NotFound, "No such account.");
        await sessions.RevokeAllAsync(account, "sign_out_everywhere");
        await db.SaveChangesAsync(context.CancellationToken);
        await tx.CommitAsync(context.CancellationToken);
        return new Empty();
    }
}
