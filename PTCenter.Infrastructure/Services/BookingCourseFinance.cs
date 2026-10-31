using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Infrastructure.Services;

public partial class BookingService
{
    private async Task CreateCourseBookingAsync(BookingRequest request, BookingContext context)
    {
        var finance = new CourseFinanceService(db);
        var quote = await finance.QuoteAsync(request, context, await PatientAsync(request.PatientId));
        var plan = await db.TreatmentPlans.Include(p => p.Sessions).SingleAsync(p => p.Id == request.TreatmentPlanId);
        for (var number = 1; number <= plan.NumberOfSessions; number++)
            if (!plan.Sessions.Any(s => s.SessionNumber == number)) plan.Sessions.Add(new TreatmentPlanSession { SessionNumber = number, Status = SessionStatus.Pending, PaymentStatus = PaymentStatus.Pending });
        await db.SaveChangesAsync();
        var agreement = await finance.EnsureAsync(quote);
        var numbers = (await finance.AvailableNumbersAsync(plan.Id, agreement.CoveredSessions)).Take(request.StartsAt.Count).ToList();
        if (numbers.Count != request.StartsAt.Count) throw Rule("PlanLimit", "The prescribed course has insufficient unbooked covered sessions.", 409);
        var batch = new BookingBatch { IdempotencyKey = request.IdempotencyKey, RequestHash = Hash(request), PatientId = request.PatientId,
            CourseAgreementId = agreement.Id, PricingMode = agreement.PricingMode, InstallmentsJson = "[]" };
        db.BookingBatches.Add(batch); await db.SaveChangesAsync();
        for (var index = 0; index < numbers.Count; index++)
        {
            var session = plan.Sessions.Single(s => s.SessionNumber == numbers[index]);
            var start = request.StartsAt[index].UtcDateTime;
            var appointment = new Appointment { PatientId = request.PatientId, DoctorId = request.ClinicianId, RoomId = request.RoomId,
                TreatmentPlanSessionId = session.Id, AppointmentType = "Session", StartTime = start, EndTime = start.AddMinutes(60),
                Status = AppointmentStatus.Pending, BookingBatchId = batch.Id, BookingSequence = index, InsuranceFunded = agreement.FundingType == "Insurance" };
            db.Appointments.Add(appointment); session.SessionDate = start; session.Status = SessionStatus.Pending; await db.SaveChangesAsync();
            var invoice = await finance.EnsureInvoiceAsync(agreement, session);
            invoice.AppointmentId = appointment.Id;
            session.PaymentStatus = invoice.Status == InvoiceStatus.Paid ? PaymentStatus.Paid : PaymentStatus.Pending;
        }
        await db.SaveChangesAsync();
        if (request.Deposit > 0) await finance.CollectAsync(agreement.Id, request.IdempotencyKey, request.Deposit, request.PaymentMethod!.Value);
    }
    private async Task<BookingReceipt> CourseReceiptAsync(BookingBatch batch, List<Appointment> appointments)
    {
        var agreement = await db.TreatmentCourseAgreements.AsNoTracking().SingleAsync(a => a.Id == batch.CourseAgreementId);
        var summary = (await new CourseFinanceService(db).SummaryAsync(agreement.TreatmentPlanId))!;
        var ids = appointments.Where(a => a.TreatmentPlanSessionId != null).Select(a => a.TreatmentPlanSessionId!.Value).ToList();
        var invoices = await db.Invoices.Where(i => i.CourseAgreementId == agreement.Id && i.TreatmentPlanSessionId != null && ids.Contains(i.TreatmentPlanSessionId.Value)).Select(i => i.Id).ToListAsync();
        return new(batch.IdempotencyKey, batch.PatientId, "Committed", await CompleteRowsAsync(appointments),
            invoices, summary.TotalAmount, summary.PatientAmount, summary.InsuranceAmount, summary.PaidAmount, summary.RemainingAmount, null,
            JsonSerializer.Deserialize<List<InstallmentRequest>>(agreement.InstallmentsJson) ?? []);
    }
    private async Task<int?> CourseIdAsync(string key)
    {
        if (key.StartsWith("course:", StringComparison.Ordinal) && int.TryParse(key[7..], out var id))
            return await db.TreatmentCourseAgreements.AnyAsync(a => a.Id == id) ? id : null;
        return await db.BookingBatches.Where(b => b.IdempotencyKey == key).Select(b => b.CourseAgreementId).SingleOrDefaultAsync();
    }
    private async Task<BookingReceipt> AnyCourseReceiptAsync(int id)
    {
        var key = await db.BookingBatches.Where(b => b.CourseAgreementId == id).OrderBy(b => b.Id).Select(b => b.IdempotencyKey).FirstAsync();
        return await ReceiptAsync(key);
    }
}
