using System.Text.Json;

namespace Bloom.Shared.DeadLetters;

/// <summary>Payload of <c>identity.staff-action-recorded.v1</c> (06-identity §4.1 <c>audit_log</c>), published for every DLQ replay or discard.</summary>
public sealed record StaffActionRecorded(
    Guid ActorId, string ActorRole, string Service, string Action, string TargetType, string TargetId, JsonElement Details);
