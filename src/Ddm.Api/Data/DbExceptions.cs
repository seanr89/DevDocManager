using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ddm.Api.Data;

public static class DbExceptions
{
    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public static bool IsForeignKeyViolation(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };

    /// <summary>Postgres picked this transaction to abort to break a lock cycle (or a serialization failure): retrying is safe.</summary>
    public static bool IsDeadlock(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };
}
