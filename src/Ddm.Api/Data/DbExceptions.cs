using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ddm.Api.Data;

public static class DbExceptions
{
    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
