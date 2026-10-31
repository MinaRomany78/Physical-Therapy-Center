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

public partial class CourseFinanceService(ApplicationDbContext db)
{
    private static BookingRuleException Rule(string code, string message, int status = 400) => new("Booking." + code, message, status);
    public static bool Money(decimal value) => value >= 0 && value <= 999999999 && decimal.Round(value, 2) == value;
    public static bool InsurerReceipt(Payment payment) => payment.Status == PaymentStatus.Paid &&
        (payment.PaymentMethod == PaymentMethod.Insurance || payment.PayerType is "Insurer" or "Insurance");
    public static bool PatientReceipt(Payment payment) => payment.Status == PaymentStatus.Paid && !InsurerReceipt(payment);
    public static decimal Allocate(decimal amount, int count, int number)
    {
        var cents = decimal.ToInt64(amount * 100); var basis = cents / count; var remainder = cents % count;
        return (basis + (number <= remainder ? 1 : 0)) / 100m;
    }
    private IQueryable<Invoice> CourseInvoices(int id) => db.Invoices.Include(i => i.Payments).Where(i => i.CourseAgreementId == id);
    public async Task<List<int>> AvailableNumbersAsync(int planId, int count)
    {
        var sessions = await db.TreatmentPlanSessions.AsNoTracking().Where(s => s.TreatmentPlanId == planId).ToListAsync();
        var ids = sessions.Select(s => s.Id).ToList();
        var used = await db.Appointments.Where(a => a.TreatmentPlanSessionId != null && ids.Contains(a.TreatmentPlanSessionId.Value) &&
            (a.ActualCheckIn != null || a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow))
            .Select(a => a.TreatmentPlanSessionId!.Value).Distinct().ToListAsync();
        return Enumerable.Range(1, count).Where(number => !sessions.Any(s => s.SessionNumber == number && (s.Status == SessionStatus.Completed || used.Contains(s.Id)))).ToList();
    }
    private async Task<List<int>> ConsumedSessionIdsAsync(int planId)
    {
        var sessionIds = await db.TreatmentPlanSessions.Where(s => s.TreatmentPlanId == planId &&
            (s.Status == SessionStatus.Completed || s.Status == SessionStatus.InProgress)).Select(s => s.Id).ToListAsync();
        var appointmentIds = await db.Appointments.Where(a => a.TreatmentPlanSessionId != null &&
            db.TreatmentPlanSessions.Any(s => s.Id == a.TreatmentPlanSessionId && s.TreatmentPlanId == planId) &&
            (a.TreatmentStartedAt != null || a.Status == AppointmentStatus.Completed))
            .Select(a => a.TreatmentPlanSessionId!.Value).ToListAsync();
        return sessionIds.Concat(appointmentIds).Distinct().ToList();
    }
    public async Task<CourseFinancialSummary?> SummaryAsync(int planId)
    {
        var agreement = await db.TreatmentCourseAgreements.AsNoTracking().SingleOrDefaultAsync(a => a.TreatmentPlanId == planId);
        return agreement == null ? null : await SummaryAsync(agreement);
    }
    private async Task<CourseFinancialSummary> SummaryAsync(TreatmentCourseAgreement a)
    {
        var invoices = await CourseInvoices(a.Id).AsNoTracking().ToListAsync();
        var paid = invoices.SelectMany(i => i.Payments).Where(PatientReceipt).Sum(p => p.Amount);
        var consumed = await ConsumedSessionIdsAsync(a.TreatmentPlanId);
        var consumedCost = invoices.Where(i => i.TreatmentPlanSessionId != null && consumed.Contains(i.TreatmentPlanSessionId.Value)).Sum(i => i.PatientAmount);
        var nextInvoice = invoices.Where(i => i.TreatmentPlanSessionId != null && !consumed.Contains(i.TreatmentPlanSessionId.Value)).OrderBy(i => i.Id).FirstOrDefault();
        var next = nextInvoice?.PatientAmount
            ?? (a.PricingMode == "PerSession" ? a.TotalAmount / a.CoveredSessions : 0);
        var patient = a.PricingMode == "Package" ? a.PatientAmount : invoices.Sum(i => i.PatientAmount);
        var total = a.PricingMode == "Package" ? a.TotalAmount : invoices.Sum(i => i.TotalAmount);
        return new(a.Id, a.TreatmentPlanId, a.FundingType, a.PricingMode, a.PrescribedSessions, a.CoveredSessions, a.ApprovedSessions,
            a.UnitPrice, a.DiscountType, a.DiscountValue, a.PricingMode == "Package" ? a.GrossAmount : a.UnitPrice * invoices.Count,
            a.PricingMode == "Package" ? a.DiscountAmount : a.UnitPrice * invoices.Count - total, total, patient,
            a.PricingMode == "Package" ? a.InsuranceAmount : invoices.Sum(i => i.InsuranceAmount), a.CopayPercentage,
            paid, Math.Max(0, patient - paid), consumed.Count, next,
            Math.Max(0, a.PricingMode == "Package" ? consumedCost + next - paid : next - (nextInvoice?.Payments.Where(PatientReceipt).Sum(p => p.Amount) ?? 0)), true, a.ApprovalReference);
    }

    public sealed record Quote(TreatmentCourseAgreement Agreement, CourseFinancialSummary Financial, List<string> Errors);
    public async Task<Quote> QuoteAsync(BookingRequest request, BookingContext context, Patient patient)
    {
        var errors = new List<string>();
        var plan = context.Plans.FirstOrDefault(p => p.Id == request.TreatmentPlanId);
        if (plan == null) throw Rule("PlanRequired", "Select the prescribed treatment plan.");
        var existing = await db.TreatmentCourseAgreements.AsNoTracking().SingleOrDefaultAsync(a => a.TreatmentPlanId == plan.Id);
        TreatmentCourseAgreement agreement;
        if (existing != null)
        {
            agreement = existing;
            if ((existing.FundingType == "Insurance") != context.IsInsurance || context.IsInsurance &&
                (existing.InsuranceCompanyId != patient.InsuranceCompanyId || existing.CopayPercentage != context.CopayPercentage || existing.ApprovedSessions != patient.InsuranceDetails?.ApprovedSessions))
                errors.Add("ExistingAgreementReviewRequired");
        }
        else
        {
            var insurance = context.IsInsurance;
            var otherAgreements = insurance ? await db.TreatmentCourseAgreements.AsNoTracking().Where(a => a.PatientId == patient.Id && a.FundingType == "Insurance").ToListAsync() : [];
            var legacyOutside = insurance ? await db.Appointments.CountAsync(a => a.PatientId == patient.Id && a.AppointmentType == "Session" &&
                a.TreatmentPlanSession != null && a.TreatmentPlanSession.TreatmentPlanId != plan.Id &&
                (a.ActualCheckIn != null || a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow) &&
                !db.TreatmentCourseAgreements.Any(c => c.TreatmentPlanId == a.TreatmentPlanSession.TreatmentPlanId)) : 0;
            var approvalAvailable = (patient.InsuranceDetails?.ApprovedSessions ?? 0) - otherAgreements.Sum(a => a.CoveredSessions) - legacyOutside;
            var count = insurance ? Math.Min(plan.PlannedSessions, approvalAvailable) : plan.PlannedSessions;
            if (count <= 0) { errors.Add("InsuranceLimit"); count = 1; }
            var mode = insurance ? "Package" : request.PricingMode;
            if (mode is not ("Package" or "PerSession")) errors.Add("PricingModeRequired");
            var unit = request.UnitPrice ?? 0;
            if (!Money(unit) || unit <= 0) errors.Add("PriceRequired");
            if (request.PackagePrice != null) errors.Add("UnitPriceRequired");
            var discountType = request.DiscountType;
            var value = request.DiscountValue;
            if (!Money(request.Discount)) errors.Add("DiscountInvalid");
            if (request.Discount > 0)
            {
                if (discountType != "None" || value != 0) errors.Add("DiscountExclusive");
                else { discountType = "Fixed"; value = request.Discount; }
            }
            var discountBase = mode == "Package" ? unit * count : unit;
            if (discountType is not ("None" or "Fixed" or "Percent") || !Money(value) ||
                discountType == "Percent" && value > 100 || discountType == "None" && value != 0) errors.Add("DiscountInvalid");
            if (insurance && (value != 0 || discountType != "None")) errors.Add("InsuranceDiscountUnavailable");
            var discount = discountType == "Percent" ? decimal.Round(discountBase * value / 100, 2, MidpointRounding.AwayFromZero)
                : discountType == "Fixed" ? value : 0;
            if (discount > discountBase) errors.Add("DiscountInvalid");
            var gross = unit * count;
            var courseDiscount = mode == "Package" ? discount : discount * count;
            var total = Math.Max(0, gross - courseDiscount);
            if (!Money(gross) || !Money(total)) errors.Add("FinancialInvalid");
            var copay = insurance ? context.CopayPercentage ?? -1 : 100;
            if (copay is < 0 or > 100) { errors.Add("CopayRequired"); copay = 0; }
            var patientAmount = decimal.Round(total * copay / 100, 2, MidpointRounding.AwayFromZero);
            var snapshot = insurance ? JsonSerializer.Serialize(new { patient.Id, patient.InsuranceCompanyId, patient.InsuranceDetails }) : null;
            agreement = new() { PatientId = patient.Id, TreatmentPlanId = plan.Id, FundingType = insurance ? "Insurance" : "SelfPay",
                PricingMode = mode, PrescribedSessions = plan.PlannedSessions, CoveredSessions = count, UnitPrice = unit,
                DiscountType = discountType, DiscountValue = value, DiscountAmount = courseDiscount, GrossAmount = gross,
                TotalAmount = total, PatientAmount = patientAmount, InsuranceAmount = total - patientAmount, CopayPercentage = copay,
                InsuranceCompanyId = insurance ? patient.InsuranceCompanyId : null, ApprovedSessions = insurance ? patient.InsuranceDetails?.ApprovedSessions : null,
                ApprovalSnapshotJson = snapshot, ApprovalReference = snapshot == null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))),
                InstallmentsJson = JsonSerializer.Serialize(request.Installments) };
            if (!await LegacyMatchesAsync(agreement)) errors.Add("LegacyAgreementReviewRequired");
        }
        var available = await AvailableNumbersAsync(plan.Id, agreement.CoveredSessions);
        if (available.Count < request.StartsAt.Count) errors.Add(context.IsInsurance ? "InsuranceLimit" : "PlanLimit");
        var summary = existing == null ? new CourseFinancialSummary(null, plan.Id, agreement.FundingType, agreement.PricingMode,
            agreement.PrescribedSessions, agreement.CoveredSessions, agreement.ApprovedSessions, agreement.UnitPrice, agreement.DiscountType,
            agreement.DiscountValue, agreement.GrossAmount, agreement.DiscountAmount, agreement.TotalAmount, agreement.PatientAmount,
            agreement.InsuranceAmount, agreement.CopayPercentage, 0, agreement.PatientAmount, 0,
            Allocate(agreement.PatientAmount, agreement.CoveredSessions, 1), Allocate(agreement.PatientAmount, agreement.CoveredSessions, 1), false, agreement.ApprovalReference)
            : await SummaryAsync(existing);
        var legacyInvoices = existing == null ? await db.Invoices.AsNoTracking().Include(i => i.Payments).Include(i => i.Appointment)
            .Where(i => i.CourseAgreementId == null && i.Appointment != null && i.Appointment.TreatmentPlanSession != null &&
                i.Appointment.TreatmentPlanSession.TreatmentPlanId == plan.Id).ToListAsync() : [];
        if (existing == null && legacyInvoices.Count > 0)
        {
            var paid = legacyInvoices.SelectMany(i => i.Payments).Where(PatientReceipt).Sum(p => p.Amount);
            var consumed = await ConsumedSessionIdsAsync(plan.Id);
            var consumedCost = legacyInvoices.Where(i => i.Appointment != null && i.Appointment.TreatmentPlanSessionId != null && consumed.Contains(i.Appointment.TreatmentPlanSessionId.Value)).Sum(i => i.PatientAmount);
            summary = summary with {PaidAmount = paid, RemainingAmount = Math.Max(0, summary.PatientAmount - paid), ConsumedSessions = consumed.Count,
                AdditionalPaymentRequired = Math.Max(0, consumedCost + summary.NextSessionPatientAmount - paid)};
        }
        if (agreement.PricingMode == "PerSession")
        {
            var pricedNumbers = existing == null ? await db.Invoices.Where(i => i.CourseAgreementId == null && i.Appointment != null && i.Appointment.TreatmentPlanSession != null && i.Appointment.TreatmentPlanSession.TreatmentPlanId == plan.Id)
                .Select(i => i.Appointment!.TreatmentPlanSession!.SessionNumber).ToListAsync() : await (from i in db.Invoices join s in db.TreatmentPlanSessions on i.TreatmentPlanSessionId equals s.Id
                where i.CourseAgreementId == existing.Id select s.SessionNumber).ToListAsync();
            var newCount = available.Take(request.StartsAt.Count).Count(number => !pricedNumbers.Contains(number));
            var addition = newCount * (agreement.TotalAmount / agreement.CoveredSessions);
            var total = (existing == null ? legacyInvoices.Sum(i => i.TotalAmount) : summary.TotalAmount) + addition;
            var gross = agreement.UnitPrice * (pricedNumbers.Count + newCount);
            summary = summary with { GrossAmount = gross, DiscountAmount = gross - total, TotalAmount = total, PatientAmount = total, InsuranceAmount = 0, RemainingAmount = Math.Max(0, total - summary.PaidAmount) };
        }
        if (!Money(request.Deposit) || request.Deposit > summary.RemainingAmount || request.Deposit > 0 &&
            (request.PaymentMethod == null || !Enum.IsDefined(request.PaymentMethod.Value) || request.PaymentMethod == PaymentMethod.Insurance)) errors.Add("DepositInvalid");
        if (request.Installments.Any(i => !Money(i.Amount) || i.Amount <= 0 || i.DueDate < DateOnly.FromDateTime(DateTime.Today)) ||
            request.Installments.Count > 100 || request.Installments.Select(i => i.DueDate).Distinct().Count() != request.Installments.Count ||
            request.Installments.Count > 0 && request.Installments.Sum(i => i.Amount) != summary.RemainingAmount - request.Deposit) errors.Add("InstallmentsInvalid");
        return new(agreement, summary, errors.Distinct().ToList());
    }

    private async Task<bool> LegacyMatchesAsync(TreatmentCourseAgreement agreement)
    {
        var legacy = await db.Invoices.AsNoTracking().Include(i => i.Appointment).ThenInclude(a => a!.TreatmentPlanSession)
            .Where(i => i.CourseAgreementId == null && i.PatientId == agreement.PatientId && i.Type == InvoiceType.Session).ToListAsync();
        if (legacy.Any(i => i.Appointment?.TreatmentPlanSession == null)) return false;
        var matched = legacy.Where(i => i.Appointment!.TreatmentPlanSession!.TreatmentPlanId == agreement.TreatmentPlanId).ToList();
        var consumed = await ConsumedSessionIdsAsync(agreement.TreatmentPlanId);
        if (consumed.Any(id => !matched.Any(i => i.Appointment!.TreatmentPlanSessionId == id))) return false;
        if (matched.GroupBy(i => i.Appointment!.TreatmentPlanSessionId).Any(group => group.Count() > 1)) return false;
        return matched.All(i => { var number = i.Appointment!.TreatmentPlanSession!.SessionNumber;
            return number >= 1 && number <= agreement.CoveredSessions &&
                i.TotalAmount == Allocate(agreement.TotalAmount, agreement.CoveredSessions, number) &&
                i.PatientAmount == Allocate(agreement.PatientAmount, agreement.CoveredSessions, number) && i.InsuranceAmount == i.TotalAmount - i.PatientAmount; });
    }

    public async Task<TreatmentCourseAgreement> EnsureAsync(Quote quote)
    {
        if (quote.Errors.Count > 0) throw Rule("Validation", string.Join(", ", quote.Errors), 409);
        var a = await db.TreatmentCourseAgreements.SingleOrDefaultAsync(x => x.TreatmentPlanId == quote.Agreement.TreatmentPlanId);
        if (a != null) return a;
        a = quote.Agreement; db.TreatmentCourseAgreements.Add(a); await db.SaveChangesAsync();
        var sessions = await db.TreatmentPlanSessions.Where(s => s.TreatmentPlanId == a.TreatmentPlanId).ToListAsync();
        var legacy = await db.Invoices.Include(i => i.Appointment).Where(i => i.CourseAgreementId == null && i.Appointment != null &&
            i.Appointment.TreatmentPlanSession != null && i.Appointment.TreatmentPlanSession.TreatmentPlanId == a.TreatmentPlanId).ToListAsync();
        foreach (var invoice in legacy) { invoice.CourseAgreementId = a.Id; invoice.TreatmentPlanSessionId = invoice.Appointment!.TreatmentPlanSessionId; }
        var batchIds = legacy.Where(i => i.Appointment!.BookingBatchId != null).Select(i => i.Appointment!.BookingBatchId!.Value).Distinct().ToList();
        foreach (var batch in await db.BookingBatches.Where(b => batchIds.Contains(b.Id)).ToListAsync()) batch.CourseAgreementId = a.Id;
        await db.SaveChangesAsync();
        if (a.PricingMode == "Package") foreach (var session in sessions.Where(s => s.SessionNumber <= a.CoveredSessions).OrderBy(s => s.SessionNumber)) await EnsureInvoiceAsync(a, session);
        return a;
    }
    public async Task<Invoice> EnsureInvoiceAsync(TreatmentCourseAgreement agreement, TreatmentPlanSession session)
    {
        var existing = await db.Invoices.Include(i => i.Payments).SingleOrDefaultAsync(i => i.CourseAgreementId == agreement.Id && i.TreatmentPlanSessionId == session.Id);
        if (existing != null) return existing;
        var total = Allocate(agreement.TotalAmount, agreement.CoveredSessions, session.SessionNumber);
        var patient = Allocate(agreement.PatientAmount, agreement.CoveredSessions, session.SessionNumber);
        var invoice = new Invoice { CourseAgreementId = agreement.Id, TreatmentPlanSessionId = session.Id, PatientId = agreement.PatientId,
            InvoiceNumber = $"C-{agreement.Id}-{session.SessionNumber}", Type = InvoiceType.Session, TotalAmount = total,
            PatientAmount = patient, InsuranceAmount = total - patient, Status = total == 0 ? InvoiceStatus.Waived : InvoiceStatus.Pending };
        db.Invoices.Add(invoice); await db.SaveChangesAsync(); return invoice;
    }

    public async Task CollectAsync(int agreementId, string key, decimal amount, PaymentMethod method)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 90 || !Money(amount) || amount <= 0 || !Enum.IsDefined(method) || method == PaymentMethod.Insurance)
            throw Rule("InvalidPayment", "Enter a positive patient receipt and its collection method.");
        var prefix = $"course:{key}:";
        var previous = await db.Payments.Include(p => p.Invoice).Where(p => p.IdempotencyKey != null && p.IdempotencyKey.StartsWith(prefix)).ToListAsync();
        if (previous.Count > 0)
        {
            if (previous.Any(p => p.Invoice.CourseAgreementId != agreementId || p.PaymentMethod != method) || previous.Sum(p => p.Amount) != amount)
                throw Rule("RetryKeyConflict", "This receipt key belongs to different payment details.", 409);
            return;
        }
        var invoices = await CourseInvoices(agreementId).OrderBy(i => i.Id).ToListAsync();
        if (amount > invoices.Sum(i => Math.Max(0, i.PatientAmount - i.Payments.Where(PatientReceipt).Sum(p => p.Amount))))
            throw Rule("PaymentExceedsBalance", "Payment exceeds the remaining patient balance.");
        var remaining = amount;
        foreach (var invoice in invoices)
        {
            var payment = Math.Min(remaining, Math.Max(0, invoice.PatientAmount - invoice.Payments.Where(PatientReceipt).Sum(p => p.Amount)));
            if (payment <= 0) continue;
            invoice.Payments.Add(new Payment { Amount = payment, PaymentMethod = method, CollectionMethod = method.ToString(), PayerType = "Patient",
                IdempotencyKey = prefix + invoice.Id, Status = PaymentStatus.Paid, PaymentDate = DateTime.UtcNow });
            RefreshInvoice(invoice); remaining -= payment;
            if (invoice.TreatmentPlanSessionId != null)
            {
                var session = await db.TreatmentPlanSessions.FindAsync(invoice.TreatmentPlanSessionId);
                if (session != null) session.PaymentStatus = invoice.Status == InvoiceStatus.Paid ? PaymentStatus.Paid : PaymentStatus.Pending;
            }
        }
        await db.SaveChangesAsync();
    }
    public static void RefreshInvoice(Invoice invoice)
    {
        invoice.PaidAmount = invoice.Payments.Where(PatientReceipt).Sum(p => p.Amount);
        invoice.Status = invoice.TotalAmount == 0 ? InvoiceStatus.Waived : invoice.Payments.Where(p => p.Status == PaymentStatus.Paid).Sum(p => p.Amount) >= invoice.TotalAmount ? InvoiceStatus.Paid : InvoiceStatus.Pending;
    }
}
