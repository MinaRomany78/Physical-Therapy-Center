using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Infrastructure.Services;

public partial class BookingService
{
    private IQueryable<Appointment> RecoveryAppointments => db.Appointments.Include(a => a.Patient).ThenInclude(p => p.ApplicationUser)
        .Include(a => a.Doctor).Include(a => a.Room).Include(a => a.TreatmentPlanSession);
    private static (DateTime Start, DateTime End) AbsencePeriod(AbsenceRequest r)
    {
        var last = r.EndDate ?? r.Date;
        if (last < r.Date || last.DayNumber - r.Date.DayNumber > 366 || last == DateOnly.MaxValue) throw Rule("AbsencePeriod", "Choose an absence period of at most one year.");
        DateTime Local(DateOnly day, TimeOnly time)
        {
            var wall = day.ToDateTime(time, DateTimeKind.Unspecified);
            if (ClinicCalendar.Zone.IsInvalidTime(wall) || ClinicCalendar.Zone.IsAmbiguousTime(wall)) throw Rule("AbsenceTime", "This clinic time is ambiguous or does not exist. Choose another time.");
            return TimeZoneInfo.ConvertTimeToUtc(wall, ClinicCalendar.Zone);
        }
        var start = r.FullDay ? ClinicCalendar.DayStart(r.Date) : Local(r.Date, r.StartTime ?? throw Rule("AbsenceStart", "Enter a start time."));
        var end = r.FullDay ? ClinicCalendar.DayStart(last.AddDays(1)) : Local(last, r.EndTime ?? throw Rule("AbsenceEnd", "Enter an end time."));
        if (end <= start) throw Rule("AbsencePeriod", "The end must follow the start.");
        if (r.Note?.Length > 500) throw Rule("AbsenceNote", "Use at most 500 characters.");
        return (start, end);
    }
    private async Task<(string Name, string Role)> RecoveryClinician(string id, bool assessment = false)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id) ?? throw Rule("ClinicianEligibility", "Select an eligible clinician.");
        if (!user.IsActive || user.LockoutEnd > DateTimeOffset.UtcNow) throw Rule("ClinicianEligibility", "This clinician is unavailable.", 409);
        var roles = await (from membership in db.UserRoles join role in db.Roles on membership.RoleId equals role.Id where membership.UserId == id select role.Name).ToListAsync();
        var kind = roles.Contains("Senior") ? "Senior" : roles.Contains("Doctor") ? "Doctor" : "";
        if (kind == "" || assessment && kind != "Senior") throw Rule("ClinicianEligibility", "Initial assessments require an eligible Senior.", 409);
        return (user.FullName, kind);
    }
    private async Task<RecoveryBooking> RecoveryRow(Appointment a, List<DoctorAbsence> absences)
    {
        var clinician = a.DoctorId ?? a.SeniorId ?? "";
        var ids = absences.Where(x => x.DoctorId == clinician && ClinicianAbsenceRules.Overlaps(x, a.StartTime, a.EndTime)).Select(x => x.Id).ToList();
        var contact = await db.AppointmentScheduleChanges.AsNoTracking().Where(c => c.AppointmentId == a.Id && c.AbsenceId != null && ids.Contains(c.AbsenceId.Value) && c.ContactOutcome != null)
            .OrderByDescending(c => c.ChangedAt).ThenByDescending(c => c.Id).Select(c => c.ContactOutcome).FirstOrDefaultAsync();
        return new(a.Id, a.PatientId, a.Patient.ApplicationUser.FullName, a.Patient.ApplicationUser.Gender.ToString(), a.AppointmentType, clinician,
            await db.Users.Where(u => u.Id == clinician).Select(u => u.FullName).SingleOrDefaultAsync() ?? "", a.RoomId, a.Room?.Name, a.StartTime, a.EndTime, ScheduleVersion(a), ids, contact ?? "AwaitingResponse");
    }
    public async Task<List<RecoveryBooking>> AffectedBookingsAsync(DateOnly? date = null)
    {
        var absences = await db.DoctorAbsences.AsNoTracking().Where(a => !a.Withdrawn).ToListAsync();
        var query = RecoveryAppointments.AsNoTracking().Where(a => a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.InProgress);
        if (date != null) { var start = ClinicCalendar.DayStart(date.Value); var end = ClinicCalendar.DayStart(date.Value.AddDays(1)); query = query.Where(a => a.StartTime < end && a.EndTime > start); }
        var rows = new List<RecoveryBooking>();
        foreach (var a in (await query.OrderBy(a => a.StartTime).ThenBy(a => a.Id).ToListAsync()).Where(ClinicianAbsenceRules.Unstarted))
        {
            if (absences.Any(x => x.DoctorId == (a.DoctorId ?? a.SeniorId) && ClinicianAbsenceRules.Overlaps(x, a.StartTime, a.EndTime))) rows.Add(await RecoveryRow(a, absences));
        }
        return rows;
    }
    public async Task<AbsenceImpact> AbsenceImpactAsync(AbsenceRequest r)
    {
        await RecoveryClinician(r.ClinicianId); var period = AbsencePeriod(r);
        var rows = await RecoveryAppointments.AsNoTracking().Where(a => (a.DoctorId == r.ClinicianId || a.SeniorId == r.ClinicianId) && a.StartTime < period.End && a.EndTime > period.Start).OrderBy(a => a.StartTime).ToListAsync();
        var result = new List<RecoveryBooking>(); foreach (var a in rows.Where(ClinicianAbsenceRules.Unstarted)) result.Add(await RecoveryRow(a, []));
        return new(period.Start, period.End, ClinicCalendar.Zone.Id, result);
    }
    private async Task<AbsenceView> AbsenceView(DoctorAbsence a)
    {
        var clinician = await db.Users.Where(u => u.Id == a.DoctorId).Select(u => u.FullName).SingleOrDefaultAsync() ?? "";
        var role = await (from m in db.UserRoles join r in db.Roles on m.RoleId equals r.Id where m.UserId == a.DoctorId orderby r.Name descending select r.Name).FirstOrDefaultAsync() ?? "";
        var period = ClinicianAbsenceRules.Interval(a); var affected = (await AffectedBookingsAsync()).Where(b => b.AbsenceIds.Contains(a.Id)).ToList();
        return new(a.Id, a.DoctorId, clinician, role, period.Start, period.End, a.Reason, a.Version, a.Withdrawn,
            await db.Users.Where(u => u.Id == a.CreatedBy).Select(u => u.FullName).SingleOrDefaultAsync(), a.CreatedAt, JsonSerializer.Deserialize<List<AbsenceAudit>>(a.HistoryJson) ?? [], affected);
    }
    public async Task<List<AbsenceView>> AbsencesAsync(DateOnly? date = null)
    {
        var rows = await db.DoctorAbsences.AsNoTracking().OrderByDescending(a => a.Id).ToListAsync();
        if (date != null) { var start = ClinicCalendar.DayStart(date.Value); var end = ClinicCalendar.DayStart(date.Value.AddDays(1)); rows = rows.Where(a => { var p = ClinicianAbsenceRules.Interval(a); return p.Start < end && p.End > start; }).ToList(); }
        var result = new List<AbsenceView>(); foreach (var a in rows) result.Add(await AbsenceView(a)); return result;
    }
    public async Task<AbsenceView> SaveAbsenceAsync(int? id, AbsenceRequest r, string actor, bool withdraw = false)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(r.OperationKey) || r.OperationKey.Length > 100) throw Rule("AbsenceRequest", "An authenticated recorder and stable operation key are required.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable); await ClinicMutationLock.AcquireAsync(db);
        var replay = id == null ? await db.DoctorAbsences.SingleOrDefaultAsync(a => a.OperationKey == r.OperationKey) : null;
        if (replay != null) { var period = AbsencePeriod(r); if (replay.DoctorId != r.ClinicianId || replay.StartsAt != period.Start || replay.EndsAt != period.End || replay.Reason != r.Note?.Trim() || replay.CreatedBy != actor) throw Rule("RetryKeyConflict", "This operation key belongs to different absence details.", 409); await transaction.CommitAsync(); return await AbsenceView(replay); }
        var absence = id == null ? new DoctorAbsence() : await db.DoctorAbsences.SingleOrDefaultAsync(a => a.Id == id) ?? throw Rule("AbsenceNotFound", "Absence was not found.", 404);
        var history = JsonSerializer.Deserialize<List<AbsenceAudit>>(absence.HistoryJson) ?? [];
        var fingerprint = Hash(new { id, r, withdraw, actor }); var previousOperation = history.FirstOrDefault(h => h.OperationKey == r.OperationKey);
        if (previousOperation != null) { if (previousOperation.RequestHash != fingerprint || previousOperation.ActorId != actor) throw Rule("RetryKeyConflict", "The retry key belongs to different absence details.", 409); await transaction.CommitAsync(); return await AbsenceView(absence); }
        if (id != null && absence.Version != r.ExpectedVersion) throw Rule("StaleAbsence", "This absence changed. Reload its details.", 409);
        if (id != null && absence.DoctorId != r.ClinicianId) throw Rule("AbsenceClinician", "Correct the period for the same clinician, or record a separate absence.");
        await RecoveryClinician(r.ClinicianId); var range = AbsencePeriod(r); var previous = id == null ? ((DateTime Start, DateTime End)?)null : ClinicianAbsenceRules.Interval(absence);
        var name = await db.Users.Where(u => u.Id == actor).Select(u => u.FullName).SingleAsync();
        history.Add(new(withdraw ? "Withdraw" : id == null ? "Record" : "Correct", actor, name, DateTime.UtcNow, previous?.Start, previous?.End, range.Start, range.End, r.Note?.Trim(), r.OperationKey, fingerprint));
        absence.DoctorId = r.ClinicianId; absence.AbsentDate = r.Date; absence.StartsAt = range.Start; absence.EndsAt = range.End; absence.Reason = r.Note?.Trim(); absence.Withdrawn = withdraw; absence.Version = id == null ? 1 : absence.Version + 1;
        absence.HistoryJson = JsonSerializer.Serialize(history); if (id == null) { absence.CreatedBy = actor; absence.OperationKey = r.OperationKey; db.DoctorAbsences.Add(absence); } else absence.UpdatedBy = actor;
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return await AbsenceView(absence);
    }
    private async Task<Appointment> RecoveryAppointment(int id)
    {
        var a = await RecoveryAppointments.SingleOrDefaultAsync(a => a.Id == id) ?? throw Rule("AppointmentNotFound", "Appointment was not found.", 404);
        if (!ClinicianAbsenceRules.Unstarted(a)) throw Rule("AlreadyStarted", "Only appointments that have not started can be recovered.", 409);
        if (a.AppointmentType != "Session" && a.AppointmentType != "Assessment") throw Rule("AppointmentType", "This appointment type needs staff review.", 409);
        if (a.AppointmentType == "Session")
        {
            var plan = await db.TreatmentPlans.AsNoTracking().Include(p => p.Assessment).SingleOrDefaultAsync(p => a.TreatmentPlanSession != null && p.Id == a.TreatmentPlanSession.TreatmentPlanId);
            if (plan == null || plan.Status != StatusPlan.Active || plan.Assessment.CompletedAt == null) throw Rule("PlanRequired", "Recovery requires the existing active treatment plan and a completed assessment.", 409);
            if (a.InsuranceFunded && !(await ContextAsync(a.PatientId)).InsuranceApproved) throw Rule("InsuranceApprovalRequired", "Current insurance approval is required before confirming treatment coverage.", 409);
        }
        return a;
    }
    private async Task<List<string>> RecoveryConflicts(Appointment a, string clinician, string role, DateTime start, int room)
    {
        var hours = await db.DoctorSchedules.AsNoTracking().Where(h => h.DoctorId == clinician).ToListAsync(); var absences = await db.DoctorAbsences.AsNoTracking().Where(x => x.DoctorId == clinician && !x.Withdrawn).ToListAsync();
        var duration = a.EndTime - a.StartTime;
        var conflicts = WorkingTimeConflicts(new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc)), hours, absences, duration);
        if((await WeeklyLimitAsync(a))?.Conflict(start) is {} frequencyConflict)conflicts.Add(frequencyConflict);
        // Keep-time replacement remains possible for an arrived, unstarted appointment whose scheduled time just passed.
        if (start == a.StartTime) conflicts.Remove("TimeInPast");
        var target = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(r => r.Id == room && r.IsActive && r.Capacity > 0);
        if (target == null) { conflicts.Add("RoomRequired"); return conflicts; }
        var others = await db.Appointments.AsNoTracking().Where(x => x.Id != a.Id && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow && x.Status != AppointmentStatus.Completed).ToListAsync();
        conflicts.AddRange(ClinicalCapacity.Conflicts(start, start + duration, a.PatientId, clinician, room, others, role == "Doctor" && a.AppointmentType == "Session", target.Capacity, actual: true));
        return conflicts.Distinct().ToList();
    }
    public async Task<RecoveryOptions> RecoveryOptionsAsync(int id, DateOnly? date = null)
    {
        var a = await RecoveryAppointment(id); var absences = await db.DoctorAbsences.AsNoTracking().Where(x => !x.Withdrawn).ToListAsync(); var row = await RecoveryRow(a, absences);
        if (row.AbsenceIds.Count == 0) throw Rule("RecoveryResolved", "This appointment no longer needs absence recovery.", 409);
        var seniors = await ReadCliniciansAsync("Senior", null); var clinicians = a.AppointmentType == "Assessment" ? seniors : seniors.Concat(await ReadCliniciansAsync("Doctor", null)).DistinctBy(c => c.Id).ToList();
        var rooms = await db.Rooms.AsNoTracking().Where(r => r.IsActive && r.Capacity > 0).OrderByDescending(r => r.Id == a.RoomId).ThenBy(r => r.Id).ToListAsync();
        var allHours = await db.DoctorSchedules.AsNoTracking().ToListAsync();
        var otherAppointments = await db.Appointments.AsNoTracking().Where(x => x.Id != a.Id && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow && x.Status != AppointmentStatus.Completed).ToListAsync();
        var weeklyLimit=await WeeklyLimitAsync(a);
        var matching = new List<RecoveryCandidate>(); var exceptions = new List<RecoveryCandidate>(); var nearest = new List<RecoveryCandidate>();
        var duration = a.EndTime - a.StartTime;
        RecoveryCandidate? Candidate(BookingClinician clinician, DateTime start)
        {
            if(weeklyLimit?.Conflict(start)!=null)return null;
            var working = WorkingTimeConflicts(new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc)), allHours.Where(h => h.DoctorId == clinician.Id), absences.Where(x => x.DoctorId == clinician.Id), duration);
            if (start == a.StartTime) working.Remove("TimeInPast"); if (working.Count > 0) return null;
            foreach (var room in rooms) if (ClinicalCapacity.Conflicts(start, start + duration, a.PatientId, clinician.Id, room.Id, otherAppointments, clinician.Role == "Doctor" && a.AppointmentType == "Session", room.Capacity, actual: true).Count == 0) return new(clinician.Id, clinician.Name, clinician.Role, clinician.Gender, clinician.Gender == row.PatientGender, a.AppointmentType == "Session" && clinician.Role == "Senior", start, start + duration, room.Id, room.Name);
            return null;
        }
        foreach (var clinician in clinicians) { var c = Candidate(clinician, a.StartTime); if (c != null) (c.GenderMatch ? matching : exceptions).Add(c); }
        if (date != null || matching.Count == 0)
        {
            var first = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(a.StartTime, DateTimeKind.Utc), ClinicCalendar.Zone));
            foreach (var clinician in clinicians.Where(c => c.Gender == row.PatientGender))
            {
                var hours = allHours.Where(h => h.DoctorId == clinician.Id).ToList(); var found = 0;
                var localStarts = new HashSet<DateTime>();
                for (var offset = 0; offset < (date == null ? 7 : 1); offset++)
                {
                    var day = first.AddDays(offset);
                    foreach (var hour in hours.Where(h => h.DayOfWeek == (int)day.DayOfWeek))
                        for (var wall = day.ToDateTime(hour.StartTime); wall + duration <= day.ToDateTime(hour.EndTime); wall = wall.AddMinutes(30))
                            if (!ClinicCalendar.Zone.IsInvalidTime(wall) && !ClinicCalendar.Zone.IsAmbiguousTime(wall)) localStarts.Add(TimeZoneInfo.ConvertTimeToUtc(wall, ClinicCalendar.Zone));
                }
                foreach (var start in localStarts.Where(s => s != a.StartTime && s > DateTime.UtcNow).OrderBy(s => Math.Abs((s - a.StartTime).Ticks)).ThenBy(s => s))
                {
                    var c = Candidate(clinician, start); if (c != null) { nearest.Add(c); if (++found >= 4) break; }
                }
            }
        }
        return new(row, matching, exceptions, nearest.OrderBy(c => Math.Abs((c.Start - a.StartTime).Ticks)).ThenBy(c => c.Start).ToList(), 1, ClinicCalendar.Zone.Id);
    }
    private async Task<AppointmentScheduleChange?> RecoveryReplay(string key, string fingerprint, string actor)
    {
        var saved = await db.AppointmentScheduleChanges.AsNoTracking().SingleOrDefaultAsync(c => c.OperationKey == key);
        if (saved != null && (saved.RequestHash != fingerprint || saved.ActorId != actor)) throw Rule("RetryKeyConflict", "The retry key belongs to another operation.", 409); return saved;
    }
    private async Task<AppointmentScheduleChange> RecoveryAudit(Appointment a, string key, string fingerprint, int absence, string actor, string action, string? reason)
    {
        var old = a.DoctorId ?? a.SeniorId ?? "";
        var oldName = await db.Users.Where(u => u.Id == old).Select(u => u.FullName).SingleOrDefaultAsync();
        return new()
        {
            AppointmentId = a.Id,
            PatientId = a.PatientId,
            TreatmentPlanId = a.TreatmentPlanSession?.TreatmentPlanId,
            OperationKey = key,
            RequestHash = fingerprint,
            AbsenceId = absence,
            ActorId = actor,
            ActorName = await db.Users.Where(u => u.Id == actor).Select(u => u.FullName).SingleAsync(),
            ChangedAt = DateTime.UtcNow,
            PreviousClinicianId = old,
            PreviousClinicianName = oldName,
            ClinicianId = old,
            ClinicianName = oldName,
            PreviousStart = a.StartTime,
            PreviousEnd = a.EndTime,
            NewStart = a.StartTime,
            NewEnd = a.EndTime,
            PreviousRoomId = a.RoomId,
            PreviousRoomName = a.Room?.Name ?? "",
            NewRoomId = a.RoomId ?? 0,
            NewRoomName = a.Room?.Name ?? "",
            Action = action,
            Reason = reason?.Trim()
        };
    }
    private async Task ValidateAffected(Appointment a, int absence, string version)
    {
        if (ScheduleVersion(a) != version) throw Rule("StaleSchedule", "This appointment changed. Your selection is retained; reload the available options.", 409);
        var x = await db.DoctorAbsences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == absence);
        if (x == null || x.DoctorId != (a.DoctorId ?? a.SeniorId) || !ClinicianAbsenceRules.Overlaps(x, a.StartTime, a.EndTime)) throw Rule("RecoveryResolved", "The recorded absence no longer affects this booking.", 409);
    }
    public async Task<RecoveryReceipt> ResolveAbsenceAsync(int id, RecoveryRequest r, string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(r.OperationKey) || r.OperationKey.Length > 100 || string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 500 || r.Action is not ("Replace" or "Reschedule")) throw Rule("RecoveryRequest", "Enter a reason and a valid recovery action.");
        var fingerprint = Hash(new { id, r, actor }); await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable); await ClinicMutationLock.AcquireAsync(db);
        var replay = await RecoveryReplay(r.OperationKey, fingerprint, actor); if (replay != null) { await tx.CommitAsync(); var current = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id); return new(id, r.OperationKey, replay.Action, ScheduleVersion(current)); }
        var a = await RecoveryAppointment(id); await ValidateAffected(a, r.AbsenceId, r.ExpectedVersion); var clinician = await RecoveryClinician(r.ClinicianId, a.AppointmentType == "Assessment");
        var gender = await db.Users.Where(u => u.Id == r.ClinicianId).Select(u => u.Gender).SingleAsync(); var mismatch = gender != a.Patient.ApplicationUser.Gender;
        if (mismatch && (!r.UseException || !r.PhoneConsent || string.IsNullOrWhiteSpace(r.ExceptionReason) || r.ExceptionReason.Length > 500)) throw Rule("PhoneConsentRequired", "Use exception and confirm patient agreement by phone with a reason.", 409);
        if (r.UseException && !mismatch) throw Rule("ExceptionNotRequired", "Use a normal replacement for a matching clinician.");
        if (r.Action == "Replace" && r.StartTime.UtcDateTime != a.StartTime) throw Rule("KeepTime", "A replacement must keep the original time.");
        var conflicts = await RecoveryConflicts(a, r.ClinicianId, clinician.Role, r.StartTime.UtcDateTime, r.RoomId);
        if (conflicts.Count > 0) throw Rule("RecoveryConflict", "This option is no longer available: " + string.Join(", ", conflicts) + ". Refresh the options.", 409);
        var audit = await RecoveryAudit(a, r.OperationKey, fingerprint, r.AbsenceId, actor, r.Action, r.Reason);
        audit.ClinicianId = r.ClinicianId; audit.ClinicianName = clinician.Name; audit.NewStart = r.StartTime.UtcDateTime; audit.NewEnd = audit.NewStart + (a.EndTime - a.StartTime); audit.NewRoomId = r.RoomId; audit.NewRoomName = await db.Rooms.Where(x => x.Id == r.RoomId).Select(x => x.Name).SingleAsync();
        audit.GenderException = mismatch; audit.ConsentMethod = mismatch ? "Phone" : null; audit.ConsentRecordedAt = mismatch ? DateTime.UtcNow : null; audit.ConsentReason = mismatch ? r.ExceptionReason?.Trim() : null; audit.ContactOutcome = "Agreed";
        a.DoctorId = clinician.Role == "Doctor" ? r.ClinicianId : null; a.SeniorId = clinician.Role == "Senior" ? r.ClinicianId : null; a.StartTime = audit.NewStart; a.EndTime = audit.NewEnd; a.RoomId = r.RoomId; a.UpdatedBy = actor;
        if (a.TreatmentPlanSession != null) a.TreatmentPlanSession.SessionDate = a.StartTime;
        db.AppointmentScheduleChanges.Add(audit); await db.SaveChangesAsync(); await tx.CommitAsync(); return new(id, r.OperationKey, r.Action, ScheduleVersion(a));
    }
    public async Task<RecoveryReceipt> RecordRecoveryContactAsync(int id, RecoveryContactRequest r, string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(r.OperationKey) || r.OperationKey.Length > 100 || r.Reason?.Length > 500 || r.Outcome is not ("Declined" or "AwaitingResponse")) throw Rule("ContactOutcome", "Choose declined or awaiting patient response.");
        var fingerprint = Hash(new { id, r, actor }); await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable); await ClinicMutationLock.AcquireAsync(db);
        var replay = await RecoveryReplay(r.OperationKey, fingerprint, actor); if (replay != null) { await tx.CommitAsync(); return new(id, r.OperationKey, "Contact", r.ExpectedVersion); }
        var a = await RecoveryAppointment(id); await ValidateAffected(a, r.AbsenceId, r.ExpectedVersion); var audit = await RecoveryAudit(a, r.OperationKey, fingerprint, r.AbsenceId, actor, "Contact", r.Reason); audit.ContactOutcome = r.Outcome;
        db.AppointmentScheduleChanges.Add(audit); await db.SaveChangesAsync(); await tx.CommitAsync(); return new(id, r.OperationKey, "Contact", ScheduleVersion(a));
    }
    public Task<List<AppointmentScheduleChange>> RecoveryHistoryAsync(int id) => db.AppointmentScheduleChanges.AsNoTracking().Where(c => c.AppointmentId == id).OrderByDescending(c => c.ChangedAt).ThenByDescending(c => c.Id).ToListAsync();
    public async Task<bool> HasGenderConsentAsync(Appointment a, string clinician) => await db.AppointmentScheduleChanges.AnyAsync(c => c.AppointmentId == a.Id && c.ClinicianId == clinician && c.GenderException && c.ConsentMethod == "Phone" && c.ConsentRecordedAt != null);
    private Task<bool> HasRecoveryAssignmentAsync(Appointment a) => db.AppointmentScheduleChanges.AnyAsync(c => c.AppointmentId == a.Id && c.AbsenceId != null && c.ClinicianId == (a.DoctorId ?? a.SeniorId) && (c.Action == "Replace" || c.Action == "Reschedule"));
    private async Task ValidateRetainedRecoveryAsync(Appointment a, DateTime start, int room)
    {
        await RecoveryAppointment(a.Id); var clinician = a.DoctorId ?? a.SeniorId ?? ""; var identity = await RecoveryClinician(clinician, a.AppointmentType == "Assessment");
        var patient = await PatientAsync(a.PatientId); var gender = await db.Users.Where(u => u.Id == clinician).Select(u => u.Gender).SingleAsync();
        if (gender != patient.ApplicationUser.Gender && !await HasGenderConsentAsync(a, clinician)) throw Rule("PhoneConsentRequired", "This booking requires its saved patient phone consent.", 409);
        var conflicts = await RecoveryConflicts(a, clinician, identity.Role, start, room); if (conflicts.Count > 0) throw Rule("SlotUnavailable", string.Join(", ", conflicts), 409);
    }
    public async Task RequireNotAbsentAsync(Appointment a)
    {
        var clinician = a.DoctorId ?? a.SeniorId; var absences = await db.DoctorAbsences.AsNoTracking().Where(x => x.DoctorId == clinician && !x.Withdrawn).ToListAsync();
        if (absences.Any(x => ClinicianAbsenceRules.Overlaps(x, a.StartTime, a.EndTime))) throw Rule("ClinicianAbsent", "Needs attention: resolve this booking or correct the absence before starting treatment.", 409);
    }
}
