using System.Data;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Doctor;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

public class ClinicalRuleException(string code, string message, int status = 409, object? data = null)
    : BookingRuleException(code, message, status, data);

public class DoctorWorkspaceService(ApplicationDbContext db, BookingService bookings, TimeProvider? clock = null)
{
    private DateTime Now => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
    public static string State(Appointment a) => a.Status == AppointmentStatus.Cancelled ? "Cancelled" :
        a.Status == AppointmentStatus.NoShow ? "NoShow" : a.ActualCheckOut != null ? "Departed" : a.Status == AppointmentStatus.Completed ? "LegacyCompleted" :
        a.TreatmentEndedAt != null ? "TreatmentFinished" : a.TreatmentStartedAt != null ? "Treating" : a.ActualCheckIn != null ? "Arrived" : "Scheduled";
    private IQueryable<Appointment> Visits => db.Appointments.Include(a => a.Patient).ThenInclude(p => p.ApplicationUser)
        .Include(a => a.Room).Include(a => a.Doctor).Include(a => a.TreatmentPlanSession).ThenInclude(s => s!.TreatmentPlan);
    private static DoctorVisit Visit(Appointment a) => new(a.Id, a.PatientId, a.Patient.ApplicationUser.FullName,
        a.Patient.ApplicationUser.PhoneNumber, a.Patient.ApplicationUser.Gender.ToString(), a.TreatmentPlanSession?.TreatmentPlanId,
        a.TreatmentPlanSessionId, a.TreatmentPlanSession?.SessionNumber, a.TreatmentPlanSession?.TreatmentPlan.TreatmentType,
        a.DoctorId, a.Doctor?.FullName, a.RoomId, a.Room?.Name, a.StartTime, a.EndTime, a.ActualCheckIn, a.ActualCheckOut,
        a.TreatmentStartedAt, a.TreatmentEndedAt, State(a));
    public async Task<DoctorScheduleResponse> ScheduleAsync(DateOnly date, string actor, bool supervisor, string? doctorId = null)
    {
        if (!supervisor && doctorId != null && doctorId != actor) throw new ClinicalRuleException("Doctor.AccessDenied", "Only your assigned schedule is available.", 403);
        var noon = date.ToDateTime(new(12, 0));
        var range = ClinicCalendar.UtcDay(new DateTimeOffset(noon, ClinicCalendar.Zone.GetUtcOffset(noon)));
        var query = Visits.AsNoTracking().Where(a => a.AppointmentType == "Session");
        if (!supervisor || doctorId != null) query = query.Where(a => a.DoctorId == (supervisor ? doctorId : actor));
        var appointments = await query.Where(a => a.StartTime >= range.Start && a.StartTime < range.End).OrderBy(a => a.StartTime).ThenBy(a => a.Id).ToListAsync();
        var active = await query.Where(a => a.TreatmentStartedAt != null && a.TreatmentEndedAt == null).OrderBy(a => a.TreatmentStartedAt).ToListAsync();
        return new(date, appointments.Select(Visit).ToList(), active.Select(Visit).ToList());
    }
    public async Task<Appointment> AssignedAsync(int id, string actor, bool supervisor)
    {
        var a = await Visits.SingleOrDefaultAsync(a => a.Id == id) ?? throw new ClinicalRuleException("Doctor.NotFound", "The assigned treatment appointment was not found.", 404);
        if (!supervisor && a.DoctorId != actor) throw new ClinicalRuleException("Doctor.AccessDenied", "Only the assigned Doctor can access this session.", 403);
        if (a.AppointmentType != "Session" || a.TreatmentPlanSession == null) throw new ClinicalRuleException("Doctor.TreatmentRequired", "Select a saved treatment session.");
        return a;
    }
    private async Task<ClinicalNotes> NotesAsync(int id)
    {
        var note = await db.TreatmentSessionRecords.AsNoTracking().SingleOrDefaultAsync(n => n.AppointmentId == id);
        if (note == null) return new(0, await db.Appointments.Where(a => a.Id == id).Select(a => a.TreatmentPlanSession == null ? null : a.TreatmentPlanSession.Notes).SingleAsync() ?? "", "", "", "", null, null, null);
        return new(note.Version, note.ProgressNotes, note.ConditionChanges, note.ResponseToTreatment, note.TreatmentPerformed,
            await db.Users.Where(u => u.Id == note.AuthorId).Select(u => u.FullName).SingleOrDefaultAsync(), note.SavedAt, note.FinalizedAt);
    }
    public async Task<List<PreviousClinicalSession>> PreviousAsync(int planId, int? except = null)
    {
        var appointments = await db.Appointments.AsNoTracking().Include(a => a.TreatmentPlanSession).Where(a => a.TreatmentPlanSession != null && a.TreatmentPlanSession.TreatmentPlanId == planId && a.Id != except).ToListAsync();
        var ids = appointments.Select(a => a.Id).ToList();
        var records = await db.TreatmentSessionRecords.AsNoTracking().Where(n => ids.Contains(n.AppointmentId) && n.FinalizedAt != null).ToListAsync();
        var old = await db.SessionNotes.AsNoTracking().Where(n => ids.Contains(n.AppointmentId)).ToListAsync();
        var authorIds = records.Select(n => n.AuthorId).Concat(old.Select(n => n.DoctorId)).Distinct().ToList();
        var authors = await db.Users.Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName);
        var result = new List<PreviousClinicalSession>();
        foreach (var a in appointments)
        {
            var record = records.SingleOrDefault(n => n.AppointmentId == a.Id);
            if (record != null) result.Add(new(a.TreatmentPlanSession!.SessionNumber, a.StartTime, a.TreatmentStartedAt, a.TreatmentEndedAt,
                authors.GetValueOrDefault(record.AuthorId), record.FinalizedAt, record.ProgressNotes, record.ConditionChanges, record.ResponseToTreatment, record.TreatmentPerformed));
            foreach (var n in old.Where(n => n.AppointmentId == a.Id)) result.Add(new(a.TreatmentPlanSession!.SessionNumber, a.StartTime,
                a.TreatmentStartedAt, a.TreatmentEndedAt, authors.GetValueOrDefault(n.DoctorId), n.CreatedAt, n.Notes, null, null, null));
            if (record == null && !old.Any(n => n.AppointmentId == a.Id) && a.TreatmentPlanSession!.Status == SessionStatus.Completed)
                result.Add(new(a.TreatmentPlanSession.SessionNumber, a.StartTime, a.TreatmentStartedAt, a.TreatmentEndedAt, null, null, a.TreatmentPlanSession.Notes, null, null, null));
        }
        return result.OrderByDescending(n => n.RecordedAt ?? n.PlannedAt).ToList();
    }
    public async Task<DoctorPatientWorkspace> GetAsync(int id, string actor, bool supervisor)
    {
        var a = await AssignedAsync(id, actor, supervisor);
        var plan = await db.TreatmentPlans.AsNoTracking().Include(p => p.Assessment).ThenInclude(a => a.Senior).Include(p => p.Doctor).SingleAsync(p => p.Id == a.TreatmentPlanSession!.TreatmentPlanId);
        var booked = await db.Appointments.Where(x => x.TreatmentPlanSession != null && x.TreatmentPlanSession.TreatmentPlanId == plan.Id && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow).Select(x => x.TreatmentPlanSessionId).Distinct().CountAsync();
        var completed = await db.TreatmentPlanSessions.CountAsync(s => s.TreatmentPlanId == plan.Id && s.Status == SessionStatus.Completed);
        var review = await db.ReassessmentRequests.AnyAsync(r => r.TreatmentPlanId == plan.Id && r.ReviewRequired);
        var requests = await new ReassessmentService(db, bookings, this).ListAsync(actor, supervisor, false, plan.Id);
        var notes=await NotesAsync(id);
        return new(Visit(a), a.Patient.ApplicationUser.BirthDate,
            new(plan.Id, plan.Assessment.Diagnosis, plan.Assessment.ConditionType, plan.Assessment.Notes, plan.Assessment.CompletedAt,
                plan.Assessment.Senior.FullName, plan.TreatmentType, plan.Exercises, plan.NumberOfSessions, booked, completed,
                plan.FrequencyPerWeek, plan.Doctor.FullName, review), notes, await PreviousAsync(plan.Id, notes.Version == 0 && a.Status == AppointmentStatus.Completed ? null : id),
            await db.InsuranceDocuments.AsNoTracking().Where(d => d.PatientId == a.PatientId).Select(d => new ClinicalDocument(d.Id, d.FileName, d.UploadedAt)).ToListAsync(), requests);
    }
    public async Task<DoctorPatientWorkspace> StartAsync(int id, string actor, bool supervisor)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(db);
        var a = await AssignedAsync(id, actor, supervisor);
        if (a.TreatmentStartedAt != null) { if (a.TreatmentEndedAt != null) throw new ClinicalRuleException("Doctor.Finalized", "This treatment is already finished."); }
        else
        {
            if (a.Status != AppointmentStatus.InProgress || a.ActualCheckIn == null || a.ActualCheckOut != null)
                throw new ClinicalRuleException("Doctor.ArrivalRequired", "Ask Reception to record this patient's arrival before starting treatment.");
            var gate = await new CourseFinanceService(db).GateAsync(id);
            if (!gate.CanStart) throw new ClinicalRuleException(gate.PricingMode == "Review" ? "Booking.FinancialAgreementRequired" : "Booking.PaymentCoverageRequired", "Ask Reception to collect the exact patient shortfall before treatment.", 409, gate);
            await bookings.ValidateDoctorStartAsync(a, Now);
            a.TreatmentStartedAt = Now; a.TreatmentStartedBy = actor;
            a.TreatmentPlanSession!.Status = SessionStatus.InProgress;
            db.TreatmentSessionRecords.Add(new() { AppointmentId = id, AuthorId = actor, SavedAt = Now, Version = 1 });
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync(); return await GetAsync(id, actor, supervisor);
    }
    private static bool Same(TreatmentSessionRecord n, NotesMutation r) => n.ProgressNotes == r.ProgressNotes && n.ConditionChanges == r.ConditionChanges && n.ResponseToTreatment == r.ResponseToTreatment && n.TreatmentPerformed == r.TreatmentPerformed;
    private static void Validate(NotesMutation r, bool final)
    {
        if (new[] {r.ProgressNotes,r.ConditionChanges,r.ResponseToTreatment,r.TreatmentPerformed}.Any(t => t == null || t.Length > 10000) || final && string.IsNullOrWhiteSpace(r.ProgressNotes))
            throw new ClinicalRuleException("Doctor.NotesInvalid", "Enter final progress notes; each field must be at most 10,000 characters.", 400);
    }
    public async Task<DoctorPatientWorkspace> SaveAsync(int id, NotesMutation r, string actor, bool supervisor, bool final)
    {
        Validate(r, final);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(db);
        var a = await AssignedAsync(id, actor, supervisor);
        var n = await db.TreatmentSessionRecords.SingleOrDefaultAsync(n => n.AppointmentId == id);
        if (n == null || a.TreatmentStartedAt == null) throw new ClinicalRuleException("Doctor.NotStarted", "Start the session before saving treatment notes.");
        if (n.FinalizedAt != null)
        {
            if (!final || !Same(n, r)) throw new ClinicalRuleException("Doctor.Finalized", "The final clinical record is read-only. Your text was not applied.");
        }
        else
        {
            if (!final && n.Version == r.ExpectedVersion + 1 && Same(n, r)) { await transaction.CommitAsync(); return await GetAsync(id, actor, supervisor); }
            if (a.ActualCheckOut != null || a.Status != AppointmentStatus.InProgress) throw new ClinicalRuleException("Doctor.InvalidState", "This session is no longer active.");
            if (n.Version != r.ExpectedVersion && !(n.Version == r.ExpectedVersion + 1 && Same(n, r)))
                throw new ClinicalRuleException("Doctor.ConcurrentEdit", "Newer notes exist. Your text is retained; review the latest server record before editing again.", 409, new { currentVersion = n.Version });
            n.ProgressNotes = r.ProgressNotes; n.ConditionChanges = r.ConditionChanges;
            n.ResponseToTreatment = r.ResponseToTreatment; n.TreatmentPerformed = r.TreatmentPerformed;
            n.Version++; n.SavedAt = Now; n.AuthorId = actor;
            if (final)
            {
                n.FinalizedAt = Now; a.TreatmentEndedAt = Now; a.TreatmentEndedBy = actor;
                var session = a.TreatmentPlanSession!; session.Status = SessionStatus.Completed; session.Notes = n.ProgressNotes;
                var plan = await db.TreatmentPlans.SingleAsync(p => p.Id == session.TreatmentPlanId);
                plan.CompletedSessions = await db.TreatmentPlanSessions.CountAsync(s => s.TreatmentPlanId == plan.Id && s.Id != session.Id && s.Status == SessionStatus.Completed) + 1;
                foreach (var invoice in await db.Invoices.Where(i => i.AppointmentId == id).ToListAsync()) invoice.DeliveredAt = Now;
            }
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync(); return await GetAsync(id, actor, supervisor);
    }
}
