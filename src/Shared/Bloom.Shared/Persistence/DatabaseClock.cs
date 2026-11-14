using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Bloom.Shared.Persistence;

public static class DatabaseClock
{
    /// <summary>The database's <c>now()</c>, the clock of every stored timestamp and time check (00-reliability §10). Inside a transaction it equals that transaction's <c>now()</c>.</summary>
    public static Task<DateTimeOffset> NowAsync(this DatabaseFacade database, CancellationToken ct = default) =>
        database.SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"").SingleAsync(ct);
}
