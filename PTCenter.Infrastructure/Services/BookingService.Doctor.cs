using Microsoft.EntityFrameworkCore;
using PTCenter.Domain.Entities;
namespace PTCenter.Infrastructure.Services;

public partial class BookingService
{
    public async Task RequireEligibleDoctorAsync(int patientId, string doctorId)
    {
        if (!(await CliniciansForAsync(await PatientAsync(patientId), "Doctor")).Any(c => c.Id == doctorId))
            throw Rule("ClinicianEligibility", "Select an eligible Doctor matching the patient.", 409);
    }
    public async Task ValidateDoctorStartAsync(Appointment appointment, DateTime now)
    {
        var patient = await PatientAsync(appointment.PatientId);
        if (!(await CliniciansForAsync(patient, "Doctor")).Any(c => c.Id == appointment.DoctorId))
            throw Rule("ClinicianEligibility", "The assigned Doctor is not eligible for this patient.", 409);
        if (appointment.InsuranceFunded && !(await ContextAsync(appointment.PatientId)).InsuranceApproved)
            throw Rule("InsuranceApprovalRequired", "Verified insurance approval is required.", 409);
        var hours = await db.DoctorSchedules.Where(s => s.DoctorId == appointment.DoctorId).ToListAsync();
        var absences = await db.DoctorAbsences.Where(s => s.DoctorId == appointment.DoctorId).ToListAsync();
        var conflicts = WorkingTimeConflicts(new DateTimeOffset(DateTime.SpecifyKind(appointment.StartTime, DateTimeKind.Utc)), hours, absences).Where(c => c != "TimeInPast").ToList();
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(now, DateTimeKind.Utc), ClinicTimeZone);
        if (!hours.Any(s => s.DayOfWeek == (int)local.DayOfWeek && s.StartTime <= TimeOnly.FromDateTime(local) && s.EndTime > TimeOnly.FromDateTime(local))) conflicts.Add("OutsideWorkingHours");
        if (absences.Any(a => a.AbsentDate == DateOnly.FromDateTime(local))) conflicts.Add("ClinicianAbsent");
        if (!await db.Rooms.AnyAsync(r => r.Id == appointment.RoomId && r.IsActive && r.Capacity > 0)) conflicts.Add("RoomRequired");
        var others = await db.Appointments.AsNoTracking().Where(a => a.Id != appointment.Id && a.Status != PTCenter.Domain.Entities.Enums.AppointmentStatus.Completed && a.Status != PTCenter.Domain.Entities.Enums.AppointmentStatus.Cancelled && a.Status != PTCenter.Domain.Entities.Enums.AppointmentStatus.NoShow).ToListAsync();
        var capacity = await db.Rooms.Where(r => r.Id == appointment.RoomId && r.IsActive).Select(r => r.Capacity).SingleOrDefaultAsync();
        conflicts.AddRange(ClinicalCapacity.Conflicts(now, now.AddMinutes(60), appointment.PatientId, appointment.DoctorId!, appointment.RoomId ?? 0, others, true, capacity, actual:true));
        if (conflicts.Count > 0) throw new ClinicalRuleException("Doctor.CapacityOrEligibility", "Treatment cannot start. Review the reported room, clinician or schedule conflict.", 409, new { conflicts = conflicts.Distinct().ToList() });
    }
}
