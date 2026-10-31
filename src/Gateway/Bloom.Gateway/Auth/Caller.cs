namespace Bloom.Gateway;

/// <summary>The validated identity of a request (LLD 07 §4.3); <c>UserType</c> is <c>customer</c> or <c>staff</c>. Times are Unix seconds.</summary>
public sealed record Caller(Guid UserId, string UserType, string? Role, long AuthTime, long IssuedAt)
{
    public bool IsStaff => UserType == "staff";
}
