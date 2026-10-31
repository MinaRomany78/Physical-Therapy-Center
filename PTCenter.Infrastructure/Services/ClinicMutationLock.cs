using Microsoft.EntityFrameworkCore;
namespace PTCenter.Infrastructure.Services;

public static class ClinicMutationLock
{
    // Transaction-owned, cross-process lock. SQLite serializable transactions serialize writers.
    public static async Task AcquireAsync(PTCenter.Infrastructure.Persistence.ApplicationDbContext db)
    {
        if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlRawAsync("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'PTCenter:Scheduling', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000; IF @r<0 THROW 51001, 'Clinic scheduling changed concurrently. Retry after refreshing.', 1;");
    }
}
