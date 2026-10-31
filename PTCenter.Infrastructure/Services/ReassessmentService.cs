using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Doctor;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

public class ReassessmentService(ApplicationDbContext db, BookingService bookings, DoctorWorkspaceService clinical)
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public async Task<List<ReassessmentView>> ListAsync(string actor, bool staff, bool senior = false, int? planId = null, int? requestId = null, bool includeHistory = false)
    {
        var query = db.ReassessmentRequests.AsNoTracking();
        if (requestId != null) query = query.Where(r => r.Id == requestId);
        if (planId != null) query = query.Where(r => r.TreatmentPlanId == planId);
        if (!staff)
        {
            if (senior) query = query.Where(r => r.ReviewingSeniorId == actor || r.State == "Requested" && db.Patients.Any(p => p.Id == r.PatientId && db.Users.Any(u => u.Id == actor && u.Gender == p.ApplicationUser.Gender)));
            else query = query.Where(r => db.TreatmentPlans.Any(p => p.Id == r.TreatmentPlanId && p.DoctorId == actor) || db.Appointments.Any(a => a.DoctorId == actor && a.TreatmentPlanSession != null && a.TreatmentPlanSession.TreatmentPlanId == r.TreatmentPlanId));
        }
        var records = await query.OrderByDescending(r => r.RequestedAt).ToListAsync();
        var result = new List<ReassessmentView>();
        foreach (var r in records)
        {
            var plan = await db.TreatmentPlans.AsNoTracking().Include(p => p.Assessment).Include(p => p.Doctor).SingleAsync(p => p.Id == r.TreatmentPlanId);
            result.Add(new(r.Id, r.PatientId, await db.Patients.Where(p => p.Id == r.PatientId).Select(p => p.ApplicationUser.FullName).SingleAsync(), r.TreatmentPlanId, r.RelatedAppointmentId,
                await db.Users.Where(u => u.Id == r.RequestedBy).Select(u => u.FullName).SingleAsync(), r.RequestedAt, r.ReasonCategory, r.Notes, r.Recommendation, r.State,
                r.ReviewStartedAt, r.ReviewingSeniorId,
                r.ReviewingSeniorId == null ? null : await db.Users.Where(u => u.Id == r.ReviewingSeniorId).Select(u => u.FullName).SingleOrDefaultAsync(),
                r.CompletedAssessmentId, r.RevisedPlanId, r.CancellationReason, r.Version, plan.Assessment.Diagnosis, plan.Exercises, includeHistory ? await clinical.PreviousAsync(plan.Id) : [], plan.NumberOfSessions, plan.CompletedSessions, plan.TreatmentType, plan.FrequencyPerWeek, plan.DoctorId, plan.Doctor.FullName,
                r.CompletedAssessmentId == null ? null : await db.Assessments.Where(a => a.Id == r.CompletedAssessmentId).Select(a => a.Notes).SingleOrDefaultAsync(), r.Decision, r.ResolvedAt,
                r.ResolvedBy == null ? null : await db.Users.Where(u => u.Id == r.ResolvedBy).Select(u => u.FullName).SingleOrDefaultAsync()));
        }
        return result;
    }
    private async Task<ReassessmentView> View(int id) => (await ListAsync("", true, requestId: id, includeHistory: true)).Single(r => r.Id == id);
    public async Task<ReassessmentView> RequestAsync(int appointmentId, RequestReassessment request, string actor, bool supervisor)
    {
        if (string.IsNullOrWhiteSpace(request.OperationKey) || request.OperationKey.Length > 100 ||
            request.ReasonCategory is not ("SymptomsChanged" or "ProgressConcern" or "PlanReview" or "Other") ||
            string.IsNullOrWhiteSpace(request.Notes) || request.Notes.Length > 4000 || request.Recommendation is not ("Continue" or "Pause"))
            throw new ClinicalRuleException("Reassessment.InvalidRequest", "Choose a reason, add explanatory notes and select a continue/pause recommendation.", 400);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(db);
        var a = await clinical.AssignedAsync(appointmentId, actor, supervisor);
        var previous = await db.ReassessmentRequests.SingleOrDefaultAsync(r => r.OperationKey == request.OperationKey);
        if (previous != null)
        {
            if (previous.RelatedAppointmentId != appointmentId || previous.RequestedBy != actor || previous.ReasonCategory != request.ReasonCategory || previous.Notes != request.Notes.Trim() || previous.Recommendation != request.Recommendation)
                throw new ClinicalRuleException("Reassessment.RetryConflict", "This key belongs to another reassessment request.");
            await transaction.CommitAsync(); return await View(previous.Id);
        }
        var existing = await db.ReassessmentRequests.SingleOrDefaultAsync(r => r.ActivePlanId == a.TreatmentPlanSession!.TreatmentPlanId);
        if (existing != null) throw new ClinicalRuleException("Reassessment.ExistingActive", "An active request already exists. Open the existing request.", 409, new { existingRequestId = existing.Id });
        var record = new ReassessmentRequest { PatientId = a.PatientId, TreatmentPlanId = a.TreatmentPlanSession!.TreatmentPlanId,
            ActivePlanId = a.TreatmentPlanSession.TreatmentPlanId, RelatedAppointmentId = appointmentId, RequestedBy = actor,
            RequestedAt = DateTime.UtcNow, ReasonCategory = request.ReasonCategory, Notes = request.Notes.Trim(),
            Recommendation = request.Recommendation, OperationKey = request.OperationKey, Version = 1 };
        db.ReassessmentRequests.Add(record); await db.SaveChangesAsync(); await transaction.CommitAsync(); return await View(record.Id);
    }
    public async Task<ReassessmentView> StartAsync(int id, StartReassessment request, string actor, bool supervisor)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(db);
        var r = await db.ReassessmentRequests.SingleOrDefaultAsync(r => r.Id == id) ?? throw new ClinicalRuleException("Reassessment.NotFound", "Request not found.", 404);
        if (r.State == "InReview" && r.ReviewingSeniorId == actor) { await transaction.CommitAsync(); return await View(id); }
        if (r.State != "Requested" || r.Version != request.ExpectedVersion) throw new ClinicalRuleException("Reassessment.ConcurrentEdit", "This request changed. Reload before starting the review.");
        if (!supervisor)
        {
            var gender = await db.Patients.Where(p => p.Id == r.PatientId).Select(p => p.ApplicationUser.Gender).SingleAsync();
            var seniorUser = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == actor);
            if (seniorUser == null || seniorUser.Gender != gender || seniorUser.LockoutEnd > DateTimeOffset.UtcNow)
                throw new ClinicalRuleException("Reassessment.SeniorEligibility", "An eligible Senior matching the patient's gender must review this request.", 403);
        }
        r.ReviewingSeniorId = actor; r.ReviewStartedAt = DateTime.UtcNow; r.State = "InReview"; r.Version++;
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return await View(id);
    }
    public async Task<ReassessmentView> CancelAsync(int id, CancelReassessment request, string actor, bool staff = false)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000) throw new ClinicalRuleException("Reassessment.ReasonRequired", "A cancellation reason is required.", 400);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable); await ClinicMutationLock.AcquireAsync(db);
        var r = await db.ReassessmentRequests.SingleOrDefaultAsync(r => r.Id == id) ?? throw new ClinicalRuleException("Reassessment.NotFound", "Request not found.", 404);
        if (!staff && r.RequestedBy != actor && r.ReviewingSeniorId != actor) throw new ClinicalRuleException("Reassessment.AccessDenied", "Only the requester, reviewing Senior or authorized reception staff can cancel this review.", 403);
        if (r.State == "Cancelled" && r.CancellationReason == request.Reason.Trim()) { await transaction.CommitAsync(); return await View(id); }
        if (r.State is "Completed" or "Cancelled" || r.Version != request.ExpectedVersion) throw new ClinicalRuleException("Reassessment.ConcurrentEdit", "This request changed. Reload before cancelling.");
        r.State = "Cancelled"; r.ActivePlanId = null; r.CancellationReason = request.Reason.Trim(); r.ResolvedAt = DateTime.UtcNow; r.ResolvedBy = actor; r.Version++;
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return await View(id);
    }
    public async Task<ReassessmentView> CompleteAsync(int id, CompleteReassessment request, string actor, bool supervisor)
    {
        if (string.IsNullOrWhiteSpace(request.ProgressNotes) || request.ProgressNotes.Length > 10000 || request.Decision is not ("Continue" or "Revise" or "Discharge"))
            throw new ClinicalRuleException("Reassessment.ClinicalDetails", "Enter clinical progress and an explicit decision.", 400);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable); await ClinicMutationLock.AcquireAsync(db);
        var r = await db.ReassessmentRequests.SingleOrDefaultAsync(r => r.Id == id) ?? throw new ClinicalRuleException("Reassessment.NotFound", "Request not found.", 404);
        if (!supervisor && r.ReviewingSeniorId != actor) throw new ClinicalRuleException("Reassessment.AccessDenied", "Only the assigned Senior can complete this reassessment.", 403);
        var hash = Hash(new { id, request, actor });
        if (r.State == "Completed")
        {
            if (r.CompletionHash != hash) throw new ClinicalRuleException("Reassessment.Finalized", "This reassessment is already finalized.");
            await transaction.CommitAsync(); return await View(id);
        }
        if (r.State != "InReview" || r.Version != request.ExpectedVersion)
            throw new ClinicalRuleException("Reassessment.ReviewRequired", "Start the clinical review and refresh its current state before completing it.");
        var original = await db.TreatmentPlans.Include(p => p.Assessment).Include(p => p.Doctor).SingleAsync(p => p.Id == r.TreatmentPlanId);
        var assessment = new Assessment { PatientId = r.PatientId, SeniorId = r.ReviewingSeniorId!, Diagnosis = original.Assessment.Diagnosis,
            ConditionType = original.Assessment.ConditionType, Priority = original.Assessment.Priority, Notes = request.ProgressNotes, CompletedAt = DateTime.UtcNow, AssessmentDate = DateTime.UtcNow };
        db.Assessments.Add(assessment);
        if (request.Decision == "Revise")
        {
            if (string.IsNullOrWhiteSpace(request.Diagnosis) || string.IsNullOrWhiteSpace(request.TreatmentType) || request.NumberOfSessions is < 1 or > 200 || request.FrequencyPerWeek is < 1 or > 7)
                throw new ClinicalRuleException("Reassessment.PlanDetails", "Enter the revised diagnosis, treatment, prescribed count and weekly frequency.", 400);
            await bookings.RequireEligibleDoctorAsync(r.PatientId, request.DoctorId);
            assessment.Diagnosis = request.Diagnosis.Trim(); assessment.ConditionType = request.TreatmentType;
            var revised = new TreatmentPlan { Assessment = assessment, DoctorId = request.DoctorId, NumberOfSessions = request.NumberOfSessions,
                FrequencyPerWeek = request.FrequencyPerWeek, TreatmentType = request.TreatmentType, Exercises = request.Exercises, Status = StatusPlan.Active };
            for (var number = 1; number <= revised.NumberOfSessions; number++) revised.Sessions.Add(new() { SessionNumber = number });
            db.TreatmentPlans.Add(revised); await db.SaveChangesAsync(); r.RevisedPlanId = revised.Id;
        }
        db.ReAssessments.Add(new() { TreatmentPlanId = original.Id, SeniorId = r.ReviewingSeniorId!, Progress = request.ProgressNotes, Recommendation = request.Decision });
        await db.SaveChangesAsync(); r.CompletedAssessmentId = assessment.Id;
        r.State = "Completed"; r.ActivePlanId = null; r.ResolvedAt = DateTime.UtcNow; r.ResolvedBy = actor; r.CompletionHash = hash;
        r.Decision = request.Decision; r.ReviewRequired = request.Decision != "Continue"; r.Version++;
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return await View(id);
    }
}
