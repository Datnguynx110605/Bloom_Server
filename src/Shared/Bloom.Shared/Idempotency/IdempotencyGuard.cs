using System.Collections.Frozen;
using System.Security.Cryptography;
using Bloom.Shared.Errors;
using Bloom.Shared.Observability;
using Google.Protobuf;
using Google.Rpc;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Status = Google.Rpc.Status;

namespace Bloom.Shared.Idempotency;

/// <summary>What <see cref="IdempotencyGuard.ExecuteMultiStepAsync{TRequest,TState,TResponse}"/> hands its two steps. <see cref="Resumed"/> is true when this request took over a stale key.</summary>
public sealed record IdempotencyClaim(Guid UserId, string Key, bool Resumed);

/// <summary>Idempotency guard (00-reliability §7.4, LLD-ADR-001).</summary>
public sealed class IdempotencyGuard(DbContext db, BloomMetrics metrics)
{
    private const string Savepoint = "idempotency";

    /// <summary>The gRPC statuses that are deterministic business rejections: stored and replayed (spec §6.7). Everything else releases the key.</summary>
    public static readonly IReadOnlySet<StatusCode> Stored =
        new[] { StatusCode.FailedPrecondition, StatusCode.InvalidArgument, StatusCode.NotFound, StatusCode.AlreadyExists, StatusCode.PermissionDenied }.ToFrozenSet();

    private sealed record Call(Guid UserId, string Key, string Operation, byte[] Hash)
    {
        public string ProviderKey => $"bloom:{Operation}:{UserId}:{Key}";

        /// <summary>The <c>locked_until</c> this request set when it claimed or took over the key. Complete and release only act while it is unchanged.</summary>
        public DateTime Fence { get; set; }

        /// <summary>True when the last claim took over a stale key.</summary>
        public bool Resumed { get; set; }
    }

    /// <summary>The key was taken over by another request while this one was working; its transaction is already rolled back.</summary>
    private sealed class FenceLostException : Exception;

    internal sealed class ExistingRow
    {
        public string Operation { get; set; } = "";
        public byte[] RequestHash { get; set; } = [];
        public string Status { get; set; } = "";
        public bool Locked { get; set; }
        public int? ResponseStatus { get; set; }
        public byte[]? ResponseBody { get; set; }
    }

    /// <summary>
    /// Single-transaction form: claim, handler, <c>SaveChanges</c> and the stored outcome commit together. A concurrent duplicate
    /// blocks on the primary key until then and gets the replay. The handler only stages changes; the guard saves them.
    /// Request messages must not use map fields (C# protobuf serializes them non-deterministically, so the request hash would vary; spec §6.7).
    /// The guard owns the transaction, the handler must not begin one.
    /// </summary>
    public async Task<TResponse> ExecuteAsync<TRequest, TResponse>(TRequest request, ServerCallContext context,
        Func<CancellationToken, Task<TResponse>> handler)
        where TRequest : IMessage<TRequest> where TResponse : IMessage<TResponse>, new()
    {
        var call = Parse(request, context);
        var ct = context.CancellationToken;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var replay = await ClaimAsync<TResponse>(call, ct);
        return replay ?? await RunAsync(call, tx, handler, ct);
    }

    /// <summary>
    /// Two-phase form for operations with a provider call: tx 1 claims and commits; <paramref name="callProvider"/> gets the provider
    /// idempotency key <c>bloom:&lt;operation&gt;:&lt;user_id&gt;:&lt;key&gt;</c>; tx 2 runs <paramref name="apply"/> and stores the outcome.
    /// A provider failure releases the key. Request messages must not use map fields; the guard owns the transactions, the handler must not begin one.
    /// </summary>
    public async Task<TResponse> ExecuteWithProviderAsync<TRequest, TProviderResult, TResponse>(TRequest request, ServerCallContext context,
        Func<string, CancellationToken, Task<TProviderResult>> callProvider,
        Func<TProviderResult, CancellationToken, Task<TResponse>> apply)
        where TRequest : IMessage<TRequest> where TResponse : IMessage<TResponse>, new()
    {
        var call = Parse(request, context);
        var ct = context.CancellationToken;
        await using (var tx1 = await db.Database.BeginTransactionAsync(ct))
        {
            var replay = await ClaimAsync<TResponse>(call, ct);
            if (replay is not null) return replay;
            await tx1.CommitAsync(ct);
        }

        TProviderResult result;
        try
        {
            result = await callProvider(call.ProviderKey, ct);
        }
        catch
        {
            await ReleaseAsync(call);
            throw;
        }

        while (true)
        {
            try
            {
                await using var tx2 = await db.Database.BeginTransactionAsync(ct);
                return await RunAsync(call, tx2, t => apply(result, t), ct);
            }
            catch (FenceLostException)
            {
                db.ChangeTracker.Clear(); // the re-run apply must not save what the lost run staged
            }
            catch (Exception ex) when (ex is not RpcException { StatusCode: var c } || !Stored.Contains(c))
            {
                await ReleaseAsync(call);
                throw;
            }

            // Our lock expired and another request took the key over: answer as a duplicate (its replay, or IN_PROGRESS).
            // If that request has since expired too, this claim takes the key over again and we apply once more.
            await using var tx3 = await db.Database.BeginTransactionAsync(ct);
            if (await ClaimAsync<TResponse>(call, ct) is { } replay) return replay;
            await tx3.CommitAsync(ct);
        }
    }

    /// <summary>
    /// Multi-step form for operations whose work is several calls and commits of its own (spec §4, checkout). Tx 1 claims the key and runs
    /// <paramref name="claimed"/> (new or taken over; a replay returns at once and runs neither step); a hook failure rolls Tx 1 back, so the key stays unclaimed.
    /// Then <paramref name="run"/> receives the hook's state and may begin and commit its own transactions on the same <c>DbContext</c>.
    /// When <paramref name="run"/> returns, or throws a deterministic rejection (the statuses the other forms store), the outcome is stored with a fenced update:
    /// <b>if <c>db.Database.CurrentTransaction</c> is open, in that transaction</b> (pending changes are saved and everything commits together), otherwise in a new one.
    /// Any other exception rolls back an open transaction, releases the key and is rethrown. A request that lost its lock claims again
    /// and answers as a duplicate (replay or <c>IN_PROGRESS</c>); if that claim takes the key over, the hook and <paramref name="run"/> run again.
    /// The other forms and their behaviour are unchanged. Request messages must not use map fields.
    /// </summary>
    public async Task<TResponse> ExecuteMultiStepAsync<TRequest, TState, TResponse>(TRequest request, ServerCallContext context,
        Func<IdempotencyClaim, CancellationToken, Task<TState>> claimed,
        Func<IdempotencyClaim, TState, CancellationToken, Task<TResponse>> run)
        where TRequest : IMessage<TRequest> where TResponse : IMessage<TResponse>, new()
    {
        var call = Parse(request, context);
        var ct = context.CancellationToken;
        while (true)
        {
            var (replay, claim, state) = await ClaimAndHookAsync<TResponse, TState>(call, claimed, ct);
            if (replay is not null) return replay;

            try
            {
                TResponse response;
                try
                {
                    response = await run(claim, state, ct);
                }
                catch (RpcException ex) when (Stored.Contains(ex.StatusCode))
                {
                    var status = ex.GetRpcStatus() ?? new Status { Code = (int)ex.StatusCode, Message = ex.Status.Detail };
                    await StoreOutcomeAsync(call, status.Code, status.ToByteArray(), ct);
                    throw;
                }
                await StoreOutcomeAsync(call, 0, response.ToByteArray(), ct);
                return response;
            }
            catch (FenceLostException)
            {
            }
            catch (Exception ex) when (ex is not RpcException { StatusCode: var c } || !Stored.Contains(c))
            {
                if (db.Database.CurrentTransaction is { } open) await open.RollbackAsync(CancellationToken.None);
                await ReleaseAsync(call);
                throw;
            }
            // Our lock expired and another request took the key over: loop to claim again (its replay, IN_PROGRESS, or another takeover).
        }
    }

    /// <summary>Tx 1: claim, then the hook and its <c>SaveChanges</c>, commit. Returns the replay instead when the key is already completed.</summary>
    private async Task<(TResponse? Replay, IdempotencyClaim Claim, TState State)> ClaimAndHookAsync<TResponse, TState>(Call call,
        Func<IdempotencyClaim, CancellationToken, Task<TState>> claimed, CancellationToken ct) where TResponse : IMessage<TResponse>, new()
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var replay = await ClaimAsync<TResponse>(call, ct);
        var claim = new IdempotencyClaim(call.UserId, call.Key, call.Resumed);
        if (replay is not null) return (replay, claim, default!);
        var state = await claimed(claim, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (default, claim, state);
    }

    /// <summary>Saves pending changes and stores the outcome in the open transaction, or in a new one, and commits. Fence lost: rolls back and throws <see cref="FenceLostException"/>.</summary>
    private async Task StoreOutcomeAsync(Call call, int responseStatus, byte[] body, CancellationToken ct)
    {
        await using var tx = db.Database.CurrentTransaction ?? await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await CompleteAsync(call, tx, responseStatus, body, ct);
        await tx.CommitAsync(ct);
    }

    private static Call Parse<TRequest>(TRequest request, ServerCallContext context) where TRequest : IMessage<TRequest>
    {
        var raw = context.RequestHeaders.GetValue("idempotency-key");
        if (raw is null)
            throw BloomErrors.Create(StatusCode.InvalidArgument, BloomErrors.IdempotencyKeyRequired, "The Idempotency-Key header is required.");
        if (raw.Length > 36 || !Guid.TryParseExact(raw, "D", out var key))
            throw BloomErrors.Create(StatusCode.InvalidArgument, BloomErrors.IdempotencyKeyInvalid, "The Idempotency-Key header must be a UUID.");
        if (!Guid.TryParse(context.RequestHeaders.GetValue("x-bloom-user-id"), out var userId))
            throw new InvalidOperationException("x-bloom-user-id is missing or not a UUID: the gateway must set it on every authenticated call.");
        return new Call(userId, key.ToString(), context.Method.TrimStart('/'), SHA256.HashData(request.ToByteArray()));
    }

    /// <summary>Returns the stored response to replay, or null when this caller now owns the key (new or taken over).</summary>
    private async Task<TResponse?> ClaimAsync<TResponse>(Call call, CancellationToken ct) where TResponse : IMessage<TResponse>, new()
    {
        while (true)
        {
            var inserted = await db.Database.SqlQuery<DateTime>($"""
                INSERT INTO idempotency_keys (user_id, key, operation, request_hash, status, locked_until)
                VALUES ({call.UserId}, {call.Key}, {call.Operation}, {call.Hash}, 'in_progress', now() + interval '60 seconds')
                ON CONFLICT DO NOTHING
                RETURNING locked_until AS "Value"
                """).ToListAsync(ct);
            if (inserted.Count == 1)
            {
                call.Fence = inserted[0];
                call.Resumed = false;
                metrics.Idempotency(call.Operation, "new");
                return default;
            }

            var row = (await db.Database.SqlQuery<ExistingRow>($"""
                SELECT operation AS "Operation", request_hash AS "RequestHash", status AS "Status",
                       locked_until > now() AS "Locked", response_status AS "ResponseStatus", response_body AS "ResponseBody"
                FROM idempotency_keys WHERE user_id = {call.UserId} AND key = {call.Key}
                """).ToListAsync(ct)).SingleOrDefault();
            if (row is null) continue; // released between our insert and our read: claim again

            if (row.Operation != call.Operation || !row.RequestHash.AsSpan().SequenceEqual(call.Hash))
                throw Reject(call, "reused", StatusCode.InvalidArgument, BloomErrors.IdempotencyKeyReused,
                    "This Idempotency-Key was already used for a different request.");

            if (row.Status == "completed")
            {
                metrics.Idempotency(call.Operation, "replay");
                if (row.ResponseStatus == 0)
                    return new MessageParser<TResponse>(() => new TResponse()).ParseFrom(row.ResponseBody);
                throw Status.Parser.ParseFrom(row.ResponseBody).ToRpcException();
            }

            if (!row.Locked)
            {
                var taken = await db.Database.SqlQuery<DateTime>($"""
                    UPDATE idempotency_keys SET locked_until = now() + interval '60 seconds'
                    WHERE user_id = {call.UserId} AND key = {call.Key} AND status = 'in_progress' AND locked_until <= now()
                    RETURNING locked_until AS "Value"
                    """).ToListAsync(ct);
                if (taken.Count == 1)
                {
                    call.Fence = taken[0];
                    call.Resumed = true;
                    metrics.Idempotency(call.Operation, "takeover");
                    return default;
                }
            }

            throw Reject(call, "in_progress", StatusCode.Aborted, BloomErrors.IdempotencyInProgress,
                "A request with this Idempotency-Key is still being processed; retry shortly.");
        }
    }

    private RpcException Reject(Call call, string result, StatusCode code, string reason, string message)
    {
        metrics.Idempotency(call.Operation, result);
        return BloomErrors.Create(code, reason, message);
    }

    /// <summary>Handler, save, stored outcome, commit. A deterministic rejection is rolled back to the savepoint, stored, and rethrown.</summary>
    private async Task<TResponse> RunAsync<TResponse>(Call call, IDbContextTransaction tx,
        Func<CancellationToken, Task<TResponse>> body, CancellationToken ct) where TResponse : IMessage<TResponse>
    {
        await tx.CreateSavepointAsync(Savepoint, ct);
        TResponse response;
        try
        {
            response = await body(ct);
            await db.SaveChangesAsync(ct);
        }
        catch (RpcException ex) when (Stored.Contains(ex.StatusCode))
        {
            await tx.RollbackToSavepointAsync(Savepoint, ct);
            var status = ex.GetRpcStatus() ?? new Status { Code = (int)ex.StatusCode, Message = ex.Status.Detail };
            await CompleteAsync(call, tx, status.Code, status.ToByteArray(), ct);
            await tx.CommitAsync(ct);
            throw;
        }

        await CompleteAsync(call, tx, 0, response.ToByteArray(), ct);
        await tx.CommitAsync(ct);
        return response;
    }

    /// <summary>Stores the outcome only while this request still owns the key; otherwise rolls everything back and throws <see cref="FenceLostException"/>.</summary>
    private async Task CompleteAsync(Call call, IDbContextTransaction tx, int responseStatus, byte[] body, CancellationToken ct)
    {
        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE idempotency_keys
            SET status = 'completed', response_status = {responseStatus}, response_body = {body}, completed_at = now()
            WHERE user_id = {call.UserId} AND key = {call.Key} AND status = 'in_progress' AND locked_until = {call.Fence}
            """, ct);
        if (updated == 1) return;
        await tx.RollbackAsync(ct);
        throw new FenceLostException();
    }

    /// <summary>Two-phase only: drops the in-progress row so the same key can retry. Not cancellable: a cancelled request must still release.</summary>
    private async Task ReleaseAsync(Call call)
    {
        try
        {
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM idempotency_keys WHERE user_id = {call.UserId} AND key = {call.Key} AND status = 'in_progress' AND locked_until = {call.Fence}", CancellationToken.None);
        }
        catch (Exception)
        {
            // ponytail: if the release itself fails, the row stays until locked_until (60 s), then a retry takes it over; the original error matters more.
        }
    }
}
