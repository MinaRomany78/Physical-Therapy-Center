using System.Data;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Infrastructure.Services;

public partial class CourseFinanceService
{
    public async Task<PaymentGate> GateAsync(int appointmentId)
    {
        var appointment = await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == appointmentId)
            ?? throw Rule("AppointmentNotFound", "Appointment was not found.", 404);
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Payments).SingleOrDefaultAsync(i => i.AppointmentId == appointmentId);
        if (invoice == null) return new(appointmentId, false, "", null, "Review", 0, 0, 0, 0, 0);
        if (invoice.CourseAgreementId is int courseId)
        {
            var agreement = await db.TreatmentCourseAgreements.AsNoTracking().SingleAsync(a => a.Id == courseId);
            var invoices = await CourseInvoices(courseId).AsNoTracking().ToListAsync();
            var consumed = await ConsumedSessionIdsAsync(agreement.TreatmentPlanId);
            var package = agreement.PricingMode == "Package";
            var required = package ? invoices.Where(i => i.TreatmentPlanSessionId == invoice.TreatmentPlanSessionId ||
                i.TreatmentPlanSessionId != null && consumed.Contains(i.TreatmentPlanSessionId.Value)).Sum(i => i.PatientAmount) : invoice.PatientAmount;
            var paid = (package ? invoices : [invoice]).SelectMany(i => i.Payments).Where(PatientReceipt).Sum(p => p.Amount);
            var balance = package
                ? Math.Max(0, invoices.Sum(i => i.PatientAmount) - invoices.SelectMany(i => i.Payments).Where(PatientReceipt).Sum(p => p.Amount))
                : Math.Max(0, invoice.PatientAmount - paid);
            return new(appointmentId, paid >= required, $"course:{courseId}", invoice.Id, agreement.PricingMode, paid, required, Math.Max(0, required - paid), balance, consumed.Count);
        }
        var batch = appointment.BookingBatchId is int batchId ? await db.BookingBatches.AsNoTracking().SingleAsync(b => b.Id == batchId) : null;
        if (batch?.PricingMode == "Package")
        {
            var invoices = await db.Invoices.AsNoTracking().Include(i => i.Appointment).Include(i => i.Payments)
                .Where(i => i.Appointment != null && i.Appointment.BookingBatchId == batch.Id).ToListAsync();
            var required = invoices.Where(i => i.Id == invoice.Id || i.Appointment!.TreatmentStartedAt != null || i.Appointment.Status == AppointmentStatus.Completed).Sum(i => i.PatientAmount);
            var paid = invoices.SelectMany(i => i.Payments).Where(PatientReceipt).Sum(p => p.Amount);
            return new(appointmentId, paid >= required, batch.IdempotencyKey, invoice.Id, "Package", paid, required, Math.Max(0, required - paid), Math.Max(0, batch.PatientAmount - paid), invoices.Count(i => i.Appointment!.TreatmentStartedAt != null));
        }
        var received = invoice.Payments.Where(PatientReceipt).Sum(p => p.Amount);
        return new(appointmentId, received >= invoice.PatientAmount, batch?.IdempotencyKey ?? $"invoice:{invoice.Id}", invoice.Id, "PerSession", received,
            invoice.PatientAmount, Math.Max(0, invoice.PatientAmount - received), Math.Max(0, invoice.PatientAmount - received), appointment.TreatmentStartedAt != null ? 1 : 0);
    }

    public async Task<PaymentGate> CollectForAppointmentAsync(int appointmentId, CollectBookingPaymentRequest request)
    {
        // Historical batch packages keep their saved charges and existing allocation/retry rules.
        // The shared account collector owns its Serializable transaction; do not nest it below.
        var initial = await GateAsync(appointmentId);
        if (initial.PricingMode == "Package" && !initial.AccountKey.StartsWith("course:", StringComparison.Ordinal))
        {
            await new BookingService(db).CollectAsync(initial.AccountKey, request);
            return await GateAsync(appointmentId);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var gate = await GateAsync(appointmentId);
        if (gate.InvoiceId == null) throw Rule("FinancialAgreementRequired", "Reconcile the session's financial agreement before collection.", 409);
        if (gate.PricingMode == "Package" && gate.AccountKey.StartsWith("course:", StringComparison.Ordinal))
            await CollectAsync(int.Parse(gate.AccountKey[7..]), request.IdempotencyKey, request.Amount,
                request.Method ?? throw Rule("InvalidPayment", "Select the actual collection method."));
        else
        {
            if (request.Method == null || request.Method == PaymentMethod.Insurance || !Enum.IsDefined(request.Method.Value) || !Money(request.Amount) || request.Amount <= 0 ||
                string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 90) throw Rule("InvalidPayment", "Enter a valid positive patient receipt and collection method.");
            var key = $"start:{request.IdempotencyKey}";
            var previous = await db.Payments.SingleOrDefaultAsync(p => p.IdempotencyKey == key);
            if (previous != null)
            {
                if (previous.InvoiceId != gate.InvoiceId || previous.Amount != request.Amount || previous.PaymentMethod != request.Method)
                    throw Rule("RetryKeyConflict", "This receipt key belongs to different payment details.", 409);
            }
            else
            {
                var invoice = await db.Invoices.Include(i => i.Payments).SingleAsync(i => i.Id == gate.InvoiceId);
                var balance = invoice.PatientAmount - invoice.Payments.Where(PatientReceipt).Sum(p => p.Amount);
                if (request.Amount > balance) throw Rule("PaymentExceedsBalance", "Payment exceeds the remaining patient balance.");
                invoice.Payments.Add(new Payment { Amount = request.Amount, PaymentMethod = request.Method.Value, PayerType = "Patient", CollectionMethod = request.Method.Value.ToString(),
                    Status = PaymentStatus.Paid, IdempotencyKey = key, PaymentDate = DateTime.UtcNow });
                RefreshInvoice(invoice);
                var sessionId = invoice.TreatmentPlanSessionId ?? await db.Appointments.Where(a => a.Id == appointmentId).Select(a => a.TreatmentPlanSessionId).SingleAsync();
                if (sessionId != null)
                {
                    var session = await db.TreatmentPlanSessions.FindAsync(sessionId);
                    if (session != null) session.PaymentStatus = invoice.Status == InvoiceStatus.Paid ? PaymentStatus.Paid : PaymentStatus.Pending;
                }
                await db.SaveChangesAsync();
            }
        }
        await transaction.CommitAsync(); return await GateAsync(appointmentId);
    }
}
