using System.Net.Sockets;
using Npgsql;

namespace Bloom.Shared.Messaging;

/// <summary>
/// Classifies "the service's own PostgreSQL can't be reached" (HLD-ADR-019). Only connection-level failures count:
/// an <see cref="NpgsqlException"/> caused by a socket error, a timeout or a broken stream, and the server's own
/// 57P01 (admin shutdown) / 57P03 (cannot connect now). Query errors such as 23505, 57014 or 40001 are not outages.
/// </summary>
internal static class DatabaseOutage
{
    public static bool IsOutage(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg)
            {
                if (pg.SqlState is "57P01" or "57P03") return true;
            }
            else if (e is NpgsqlException { InnerException: SocketException or TimeoutException or IOException })
            {
                return true;
            }
        }
        return false;
    }
}
