using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PTCenter.Infrastructure.Persistence;
namespace PTCenter.Api.Authorization;

public static class AttendanceAuthorization
{
    public static async Task<bool> AllowedAsync(ApplicationDbContext db, ClaimsPrincipal user, int id)
    {
        if (new[] { "Receptionist", "Ceo", "Admin", "Owner" }.Any(user.IsInRole)) return true;
        var actor = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return user.IsInRole("Senior") && await db.Appointments.AnyAsync(a => a.Id == id && a.SeniorId == actor && a.AppointmentType == "Assessment");
    }
}
