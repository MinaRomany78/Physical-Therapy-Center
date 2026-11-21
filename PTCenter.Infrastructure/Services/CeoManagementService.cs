using System.Data;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Ceo;
using PTCenter.Application.Contracts.Users;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

public sealed class CeoManagementService(ApplicationDbContext db, TimeProvider clock)
{
    public async Task<ClinicIdentity> IdentityAsync(CancellationToken ct = default)
    {
        var s = await db.ClinicSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct) ?? new();
        return new(s.Name, s.Phone, s.Address, s.Email, s.Version, s.UpdatedAt, s.UpdatedBy);
    }
    public async Task<ClinicIdentity> SaveSettingsAsync(SettingsRequest r, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length < 2)
            throw new BookingRuleException("Ceo.SettingsValidation", "Enter a clinic name of at least two characters.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await ClinicMutationLock.AcquireAsync(db);
        var s = await db.ClinicSettings.SingleOrDefaultAsync(x => x.Id == 1, ct);
        if ((s?.Version ?? 0) != r.Version) throw new BookingRuleException("Ceo.SettingsConflict", "Settings changed. Reload before saving.", 409);
        if (s == null) { s = new(); db.ClinicSettings.Add(s); }
        s.Name = r.Name.Trim(); s.Phone = r.Phone?.Trim(); s.Address = r.Address?.Trim(); s.Email = r.Email?.Trim();
        s.Version++; s.UpdatedAt = clock.GetUtcNow().UtcDateTime; s.UpdatedBy = actor;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await IdentityAsync(ct);
    }

    public async Task<StaffPage> StaffAsync(StaffQuery q, CancellationToken ct)
    {
        var users = db.Users.AsNoTracking().Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.Name != "Patient")));
        if (!string.IsNullOrWhiteSpace(q.Search)) { var s = q.Search.Trim(); users = users.Where(u => u.FullName.Contains(s) || u.UserName!.Contains(s)); }
        if (q.Role != null) users = users.Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == q.Role)));
        if (q.Active != null) users = users.Where(u => u.IsActive == q.Active);
        var total = await users.CountAsync(ct);
        var page = await users.OrderBy(u => u.FullName).ThenBy(u => u.Id).Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        var ids = page.Select(u => u.Id).ToArray();
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ids.Contains(ur.UserId) select new { ur.UserId, r.Name }).ToListAsync(ct);
        var hours = await db.DoctorSchedules.AsNoTracking().Where(x => ids.Contains(x.DoctorId)).OrderBy(x => x.DayOfWeek).ThenBy(x => x.StartTime).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var work = db.Appointments.Where(a => (ids.Contains(a.DoctorId!) || ids.Contains(a.SeniorId!)) &&
            a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow && a.Status != AppointmentStatus.Completed &&
            (a.EndTime > now || a.TreatmentStartedAt != null && a.TreatmentEndedAt == null));
        var workCounts=await work.Where(a=>a.DoctorId!=null && ids.Contains(a.DoctorId)).Select(a=>a.DoctorId!)
            .Concat(work.Where(a=>a.SeniorId!=null && a.SeniorId!=a.DoctorId && ids.Contains(a.SeniorId)).Select(a=>a.SeniorId!))
            .GroupBy(id=>id).Select(g=>new{Id=g.Key,Count=g.Count()}).ToDictionaryAsync(x=>x.Id,x=>x.Count,ct);
        return new(page.Select(u => new StaffRow(u.Id, u.FullName, u.UserName!, u.Gender.ToString(), u.BirthDate,
            u.IsActive, roles.Where(r => r.UserId == u.Id).Select(r => r.Name!).ToArray(),
            hours.Where(x => x.DoctorId == u.Id).Select(x => new StaffWorkingHours(x.DayOfWeek, x.StartTime, x.EndTime)).ToArray(),
            workCounts.GetValueOrDefault(u.Id), u.ConcurrencyStamp ?? "")).ToArray(), total, q.Page, q.PageSize);
    }
    public async Task SetStaffStatusAsync(string id, StaffStatusRequest r, string actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await ClinicMutationLock.AcquireAsync(db);
        var u = await db.Users.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new BookingRuleException("Ceo.StaffNotFound", "Staff member was not found.", 404);
        if (u.ConcurrencyStamp != r.Version) throw new BookingRuleException("Ceo.StaffConflict", "Staff details changed. Reload before saving.", 409);
        if (!await db.UserRoles.AnyAsync(ur => ur.UserId == id && db.Roles.Any(role => role.Id == ur.RoleId && role.Name != "Patient"), ct))
            throw new BookingRuleException("Ceo.NotStaff", "Use the patient workflow for patient records.");
        if (!r.IsActive)
        {
            if (id == actor) throw new BookingRuleException("Ceo.SelfDeactivation", "You cannot deactivate your own account.", 409);
            await StaffSafety.AssertFutureWorkAsync(db, id, clock.GetUtcNow().UtcDateTime, null, null, true);
        }
        u.IsActive = r.IsActive; u.StatusChangedAt = clock.GetUtcNow().UtcDateTime; u.StatusChangedBy = actor;
        u.ConcurrencyStamp = Guid.NewGuid().ToString();
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}

public static class StaffSafety
{
    public static async Task AssertFutureWorkAsync(ApplicationDbContext db, string id, DateTime now,
        List<string>? roles, List<StaffWorkingHours>? hours, bool deactivate = false)
    {
        var work = await db.Appointments.AsNoTracking().Where(a => (a.DoctorId == id || a.SeniorId == id) &&
            a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow && a.Status != AppointmentStatus.Completed &&
            (a.EndTime > now || a.TreatmentStartedAt != null && a.TreatmentEndedAt == null))
            .Select(a => new { a.Id, a.StartTime, a.EndTime, a.DoctorId, a.SeniorId, a.TreatmentStartedAt, a.TreatmentEndedAt }).ToListAsync();
        var conflicts = work.Where(a => {
            if (deactivate) return true;
            if (roles != null && (a.SeniorId == id ? !roles.Contains("Senior") && !roles.Contains("Ceo") : !roles.Contains("Doctor") && !roles.Contains("Senior") && !roles.Contains("Ceo"))) return true;
            if (hours == null || hours.Count == 0) return false; // Existing model: no windows means unrestricted.
            var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(a.StartTime, DateTimeKind.Utc), ClinicCalendar.Zone);
            var end = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(a.EndTime, DateTimeKind.Utc), ClinicCalendar.Zone);
            return start.Date != end.Date || !hours.Any(h => h.DayOfWeek == (int)start.DayOfWeek && h.StartTime <= TimeOnly.FromDateTime(start) && h.EndTime >= TimeOnly.FromDateTime(end));
        }).ToArray();
        if (conflicts.Length > 0) throw new BookingRuleException("Ceo.FutureWorkConflict", "Resolve affected appointments through absence coverage or rescheduling before changing staff availability.", 409,
            new { affectedCount = conflicts.Length, appointments = conflicts.Take(20).Select(a => new { a.Id, a.StartTime, a.EndTime }) });
    }
}
