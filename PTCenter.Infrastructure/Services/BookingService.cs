using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

public class BookingRuleException(string code, string message, int status = 400, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public object? Details { get; } = details;
}

/// <summary>Server-authoritative eligibility, availability and atomic booking/financial persistence.</summary>
public partial class BookingService(ApplicationDbContext db)
{
    private static readonly TimeZoneInfo ClinicTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static BookingRuleException Rule(string code, string message, int status = 400, object? details = null) => new("Booking." + code, message, status, details);
    private static bool Money(decimal value) => value >= 0 && value <= 999999999 && decimal.Round(value, 2) == value;
    private static bool Insured(Patient patient) => patient.PaymentType == "Insurance";

    private async Task<Patient> PatientAsync(int id) => await db.Patients.AsNoTracking()
        .Include(p => p.ApplicationUser).Include(p => p.InsuranceCompany).FirstOrDefaultAsync(p => p.Id == id)
        ?? throw Rule("PatientNotFound", "Patient was not found.", 404);

    public async Task<PTCenter.Application.Contracts.Common.PagedResponse<BookingPatientOption>> DirectoryAsync(int pageNumber = 1, int pageSize = 100)
    {
        if (pageNumber < 1 || pageSize is < 1 or > 100) throw Rule("DirectoryPage", "Invalid directory page.");
        var directory = BookablePatients().AsNoTracking();
        var total = await directory.CountAsync();
        var items = await directory.OrderBy(p => p.ApplicationUser.FullName).ThenBy(p=>p.Id).Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .Select(p => new BookingPatientOption(p.Id, p.ApplicationUser.FullName, p.ApplicationUser.PhoneNumber ?? "", p.ApplicationUser.Gender.ToString())).ToListAsync();
        return new() { Items = items, PageNumber = pageNumber, PageSize = pageSize, TotalCount = total, TotalPages = (int)Math.Ceiling(total / (double)pageSize) };
    }

    public async Task<BookingContext> ContextAsync(int patientId)
    {
        var patient = await PatientAsync(patientId);
        var assessments = await db.Assessments.AsNoTracking().Where(a => a.PatientId == patientId)
            .OrderByDescending(a => a.AssessmentDate).Select(a => new BookingAssessment(a.Id, a.AssessmentDate, a.CompletedAt, a.Diagnosis)).ToListAsync();
        var plans = await db.TreatmentPlans.AsNoTracking().Include(p => p.Doctor)
            .Where(p => p.Assessment.PatientId == patientId && p.Assessment.CompletedAt != null && p.Status == StatusPlan.Active).ToListAsync();
        var planIds = plans.Select(p => p.Id).ToList();
        var sessions = await db.TreatmentPlanSessions.AsNoTracking().Where(s => planIds.Contains(s.TreatmentPlanId)).ToListAsync();
        var sessionIds = sessions.Select(s => s.Id).ToList();
        var appointments = await db.Appointments.AsNoTracking().Where(a => a.TreatmentPlanSessionId != null && sessionIds.Contains(a.TreatmentPlanSessionId.Value)).ToListAsync();
        var funded = await db.Appointments.CountAsync(a => a.PatientId == patientId && a.AppointmentType == "Session" &&
            (a.InsuranceFunded || a.BookingBatchId == null && patient.PaymentType == "Insurance") &&
            (a.ActualCheckIn != null || a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow));
        var info = patient.InsuranceDetails;
        var result = new BookingContext(patientId, patient.ApplicationUser.FullName, patient.ApplicationUser.Gender.ToString(), patient.PaymentType,
            assessments.Any(a => a.CompletedAt != null), Insured(patient), info?.Status == InsuranceStatus.Approved && patient.InsuranceCompanyId != null,
            info?.ApprovedSessions is int approved ? Math.Max(0, approved - funded) : null, info?.CopayPercentage, patient.InsuranceCompany?.Name,
            assessments, plans.Select(p => {
                var ids = sessions.Where(s => s.TreatmentPlanId == p.Id).Select(s => s.Id).ToHashSet();
                var used = appointments.Where(a => ids.Contains(a.TreatmentPlanSessionId!.Value) &&
                    (a.ActualCheckIn != null || a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow)).ToList();
                return new BookingPlan(p.Id, p.DoctorId, p.Doctor.FullName, p.TreatmentType, p.NumberOfSessions,
                    used.Count(a => a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow),
                    sessions.Count(s => s.TreatmentPlanId == p.Id && s.Status == SessionStatus.Completed),
                    Math.Max(0, p.NumberOfSessions - used.Select(a => a.TreatmentPlanSessionId).Distinct().Count() -
                        sessions.Count(s => s.TreatmentPlanId == p.Id && s.Status == SessionStatus.Completed && !used.Any(a => a.TreatmentPlanSessionId == s.Id))))
                { FrequencyPerWeek = p.FrequencyPerWeek is >=1 and <=7 ? p.FrequencyPerWeek : null,
                  BookedStartsAt = used.Where(a=>a.Status!=AppointmentStatus.Cancelled && a.Status!=AppointmentStatus.NoShow).Select(a=>a.StartTime).ToList() };
            }).ToList()) { ApprovedSessions = info?.ApprovedSessions };
        var finance = new CourseFinanceService(db);
        var reviews = await db.ReassessmentRequests.AsNoTracking().Where(r => r.PatientId == patientId && r.ReviewRequired).Select(r => new {r.TreatmentPlanId,r.RevisedPlanId}).ToListAsync();
        for(var index=0;index<result.Plans.Count;index++) {
            var plan=result.Plans[index];plan.Financial = await finance.SummaryAsync(plan.Id); plan.StaffReviewRequired = reviews.Any(r => r.TreatmentPlanId == plan.Id || r.RevisedPlanId == plan.Id);
            if(plan.Financial!=null)result.Plans[index]=plan with{RemainingSessions=Math.Min(plan.RemainingSessions,(await finance.AvailableNumbersAsync(plan.Id,plan.Financial.CoveredSessions)).Count)};
        }
        return result;
    }

    private Task<List<BookingClinician>> CliniciansForAsync(Patient patient, string role, string? preferred = null) => ReadCliniciansAsync(role, patient.ApplicationUser.Gender, preferred);
    public Task<List<BookingClinician>> WorkloadAsync() => ReadCliniciansAsync("Doctor", null);
    private async Task<List<BookingClinician>> ReadCliniciansAsync(string role, Gender? gender, string? preferred = null)
    {
        var users = await (from user in db.Users.AsNoTracking()
                      join membership in db.UserRoles on user.Id equals membership.UserId
                      join userRole in db.Roles on membership.RoleId equals userRole.Id
                      where userRole.Name == role && (gender == null || user.Gender == gender)
                      select new { user.Id, user.FullName, user.Gender, user.LockoutEnd }).ToListAsync();
        var active = await db.Appointments.AsNoTracking().Where(a => a.Status == AppointmentStatus.InProgress && (a.DoctorId != null && a.TreatmentStartedAt != null && a.TreatmentEndedAt == null || a.SeniorId != null && a.ActualCheckIn != null && a.ActualCheckOut == null))
            .Select(a => new { a.DoctorId, a.SeniorId }).ToListAsync();
        return users.Where(u => u.LockoutEnd == null || u.LockoutEnd <= DateTimeOffset.UtcNow)
            .Select(u => new BookingClinician(u.Id, u.FullName, u.Gender.ToString(), role, u.Id == preferred, active.Count(a => a.DoctorId == u.Id || a.SeniorId == u.Id)))
            .DistinctBy(u => u.Id).OrderByDescending(u => u.Preferred).ThenBy(u => u.Name).ToList();
    }

    public async Task<List<BookingClinician>> CliniciansAsync(int patientId, int? planId, bool treatment = false)
    {
        var patient = await PatientAsync(patientId);
        var context = await ContextAsync(patientId);
        var plan = context.Plans.FirstOrDefault(p => p.Id == planId);
        if (planId != null && plan == null) throw Rule("PlanRequired", "Select an active saved treatment plan for this patient.");
        return await CliniciansForAsync(patient, context.HasCompletedAssessment || treatment ? "Doctor" : "Senior", plan?.DoctorId);
    }

    public Task<BookingPreview> PreviewAsync(BookingRequest request) => PreviewCoreAsync(request);

    private async Task<BookingPreview> PreviewCoreAsync(BookingRequest request, int? excludeAppointment = null, bool checkFinancial = true, bool? fundingOverride = null)
    {
        var context = await ContextAsync(request.PatientId);
        var patient = await PatientAsync(request.PatientId);
        var errors = new List<string>();
        if (patient.PaymentType is not ("SelfPay" or "Cash" or "Visa" or "Insurance" or "Card" or "Online" or "InstaPay")) errors.Add("FundingTypeRequired");
        if (request.StartsAt.Count is < 1 or > 200) throw Rule("SessionCount", "Select between 1 and 200 appointments.");
        var treatment = context.HasCompletedAssessment;
        if (!treatment && (request.TreatmentPlanId != null || request.StartsAt.Count != 1)) errors.Add("AssessmentOnly");
        if (!treatment && await db.Appointments.AnyAsync(a => a.PatientId == request.PatientId && a.Id != excludeAppointment && a.AppointmentType == "Assessment" &&
            (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.InProgress))) errors.Add("AssessmentAlreadyBooked");
        var plan = context.Plans.FirstOrDefault(p => p.Id == request.TreatmentPlanId);
        if (treatment && plan == null) errors.Add("PlanRequired");
        if (checkFinancial && treatment && plan != null && request.StartsAt.Count > plan.RemainingSessions) errors.Add("PlanLimit");
        if (treatment && (fundingOverride ?? context.IsInsurance))
        {
            if (!context.InsuranceApproved) errors.Add("InsuranceApprovalRequired");
            var excluded = excludeAppointment.HasValue && await db.Appointments.AnyAsync(a => a.Id == excludeAppointment && a.InsuranceFunded && a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow) ? 1 : 0;
            if (context.RemainingApprovedSessions == null || request.StartsAt.Count > context.RemainingApprovedSessions + excluded) errors.Add("InsuranceLimit");
            if (context.CopayPercentage is not decimal copay || copay < 0 || copay > 100) errors.Add("CopayRequired");
        }
        var clinicians = await CliniciansForAsync(patient, treatment ? "Doctor" : "Senior");
        if (!clinicians.Any(c => c.Id == request.ClinicianId)) errors.Add("ClinicianEligibility");
        var roomCapacity = await db.Rooms.Where(r => r.Id == request.RoomId && r.IsActive).Select(r => r.Capacity).SingleOrDefaultAsync();
        if (roomCapacity <= 0) errors.Add("RoomRequired");
        var schedules = await db.DoctorSchedules.AsNoTracking().Where(s => s.DoctorId == request.ClinicianId).ToListAsync();
        var absences = await db.DoctorAbsences.AsNoTracking().Where(a => a.DoctorId == request.ClinicianId).ToListAsync();
        var slots = new List<BookingSlot>();
        var weekly = plan == null ? new Dictionary<DateOnly,int>() : await WeeklyCountsAsync(plan.Id,excludeAppointment);
        if(treatment && plan?.FrequencyPerWeek == null) errors.Add("FrequencyRequired");
        foreach (var start in request.StartsAt)
        {
            var end = start.AddMinutes(60);
            var local = TimeZoneInfo.ConvertTime(start, ClinicTimeZone);
            var localEnd = TimeZoneInfo.ConvertTime(end, ClinicTimeZone);
            var conflicts = new List<string>();
            if(plan?.FrequencyPerWeek is int frequency && weekly.GetValueOrDefault(ClinicCalendar.Week(start)) + request.StartsAt.Count(s=>ClinicCalendar.Week(s)==ClinicCalendar.Week(start)) > frequency)
                conflicts.Add("WeeklyFrequencyLimit");
            conflicts.AddRange(WorkingTimeConflicts(start, schedules, absences));
            if (request.StartsAt.Count(s => s < end && s.AddMinutes(60) > start) > 1) conflicts.Add("DuplicateSlot");
            var utc = start.UtcDateTime; var utcEnd = end.UtcDateTime;
            var overlaps = await db.Appointments.AsNoTracking().Where(a => a.Id != excludeAppointment &&
                a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow && a.Status != AppointmentStatus.Completed &&
                (a.StartTime < utcEnd && a.EndTime > utc || a.Status == AppointmentStatus.InProgress && a.ActualCheckOut == null) &&
                (a.PatientId == request.PatientId || a.RoomId == request.RoomId || a.DoctorId == request.ClinicianId || a.SeniorId == request.ClinicianId)).ToListAsync();
            conflicts.AddRange(IntervalConflicts(start, request.PatientId, request.ClinicianId, request.RoomId, overlaps, treatment, roomCapacity));
            slots.Add(new(start.ToUniversalTime(), end.ToUniversalTime(), conflicts));
        }
        if (treatment && plan != null && checkFinancial)
        {
            var quote = await new CourseFinanceService(db).QuoteAsync(request, context, patient);
            errors.AddRange(quote.Errors);
            var financial = quote.Financial;
            return new(slots, errors.Distinct().ToList(), financial.TotalAmount, financial.PatientAmount, financial.InsuranceAmount,
                request.Deposit, Math.Max(0, financial.RemainingAmount - request.Deposit), null) { Financial = financial, DiscountAmount = financial.DiscountAmount };
        }
        var appliedDiscount = request.Discount;
        if (request.DiscountType != "None" || request.DiscountValue != 0)
        {
            if (request.Discount != 0) errors.Add("DiscountExclusive");
            if (request.DiscountType is not ("Fixed" or "Percent") || !Money(request.DiscountValue) || request.DiscountType == "Percent" && request.DiscountValue > 100) errors.Add("DiscountInvalid");
            appliedDiscount = request.DiscountType == "Percent" ? decimal.Round((request.UnitPrice ?? 0) * request.DiscountValue / 100, 2, MidpointRounding.AwayFromZero) : request.DiscountValue;
        }
        decimal gross = 0; string? waiver = null;
        if (checkFinancial)
        {
            if (!treatment && context.IsInsurance) waiver = "InsuranceAssessmentTemporaryPolicy";
            else if (!treatment && request.AssessmentCharge == "Free") waiver = "FreeAssessment";
            else
            {
                if (!treatment && request.AssessmentCharge != "Paid") errors.Add("AssessmentChargeRequired");
                if (request.PricingMode == "PerSession" && request.UnitPrice is decimal unit && Money(unit) && unit > 0) gross = unit * slots.Count;
                else if (request.PricingMode == "Package" && treatment && request.PackagePrice is decimal package && Money(package) && package > 0) gross = package;
                else errors.Add("PriceRequired");
            }
            if (!Money(gross) || !Money(appliedDiscount) || appliedDiscount > gross || !Money(request.Deposit)) errors.Add("FinancialInvalid");
            if (waiver != null && (request.Deposit != 0 || appliedDiscount != 0 || request.UnitPrice.GetValueOrDefault() != 0 || request.PackagePrice.GetValueOrDefault() != 0 || request.Installments.Count != 0)) errors.Add("WaivedAssessmentHasNoLiability");
        }
        var total = Math.Max(0, gross - appliedDiscount);
        var patientAmount = treatment && context.IsInsurance && context.CopayPercentage is decimal percent && percent >= 0 && percent <= 100
            ? decimal.Round(total * percent / 100, 2, MidpointRounding.AwayFromZero) : total;
        var insuranceAmount = total - patientAmount;
        var remaining = patientAmount - request.Deposit;
        if (checkFinancial)
        {
            if (remaining < 0 || request.Deposit > 0 && (request.PaymentMethod == null || !Enum.IsDefined(request.PaymentMethod.Value) || request.PaymentMethod == PaymentMethod.Insurance)) errors.Add("DepositInvalid");
            if (request.Installments.Any(i => !Money(i.Amount) || i.Amount <= 0 || i.DueDate < DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ClinicTimeZone).DateTime)) ||
                request.Installments.Count > 100 || request.Installments.Select(i => i.DueDate).Distinct().Count() != request.Installments.Count ||
                request.Installments.Count > 0 && request.Installments.Sum(i => i.Amount) != remaining) errors.Add("InstallmentsInvalid");
        }
        return new(slots, errors.Distinct().ToList(), total, patientAmount, insuranceAmount, request.Deposit, Math.Max(0, remaining), waiver) { DiscountAmount = appliedDiscount };
    }

    public async Task<BookingReceipt> CreateAsync(BookingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100) throw Rule("RetryKeyRequired", "A stable booking retry key is required.");
        var fingerprint = Hash(request);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(db);
        var existing = await db.BookingBatches.SingleOrDefaultAsync(b => b.IdempotencyKey == request.IdempotencyKey);
        if (existing != null)
        {
            if (existing.RequestHash != fingerprint) throw Rule("RetryKeyConflict", "This retry key belongs to different booking details. Reconcile the original booking first.", 409);
            return await ReceiptAsync(existing.IdempotencyKey);
        }
        var preview = await PreviewCoreAsync(request);
        if (!preview.CanSave) throw Rule("Validation", string.Join(", ", preview.Errors.Concat(preview.Appointments.SelectMany(a => a.Conflicts))), 409, new { errors = preview.Errors, appointments = preview.Appointments });
        var context = await ContextAsync(request.PatientId);
        if (context.HasCompletedAssessment)
        {
            await CreateCourseBookingAsync(request, context);
            await transaction.CommitAsync();
            return await ReceiptAsync(request.IdempotencyKey);
        }
        var batch = new BookingBatch { IdempotencyKey = request.IdempotencyKey, RequestHash = fingerprint, PatientId = request.PatientId,
            PricingMode = request.PricingMode, GrossAmount = preview.TotalAmount + preview.DiscountAmount, Discount = preview.DiscountAmount,
            TotalAmount = preview.TotalAmount, PatientAmount = preview.PatientAmount, InsuranceAmount = preview.InsuranceAmount,
            WaiverReason = preview.WaiverReason, InstallmentsJson = JsonSerializer.Serialize(request.Installments) };
        db.BookingBatches.Add(batch);
        await db.SaveChangesAsync();
        var planSessions = new List<TreatmentPlanSession>();
        if (context.HasCompletedAssessment)
        {
            var plan = await db.TreatmentPlans.Include(p => p.Sessions).SingleAsync(p => p.Id == request.TreatmentPlanId);
            for (var number = 1; number <= plan.NumberOfSessions; number++)
                if (!plan.Sessions.Any(s => s.SessionNumber == number)) plan.Sessions.Add(new TreatmentPlanSession { SessionNumber = number, Status = SessionStatus.Pending, PaymentStatus = PaymentStatus.Pending });
            await db.SaveChangesAsync();
            var reserved = await db.Appointments.Where(a => a.TreatmentPlanSessionId != null).Select(a => a.TreatmentPlanSessionId!.Value).ToListAsync();
            planSessions = plan.Sessions.Where(s => s.SessionNumber > 0 && s.SessionNumber <= plan.NumberOfSessions && s.Status != SessionStatus.Completed && !reserved.Contains(s.Id)).OrderBy(s => s.SessionNumber).Take(request.StartsAt.Count).ToList();
            if (planSessions.Count != request.StartsAt.Count) throw Rule("PlanLimit", "The saved plan has insufficient unbooked sessions.", 409);
        }
        decimal remainingTotal = preview.TotalAmount, remainingPatient = preview.PatientAmount, deposit = request.Deposit;
        for (var i = 0; i < preview.Appointments.Count; i++)
        {
            var slot = preview.Appointments[i];
            var appointment = new Appointment { PatientId = request.PatientId, DoctorId = context.HasCompletedAssessment ? request.ClinicianId : null,
                SeniorId = context.HasCompletedAssessment ? null : request.ClinicianId, RoomId = request.RoomId, AppointmentType = context.HasCompletedAssessment ? "Session" : "Assessment",
                StartTime = slot.StartTime.UtcDateTime, EndTime = slot.EndTime.UtcDateTime, Status = AppointmentStatus.Pending,
                BookingBatchId = batch.Id, BookingSequence = i, InsuranceFunded = context.HasCompletedAssessment && context.IsInsurance };
            if (context.HasCompletedAssessment)
            {
                var session = planSessions[i]; session.SessionDate = appointment.StartTime; session.Status = SessionStatus.Pending;
                appointment.TreatmentPlanSessionId = session.Id;
            }
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
            var last = i == preview.Appointments.Count - 1;
            var total = last ? remainingTotal : decimal.Floor(preview.TotalAmount / preview.Appointments.Count * 100) / 100;
            var patientAmount = last ? remainingPatient : Math.Min(total, decimal.Floor(preview.PatientAmount / preview.Appointments.Count * 100) / 100);
            remainingTotal -= total; remainingPatient -= patientAmount;
            var paid = Math.Min(deposit, patientAmount); deposit -= paid;
            var invoice = new Invoice { AppointmentId = appointment.Id, PatientId = request.PatientId,
                InvoiceNumber = $"B-{batch.Id}-{i + 1}", Type = context.HasCompletedAssessment ? InvoiceType.Session : InvoiceType.Assessment,
                TotalAmount = total, PatientAmount = patientAmount, InsuranceAmount = total - patientAmount, PaidAmount = paid,
                Status = preview.WaiverReason != null || total == 0 ? InvoiceStatus.Waived : paid == total ? InvoiceStatus.Paid : InvoiceStatus.Pending };
            if (paid > 0) invoice.Payments.Add(new Payment { Amount = paid, PaymentMethod = request.PaymentMethod!.Value, Status = PaymentStatus.Paid, IdempotencyKey = $"{request.IdempotencyKey}:{i}" });
            db.Invoices.Add(invoice);
            if (context.HasCompletedAssessment) planSessions[i].PaymentStatus = total > 0 && paid == total ? PaymentStatus.Paid : PaymentStatus.Pending;
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return await ReceiptAsync(request.IdempotencyKey);
    }

    public async Task<BookingReceipt> ReceiptAsync(string key)
    {
        var batch = await db.BookingBatches.AsNoTracking().SingleOrDefaultAsync(b => b.IdempotencyKey == key)
            ?? throw Rule("BookingNotFound", "No committed booking was found for this retry key.", 404);
        var appointments = await db.Appointments.AsNoTracking().Where(a => a.BookingBatchId == batch.Id).OrderBy(a => a.BookingSequence).ToListAsync();
        if (batch.CourseAgreementId != null) return await CourseReceiptAsync(batch, appointments);
        var ids = appointments.Select(a => a.Id).ToList();
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.Payments).Where(i => i.AppointmentId != null && ids.Contains(i.AppointmentId.Value)).ToListAsync();
        var paid = invoices.Sum(i => i.Payments.Where(p => CourseFinanceService.PatientReceipt(p)).Sum(p => p.Amount));
        return new(key, batch.PatientId, "Committed", await CompleteRowsAsync(appointments),
            invoices.Select(i => i.Id).ToList(), batch.TotalAmount, batch.PatientAmount, batch.InsuranceAmount, paid, Math.Max(0, batch.PatientAmount - paid),
            batch.WaiverReason, JsonSerializer.Deserialize<List<InstallmentRequest>>(batch.InstallmentsJson) ?? []);
    }

    public async Task RescheduleAsync(int id, RescheduleBookingRequest request)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(db);
        var appointment = await db.Appointments.Include(a => a.TreatmentPlanSession).SingleOrDefaultAsync(a => a.Id == id)
            ?? throw Rule("AppointmentNotFound", "Appointment was not found.", 404);
        RequireUpcoming(appointment);
        var originallyInsured = appointment.InsuranceFunded || await db.Invoices.AnyAsync(i => i.AppointmentId == id && i.InsuranceAmount > 0);
        var preview = await PreviewCoreAsync(new BookingRequest { PatientId = appointment.PatientId, ClinicianId = request.ClinicianId,
            RoomId = request.RoomId, StartsAt = [request.StartTime], TreatmentPlanId = appointment.TreatmentPlanSession?.TreatmentPlanId }, id, false, originallyInsured);
        if (!preview.CanSave) throw Rule("Validation", string.Join(", ", preview.Errors.Concat(preview.Appointments.SelectMany(s => s.Conflicts))), 409);
        var context = await ContextAsync(appointment.PatientId);
        if (context.HasCompletedAssessment != (appointment.AppointmentType == "Session")) throw Rule("AssessmentOnly", "This appointment no longer matches the patient's clinical stage.");
        appointment.StartTime = request.StartTime.UtcDateTime; appointment.EndTime = appointment.StartTime.AddMinutes(60);
        appointment.DoctorId = appointment.AppointmentType == "Session" ? request.ClinicianId : null;
        appointment.SeniorId = appointment.AppointmentType == "Assessment" ? request.ClinicianId : null;
        appointment.RoomId = request.RoomId;
        if (appointment.TreatmentPlanSession != null) appointment.TreatmentPlanSession.SessionDate = appointment.StartTime;
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    public async Task<List<BookingAccount>> AccountsAsync()
    {
        var batches = await db.BookingBatches.AsNoTracking().Where(b => b.CourseAgreementId == null).OrderByDescending(b => b.Id).ToListAsync();
        var result = new List<BookingAccount>();
        foreach (var course in await db.TreatmentCourseAgreements.AsNoTracking().OrderByDescending(a => a.Id).ToListAsync())
        {
            var summary = (await new CourseFinanceService(db).SummaryAsync(course.TreatmentPlanId))!;
            var name = await db.Patients.Where(p => p.Id == course.PatientId).Select(p => p.ApplicationUser.FullName).SingleAsync();
            result.Add(new($"course:{course.Id}", course.PatientId, name, course.PricingMode, summary.TotalAmount, summary.DiscountAmount,
                summary.PatientAmount, summary.InsuranceAmount, summary.PaidAmount, summary.RemainingAmount,
                await db.Invoices.Where(i => i.CourseAgreementId == course.Id).Select(i => i.Id).ToListAsync(), JsonSerializer.Deserialize<List<InstallmentRequest>>(course.InstallmentsJson) ?? []));
        }
        foreach (var batch in batches)
        {
            var receipt = await ReceiptAsync(batch.IdempotencyKey);
            var name = await db.Patients.Where(p => p.Id == batch.PatientId).Select(p => p.ApplicationUser.FullName).SingleAsync();
            result.Add(new(batch.IdempotencyKey, batch.PatientId, name, batch.PricingMode, batch.TotalAmount, batch.Discount,
                receipt.PatientAmount, receipt.InsuranceAmount, receipt.PaidAmount, receipt.RemainingAmount, receipt.InvoiceIds, receipt.Installments));
        }
        return result;
    }

    public async Task<BookingReceipt> ReconcileCollectionAsync(string accountKey, string operationKey)
    {
        var courseId = await CourseIdAsync(accountKey);
        if (courseId != null)
        {
            if (!await db.Payments.AnyAsync(p => p.IdempotencyKey != null && p.IdempotencyKey.StartsWith($"course:{operationKey}:") && p.Invoice.CourseAgreementId == courseId))
                throw Rule("CollectionNotFound", "No committed course collection was found.", 404);
            return await AnyCourseReceiptAsync(courseId.Value);
        }
        var prefix = $"package:{operationKey}:";
        if (!await db.Payments.AnyAsync(p => p.IdempotencyKey != null && p.IdempotencyKey.StartsWith(prefix) &&
            p.Invoice.Appointment != null && p.Invoice.Appointment.BookingBatchId != null && db.BookingBatches.Any(b => b.Id == p.Invoice.Appointment.BookingBatchId && b.IdempotencyKey == accountKey)))
            throw Rule("CollectionNotFound", "No committed payment was found for this collection key.", 404);
        return await ReceiptAsync(accountKey);
    }

    public async Task<BookingReceipt> CollectAsync(string accountKey, CollectBookingPaymentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 90 || !Money(request.Amount) || request.Amount <= 0 || request.Method == null || !Enum.IsDefined(request.Method.Value) || request.Method == PaymentMethod.Insurance)
            throw Rule("InvalidPayment", "A stable collection key, actual patient payment method and positive amount are required.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(db);
        var courseId = await CourseIdAsync(accountKey);
        if (courseId != null)
        {
            await new CourseFinanceService(db).CollectAsync(courseId.Value, request.IdempotencyKey, request.Amount, request.Method.Value);
            await transaction.CommitAsync(); return await AnyCourseReceiptAsync(courseId.Value);
        }
        var batch = await db.BookingBatches.SingleOrDefaultAsync(b => b.IdempotencyKey == accountKey) ?? throw Rule("BookingNotFound", "Booking account was not found.", 404);
        var prefix = $"package:{request.IdempotencyKey}:";
        var previous = await db.Payments.Include(p => p.Invoice).ThenInclude(i => i.Appointment).Where(p => p.IdempotencyKey != null && p.IdempotencyKey.StartsWith(prefix)).ToListAsync();
        if (previous.Count > 0)
        {
            if (previous.Any(p => p.PaymentMethod != request.Method || p.Invoice.Appointment?.BookingBatchId != batch.Id) || previous.Sum(p => p.Amount) != request.Amount)
                throw Rule("RetryKeyConflict", "This collection key belongs to different financial details.", 409);
            return await ReceiptAsync(accountKey);
        }
        var invoices = await db.Invoices.Include(i => i.Payments).Include(i => i.Appointment).ThenInclude(a => a!.TreatmentPlanSession)
            .Where(i => i.Appointment != null && i.Appointment.BookingBatchId == batch.Id && i.Status != InvoiceStatus.Waived && i.Status != InvoiceStatus.Cancelled).OrderBy(i => i.Id).ToListAsync();
        decimal Remaining(Invoice i) => i.PatientAmount - i.Payments.Where(p => CourseFinanceService.PatientReceipt(p)).Sum(p => p.Amount);
        if (request.Amount > invoices.Sum(Remaining)) throw Rule("PaymentExceedsBalance", "The collection exceeds the remaining patient balance.");
        var remaining = request.Amount;
        foreach (var invoice in invoices)
        {
            var amount = Math.Min(remaining, Remaining(invoice)); if (amount <= 0) continue;
            invoice.Payments.Add(new Payment { Amount = amount, PaymentMethod = request.Method.Value, Status = PaymentStatus.Paid, IdempotencyKey = prefix + invoice.Id });
            invoice.PaidAmount = invoice.Payments.Where(p => CourseFinanceService.PatientReceipt(p)).Sum(p => p.Amount);
            var covered = invoice.Payments.Where(p => p.Status == PaymentStatus.Paid).Sum(p => p.Amount) >= invoice.TotalAmount;
            invoice.Status = covered ? InvoiceStatus.Paid : InvoiceStatus.Pending;
            if (invoice.Appointment?.TreatmentPlanSession is { } session) session.PaymentStatus = covered ? PaymentStatus.Paid : PaymentStatus.Pending;
            remaining -= amount;
        }
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return await ReceiptAsync(accountKey);
    }

    public async Task TransitionAsync(int id, AppointmentStatus status)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(db);
        var appointment = await db.Appointments.Include(a => a.TreatmentPlanSession).SingleOrDefaultAsync(a => a.Id == id)
            ?? throw Rule("AppointmentNotFound", "Appointment was not found.", 404);
        if (!Enum.IsDefined(status)) throw Rule("InvalidStatus", "Invalid appointment status.");
        if (appointment.Status == status) return; // A replay cannot stamp new actual times or complete a session twice.
        if (status == AppointmentStatus.InProgress && appointment.Status == AppointmentStatus.Pending)
        {
            if (appointment.TreatmentStartedAt != null || appointment.TreatmentEndedAt != null || appointment.ActualCheckOut != null ||
                appointment.TreatmentPlanSession is { Status: not SessionStatus.Pending })
                throw Rule("ClinicalStateReviewRequired", "This session has existing clinical activity. Review its history instead of reopening it through arrival.", 409);
            var gate = await new CourseFinanceService(db).GateAsync(id);
            if (!gate.CanStart) throw Rule(gate.PricingMode == "Review" ? "FinancialAgreementRequired" : "PaymentCoverageRequired",
                gate.PricingMode == "Review" ? "Reconcile this session's financial agreement before starting." : $"Additional patient payment required: EGP {gate.AdditionalPaymentRequired:F2}.", 409);
            var clinicianId = appointment.DoctorId ?? appointment.SeniorId;
            if (appointment.InsuranceFunded && !(await ContextAsync(appointment.PatientId)).InsuranceApproved)
                throw Rule("InsuranceApprovalRequired", "Insurance-funded treatment requires current verified approval.", 409);
            if (await db.Appointments.AnyAsync(a => a.Id != id && a.ActualCheckIn != null && a.ActualCheckOut == null &&
                (a.PatientId == appointment.PatientId ||
                appointment.SeniorId != null && (a.DoctorId == appointment.SeniorId || a.SeniorId == appointment.SeniorId))))
                throw Rule("ActualOccupancy", "The patient or room is still occupied. Record departure before reusing the room.", 409);
            var roomCapacity = await db.Rooms.Where(r=>r.Id==appointment.RoomId && r.IsActive).Select(r=>r.Capacity).SingleOrDefaultAsync();
            var roomLoad = await db.Appointments.CountAsync(a=>a.Id!=id && a.RoomId==appointment.RoomId && a.ActualCheckIn!=null && a.ActualCheckOut==null);
            if(roomCapacity<=roomLoad) throw Rule("ActualOccupancy","The room has no remaining places. Record departure or choose another room before arrival.",409);
            appointment.ActualCheckIn = DateTime.UtcNow;
            if (appointment.TreatmentPlanSession != null) { appointment.TreatmentPlanSession.CheckIn = appointment.ActualCheckIn; appointment.TreatmentPlanSession.Status = SessionStatus.Pending; }
        }
        else if (status == AppointmentStatus.Completed && appointment.Status == AppointmentStatus.InProgress && appointment.ActualCheckIn != null)
        {
            if (appointment.AppointmentType == "Session" && appointment.TreatmentEndedAt == null)
                throw Rule("TreatmentNotFinished", "The assigned Doctor must explicitly end treatment before patient departure.", 409);
            appointment.ActualCheckOut = DateTime.UtcNow;
            if (appointment.TreatmentPlanSession is { } session) session.CheckOut = appointment.ActualCheckOut;
            if (appointment.AppointmentType != "Session")
                foreach (var invoice in await db.Invoices.Where(i => i.AppointmentId == id).ToListAsync()) invoice.DeliveredAt = appointment.ActualCheckOut;
        }
        else if ((status == AppointmentStatus.Cancelled || status == AppointmentStatus.NoShow) && appointment.Status == AppointmentStatus.Pending) { }
        else throw Rule("InvalidTransition", "This status transition is not allowed. Check-in and check-out are separate staff actions.", 409);
        appointment.Status = status;
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    public async Task<BookingPlan> CompleteAssessmentAsync(CompleteAssessmentRequest request, string actor, bool supervisor)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(db);
        var appointment = await db.Appointments.SingleOrDefaultAsync(a => a.Id == request.AppointmentId)
            ?? throw Rule("AppointmentNotFound", "Appointment was not found.", 404);
        if (appointment.AppointmentType != "Assessment" || appointment.ActualCheckIn == null || appointment.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow)
            throw Rule("AssessmentNotStarted", "An assessment must be checked in before its clinical record is completed.");
        if (!supervisor && appointment.SeniorId != actor) throw Rule("AssessmentOwner", "Only the assigned Senior can complete this assessment.", 403);
        if (string.IsNullOrWhiteSpace(request.Diagnosis) || string.IsNullOrWhiteSpace(request.TreatmentType) || request.NumberOfSessions is < 1 or > 200 || request.FrequencyPerWeek is < 1 or > 7)
            throw Rule("ClinicalDetailsRequired", "Diagnosis, treatment type, session count and weekly frequency are required.");
        var patient = await PatientAsync(appointment.PatientId);
        if (!(await CliniciansForAsync(patient, "Doctor")).Any(c => c.Id == request.DoctorId)) throw Rule("ClinicianEligibility", "Select an eligible Doctor matching the patient's gender.");
        var existing = await db.Assessments.Include(a => a.TreatmentPlan).SingleOrDefaultAsync(a => a.AppointmentId == appointment.Id);
        if (existing != null)
        {
            var saved = existing.TreatmentPlan;
            if (saved == null || existing.Diagnosis != request.Diagnosis.Trim() || saved.DoctorId != request.DoctorId || saved.NumberOfSessions != request.NumberOfSessions || saved.FrequencyPerWeek != request.FrequencyPerWeek || saved.Exercises != request.Exercises || saved.TreatmentType != request.TreatmentType)
                throw Rule("AssessmentAlreadyCompleted", "This assessment has already been completed with different details.", 409);
            return (await ContextAsync(appointment.PatientId)).Plans.Single(p => p.Id == saved.Id);
        }
        var assessment = new Assessment { AppointmentId = appointment.Id, PatientId = patient.Id, SeniorId = appointment.SeniorId!,
            Diagnosis = request.Diagnosis.Trim(), ConditionType = request.TreatmentType, Priority = "Routine", CompletedAt = DateTime.UtcNow, AssessmentDate = DateTime.UtcNow };
        var plan = new TreatmentPlan { Assessment = assessment, DoctorId = request.DoctorId, NumberOfSessions = request.NumberOfSessions,
            FrequencyPerWeek = request.FrequencyPerWeek, TreatmentType = request.TreatmentType, Exercises = request.Exercises, Status = StatusPlan.Active };
        for (var i = 1; i <= request.NumberOfSessions; i++) plan.Sessions.Add(new TreatmentPlanSession { SessionNumber = i });
        db.TreatmentPlans.Add(plan);
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return (await ContextAsync(patient.Id)).Plans.Single(p => p.Id == plan.Id);
    }

    public async Task CompleteExistingAssessmentAsync(int id, string actor, bool supervisor)
    {
        var assessment = await db.Assessments.SingleOrDefaultAsync(a => a.Id == id) ?? throw Rule("AssessmentNotFound", "Assessment was not found.", 404);
        if (!supervisor && assessment.SeniorId != actor) throw Rule("AssessmentOwner", "Only the assigned Senior can sign off this assessment.", 403);
        if (string.IsNullOrWhiteSpace(assessment.Diagnosis)) throw Rule("ClinicalDetailsRequired", "A saved diagnosis is required before completion.");
        assessment.CompletedAt ??= DateTime.UtcNow; await db.SaveChangesAsync();
    }
}
