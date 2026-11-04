using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bloom.Inventory.Data;

public static class Db
{
    /// <summary>True when <paramref name="e"/> is a unique violation (23505) of the named constraint or index.</summary>
    public static bool IsUniqueViolation(PostgresException e, string constraint) =>
        e is { SqlState: PostgresErrorCodes.UniqueViolation } && e.ConstraintName == constraint;

    public static bool IsUniqueViolation(DbUpdateException e, string constraint) =>
        e.InnerException is PostgresException pg && IsUniqueViolation(pg, constraint);

    /// <summary>True when <paramref name="e"/> is a check violation (23514) of the named constraint.</summary>
    public static bool IsCheckViolation(PostgresException e, string constraint) =>
        e is { SqlState: PostgresErrorCodes.CheckViolation } && e.ConstraintName == constraint;

    public static bool IsCheckViolation(DbUpdateException e, string constraint) =>
        e.InnerException is PostgresException pg && IsCheckViolation(pg, constraint);
}
