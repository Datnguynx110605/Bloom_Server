using System.Text;
using System.Text.Json;
using Bloom.Shared.Errors;
using Bloom.Shared.Messaging;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client.Events;

namespace Bloom.Shared.DeadLetters;

/// <summary>DLQ admin (00-reliability §7.7, LLD-ADR-009): admin only, own service only, one pass per operation and one at a time per DLQ.</summary>
internal sealed class DeadLetterAdminService(
    BloomOptions options, ConsumerCatalog catalog, RabbitMqConnection connection, DeadLetterPass pass, OutboxWriter outbox, DbContext db)
    : DeadLetterAdmin.DeadLetterAdminBase
{
    private const string AuditMessageType = "identity.staff-action-recorded.v1";

    public override async Task<ListDeadLetterQueuesResponse> ListDeadLetterQueues(ListDeadLetterQueuesRequest request, ServerCallContext context)
    {
        Authorize(context, request.Service);
        var response = new ListDeadLetterQueuesResponse();
        foreach (var queue in catalog.DeadLetterQueues) // R6: a queue not declared yet counts 0
            response.Queues.Add(new DeadLetterQueue { Name = queue, MessageCount = await connection.QueueDepthAsync(queue, context.CancellationToken) });
        return response;
    }

    public override async Task<PeekDeadLettersResponse> PeekDeadLetters(PeekDeadLettersRequest request, ServerCallContext context)
    {
        Authorize(context, request.Service, request.Queue);
        var limit = request.Limit == 0 ? 20 : Math.Clamp(request.Limit, 1, 100); // R2
        var response = new PeekDeadLettersResponse();
        await RunLockedAsync(request.Queue, d =>
        {
            if (response.Messages.Count < limit) response.Messages.Add(ToDeadLetter(d));
            return PassAction.Keep;
        }, (_, _) => Task.CompletedTask, context.CancellationToken);
        return response;
    }

    public override async Task<Empty> ReplayDeadLetter(DeadLetterRequest request, ServerCallContext context)
    {
        await OneAsync(request, context, PassAction.Replay);
        return new Empty();
    }

    public override async Task<Empty> DiscardDeadLetter(DeadLetterRequest request, ServerCallContext context)
    {
        await OneAsync(request, context, PassAction.Discard);
        return new Empty();
    }

    public override async Task<ReplayAllDeadLettersResponse> ReplayAllDeadLetters(ReplayAllDeadLettersRequest request, ServerCallContext context)
    {
        var actor = Authorize(context, request.Service, request.Queue);
        var replayed = 0u;
        await RunLockedAsync(request.Queue, _ => PassAction.Replay, async (d, action) =>
        {
            await RecordAsync(actor, context, d, action, request.Queue);
            replayed++;
        }, context.CancellationToken);
        return new ReplayAllDeadLettersResponse { ReplayedCount = replayed };
    }

    /// <summary>
    /// One pass at a time per DLQ, across pods (LLD-ADR-011): a transaction-scoped advisory lock on the service's own database,
    /// held by one transaction on its own connection for the whole pass. COMMIT (or rollback on a throw, or a dead process) releases it.
    /// The per-message audit commits use the context's own connection, so they commit independently.
    /// </summary>
    private async Task RunLockedAsync(
        string queue, Func<BasicDeliverEventArgs, PassAction> decide, Func<BasicDeliverEventArgs, PassAction, Task> audit, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct); // disposed without COMMIT = rollback, which also releases the lock
        await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtextextended('bloom.dlq:' || @queue, 0))", connection, tx))
        {
            cmd.Parameters.AddWithValue("queue", queue);
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) throw DeadLetterPass.Busy(queue);
        }
        await pass.RunAsync(queue, decide, audit, ct);
        await tx.CommitAsync(CancellationToken.None);
    }

    private async Task OneAsync(DeadLetterRequest request, ServerCallContext context, PassAction wanted)
    {
        var actor = Authorize(context, request.Service, request.Queue);
        var matched = false;
        await RunLockedAsync(request.Queue,
            d =>
            {
                if (matched || d.BasicProperties.MessageId != request.MessageId) return PassAction.Keep;
                matched = true;
                return wanted;
            },
            (d, action) => RecordAsync(actor, context, d, action, request.Queue),
            context.CancellationToken);
        if (!matched)
            throw BloomErrors.Create(StatusCode.NotFound, BloomErrors.DlqMessageNotFound, $"Message '{request.MessageId}' is not in '{request.Queue}'.");
    }

    /// <summary>Role, service, queue, in that order (R5). Returns the actor's user id for the audit event.</summary>
    private Guid Authorize(ServerCallContext context, string service, string? queue = null)
    {
        if (context.RequestHeaders.GetValue("x-bloom-user-type") != "staff" || context.RequestHeaders.GetValue("x-bloom-role") != "admin")
            throw BloomErrors.Create(StatusCode.PermissionDenied, BloomErrors.RoleNotAllowed, "Only staff with the admin role may manage dead letters.");
        if (service != options.ServiceName || (queue is not null && !catalog.DeadLetterQueues.Contains(queue)))
            throw BloomErrors.Create(StatusCode.NotFound, BloomErrors.DlqNotFound, "No such dead-letter queue.");
        return Guid.TryParse(context.RequestHeaders.GetValue("x-bloom-user-id"), out var actor)
            ? actor
            : throw new InvalidOperationException("x-bloom-user-id is missing or not a UUID: the gateway must set it on every authenticated call.");
    }

    /// <summary>Step (2) of §7.7: commits one audit outbox row before the DLQ delivery is ACKed.</summary>
    private async Task RecordAsync(Guid actor, ServerCallContext context, BasicDeliverEventArgs d, PassAction action, string queue)
    {
        var details = JsonSerializer.SerializeToElement(
            new { queue, messageType = d.BasicProperties.Type, failureReason = Header(d, "x-failure-reason") }, BloomJson.Options);
        outbox.PublishEvent(AuditMessageType, new StaffActionRecorded(
            actor, context.RequestHeaders.GetValue("x-bloom-role")!, options.ServiceName,
            $"{options.ServiceName}.dlq-{(action == PassAction.Replay ? "replayed" : "discarded")}",
            "dlq_message", d.BasicProperties.MessageId ?? "", details));
        await db.SaveChangesAsync(context.CancellationToken);
    }

    private static DeadLetter ToDeadLetter(BasicDeliverEventArgs d) => new()
    {
        MessageId = d.BasicProperties.MessageId ?? "",
        MessageType = d.BasicProperties.Type ?? "",
        EnvelopeJson = Encoding.UTF8.GetString(d.Body.Span),
        FailureReason = Header(d, "x-failure-reason") ?? "",
        FailedAt = Header(d, "x-failed-at") ?? "",
    };

    private static string? Header(BasicDeliverEventArgs d, string name) => RabbitMqConnection.HeaderString(d.BasicProperties.Headers, name);
}
