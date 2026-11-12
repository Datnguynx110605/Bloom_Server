using System.Diagnostics;
using Bloom.Launch.Context;
using Bloom.Launch.Data;
using Bloom.Launch.Errors;
using Bloom.Launch.Observability;
using Bloom.Launch.Passes;
using Bloom.Launch.WaitingRoom;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using StackExchange.Redis;

namespace Bloom.Launch.Services;

/// <summary>01-launch §5.1 customer API: <c>GetLaunch</c>, join (§6.2) and status (§6.3). Redis trouble is plain <c>UNAVAILABLE</c> and never falls back to Postgres (owner item 2, LR-04).</summary>
public sealed class CustomerService(StateCache cache, IConnectionMultiplexer redis, PassTokens tokens, TimeProvider time, LaunchMetrics metrics, ILogger<CustomerService> logger) : LaunchCustomer.LaunchCustomerBase
{
    public override Task<PublicLaunch> GetLaunch(GetLaunchRequest request, ServerCallContext context) => Guarded(request.LaunchId, async () =>
    {
        var launch = await FindAsync(request.LaunchId, context.CancellationToken);
        var view = new PublicLaunch
        {
            Id = launch.Id.ToString("D"),
            Name = launch.Name,
            Stage = launch.Stage,
            LaunchTime = Timestamp.FromDateTimeOffset(launch.LaunchTime),
            ReleaseDate = Timestamp.FromDateTimeOffset(launch.ReleaseDate),
            WaitingRoomState = launch.RoomState(time.GetUtcNow()),
        };
        view.Products.AddRange(launch.Products.Select(p => new LaunchProduct { ProductId = p.ProductId.ToString("D"), PurchaseLimit = p.PurchaseLimit }));
        view.Skus.AddRange(launch.Skus.Select(s => new PublicSku { SkuId = s.SkuId.ToString("D"), ProductId = s.ProductId.ToString("D") }));
        return view;
    });

    public override Task<WaitingRoomStatus> JoinWaitingRoom(WaitingRoomRequest request, ServerCallContext context) => Guarded(request.LaunchId, async () =>
    {
        var account = Caller.From(context).RequireCustomer().UserId;
        var launch = await FindAsync(request.LaunchId, context.CancellationToken);
        var now = time.GetUtcNow();
        var room = launch.RoomState(now);
        if (room == RoomStates.NotOpen) throw LaunchErrors.Error(LaunchErrors.WaitingRoomNotOpen, "The waiting room is not open yet.");
        if (room == RoomStates.Closed) throw LaunchErrors.Error(LaunchErrors.WaitingRoomClosed, "The waiting room is closed.");
        if (room == RoomStates.SoldOut) return Polling(RoomStates.SoldOut);
        if (await AdmittedAsync(launch.Id, account) is { } admitted) return admitted;

        // Before launch time a random position in [0, 1); after it, 1 + seconds since launch time, so always behind them (LCH-R07).
        var score = now < launch.LaunchTime ? Random.Shared.NextDouble() : 1 + Math.Round((now - launch.LaunchTime).TotalMilliseconds) / 1000;
        await redis.GetDatabase().SortedSetAddAsync(RedisKeys.Queue(launch.Id), account.ToString("D"), score, When.NotExists);
        return await StatusAsync(launch, account, room);
    });

    public override Task<WaitingRoomStatus> GetWaitingRoomStatus(WaitingRoomRequest request, ServerCallContext context) => Guarded(request.LaunchId, async () =>
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var account = Caller.From(context).RequireCustomer().UserId;
            var launch = await FindAsync(request.LaunchId, context.CancellationToken);
            return await StatusAsync(launch, account, launch.RoomState(time.GetUtcNow()));
        }
        finally
        {
            metrics.StatusSeconds(Stopwatch.GetElapsedTime(start).TotalSeconds);
        }
    });

    /// <summary>§6.3 in owner item 1's order: a live pass, then a closed room, then the queue position.</summary>
    private async Task<WaitingRoomStatus> StatusAsync(LaunchSnapshot launch, Guid account, string room)
    {
        if (await AdmittedAsync(launch.Id, account) is { } admitted) return admitted;
        if (room == RoomStates.Closed) return new() { State = RoomStates.Closed };
        if (await redis.GetDatabase().SortedSetRankAsync(RedisKeys.Queue(launch.Id), account.ToString("D")) is { } rank)
            return Polling("queued", (int)rank + 1);
        return room switch
        {
            RoomStates.SoldOut => Polling(RoomStates.SoldOut),
            RoomStates.NotOpen => Polling(RoomStates.NotOpen),
            _ => throw LaunchErrors.Error(LaunchErrors.NotInQueue, "You are not in the waiting room."),
        };
    }

    /// <summary><c>admitted</c> with a token signed from the stored <c>passId|expiresAtUnixMs</c>, or null when there is no pass.</summary>
    private async Task<WaitingRoomStatus?> AdmittedAsync(Guid launch, Guid account)
    {
        var stored = await redis.GetDatabase().StringGetAsync(RedisKeys.Pass(launch, account));
        var parts = stored.IsNull ? [] : ((string)stored!).Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var passId) || !long.TryParse(parts[1], out var expires)) return null;
        return new()
        {
            State = "admitted",
            PassToken = tokens.Sign(passId, account, launch, expires),
            PassExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(expires)),
        };
    }

    private static WaitingRoomStatus Polling(string state, int position = 0) =>
        new() { State = state, Position = position, PollAfterSeconds = Random.Shared.Next(4, 7) };

    /// <summary>R8: an id that is not a UUID is an unknown launch; a draft is never visible.</summary>
    private async Task<LaunchSnapshot> FindAsync(string id, CancellationToken ct)
    {
        var launch = Guid.TryParseExact(id, "D", out var launchId) ? await cache.GetAsync(launchId, ct) : null;
        return launch is { Stage: not LaunchStages.Draft } ? launch : throw LaunchErrors.NotFound("The launch");
    }

    private async Task<T> Guarded<T>(string launch, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception e) when (e is RedisException or RedisTimeoutException)
        {
            logger.LogWarning("Redis failed ({Error}) for launch {LaunchId}; answering UNAVAILABLE.", e.GetType().Name, launch); // no exception object: no stack trace per request, no Redis key
            throw LaunchErrors.Unavailable("The waiting room is unavailable.");
        }
    }
}
