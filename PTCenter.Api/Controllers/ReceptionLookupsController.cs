using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTCenter.Api.Authorization;
using PTCenter.Application.Contracts.Receptionist;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Api.Controllers;

[ApiController]
[Route("api/Receptionist")]
[Authorize(Roles = ReceptionPermissions.StaffRead)]
public class ReceptionLookupsController(ApplicationDbContext db) : ControllerBase
{
    [HttpGet("clinicians")]
    public async Task<ActionResult<List<ReceptionClinicianOption>>> Clinicians()
    {
        var now = DateTimeOffset.UtcNow;
        var memberships = await (from user in db.Users.AsNoTracking()
            join membership in db.UserRoles on user.Id equals membership.UserId
            join role in db.Roles on membership.RoleId equals role.Id
            where role.Name == "Doctor" || role.Name == "Senior"
            select new { user.Id, user.FullName, user.Gender, user.LockoutEnd, Role = role.Name! }).ToListAsync();
        memberships = memberships.Where(u => u.LockoutEnd == null || u.LockoutEnd <= now).ToList();
        var ids = memberships.Select(u => u.Id).Distinct().ToList();
        var hours = await db.DoctorSchedules.AsNoTracking().Where(h => ids.Contains(h.DoctorId)).ToListAsync();
        var absences = await db.DoctorAbsences.AsNoTracking().Where(a => ids.Contains(a.DoctorId)).ToListAsync();
        var active = await db.Appointments.AsNoTracking().Where(a => a.Status == AppointmentStatus.InProgress && (a.DoctorId != null && a.TreatmentStartedAt != null && a.TreatmentEndedAt == null || a.SeniorId != null && a.ActualCheckIn != null && a.ActualCheckOut == null))
            .Select(a => new { a.DoctorId, a.SeniorId }).ToListAsync();
        return memberships.GroupBy(u => u.Id).Select(group => new ReceptionClinicianOption(group.Key, group.First().FullName,
            group.First().Gender.ToString(), group.Select(u => u.Role).Distinct().Order().ToList(),
            active.Count(a => a.DoctorId == group.Key || a.SeniorId == group.Key),
            hours.Where(h => h.DoctorId == group.Key).OrderBy(h => h.DayOfWeek).ThenBy(h => h.StartTime).Select(h => new ReceptionWorkingHours(h.DayOfWeek, h.StartTime, h.EndTime)).ToList(),
            absences.Where(a => a.DoctorId == group.Key).Select(a => a.AbsentDate).Distinct().Order().ToList()))
            .OrderBy(u => u.Name).ToList();
    }
}
