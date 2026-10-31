using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Domain.Entities;
using PTCenter.Infrastructure.Services;
using Xunit;

namespace PTCenter.Tests;

public sealed partial class BookingDatabaseTests
{
    [Theory]
    [InlineData("Percent",10,270)]
    [InlineData("Fixed",10,290)]
    [InlineData("None",0,300)]
    public async Task Package_discount_applies_once_to_prescribed_course(string kind, decimal value, decimal total)
    {
        var request=Treatment(await PlanAsync(3),1);request.PricingMode="Package";request.DiscountType=kind;request.DiscountValue=value;
        var preview=await service.PreviewAsync(request); Assert.Equal(total,preview.TotalAmount);
        var receipt=await service.CreateAsync(request);Assert.Equal(total,receipt.TotalAmount);
        Assert.Equal(total,await db.Invoices.SumAsync(i=>i.TotalAmount));Assert.Equal(3,await db.Invoices.CountAsync());
    }
    [Theory]
    [InlineData("Percent",12.5,87.50)]
    [InlineData("Fixed",12.5,87.50)]
    public async Task Per_session_discount_requires_actual_payment_for_that_session(string kind,decimal value,decimal price)
    {
        var request=Treatment(await PlanAsync(3),2);request.DiscountType=kind;request.DiscountValue=value;
        var receipt=await service.CreateAsync(request);Assert.Equal(price*2,receipt.PatientAmount);
        var id=receipt.Appointments[0].Id; var finance=new CourseFinanceService(db);
        Assert.Equal(price,(await finance.GateAsync(id)).AdditionalPaymentRequired);
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.TransitionAsync(id,AppointmentStatus.InProgress));
        await finance.CollectForAppointmentAsync(id,new(Guid.NewGuid().ToString(),price,PaymentMethod.Online));
        await service.TransitionAsync(id,AppointmentStatus.InProgress);
        Assert.False((await finance.GateAsync(receipt.Appointments[1].Id)).CanStart);
    }
    [Fact] public async Task Package_is_reused_across_batches_and_rounding_sums_exactly()
    {
        var plan=await PlanAsync(3);var first=Treatment(plan,1);first.PricingMode="Package";first.DiscountType="Fixed";first.DiscountValue=1;
        await service.CreateAsync(first);
        var next=Treatment(plan,2);next.StartsAt=next.StartsAt.Select(t=>t.AddDays(5)).ToList();next.UnitPrice=999;
        var preview=await service.PreviewAsync(next);Assert.True(preview.Financial!.Reused);Assert.Equal(299,preview.TotalAmount);
        await service.CreateAsync(next);Assert.Single(await db.TreatmentCourseAgreements.ToListAsync());
        Assert.Equal(299,await db.Invoices.SumAsync(i=>i.TotalAmount));
        Assert.Equal(new[]{99.67m,99.67m,99.66m},await db.Invoices.OrderBy(i=>i.Id).Select(i=>i.PatientAmount).ToArrayAsync());
        Assert.Equal(2,await db.BookingBatches.CountAsync());
    }
    [Fact] public async Task Two_paid_package_sessions_do_not_cover_a_third_and_collection_is_idempotent()
    {
        var request=Treatment(await PlanAsync(4),3);request.PricingMode="Package";request.UnitPrice=500;request.Deposit=1000;request.PaymentMethod=PaymentMethod.Visa;
        var receipt=await service.CreateAsync(request);var finance=new CourseFinanceService(db);
        foreach(var appointment in receipt.Appointments.Take(2)) {await service.TransitionAsync(appointment.Id,AppointmentStatus.InProgress);await FinishClinicalForTest(appointment.Id);await service.TransitionAsync(appointment.Id,AppointmentStatus.Completed);}
        var third=receipt.Appointments[2].Id;var gate=await finance.GateAsync(third);
        Assert.False(gate.CanStart);Assert.Equal(500,gate.AdditionalPaymentRequired);Assert.Equal(1500,gate.RequiredCumulative);Assert.Equal(2,gate.ConsumedSessions);
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.TransitionAsync(third,AppointmentStatus.InProgress));
        var payment=new CollectBookingPaymentRequest(Guid.NewGuid().ToString(),500,PaymentMethod.Cash);
        Assert.True((await finance.CollectForAppointmentAsync(third,payment)).CanStart);
        await finance.CollectForAppointmentAsync(third,payment);Assert.Equal(1500,await db.Payments.SumAsync(p=>p.Amount));
        await Assert.ThrowsAsync<BookingRuleException>(()=>finance.CollectForAppointmentAsync(third,payment with {Amount=499}));
        await service.TransitionAsync(third,AppointmentStatus.InProgress);
    }
    [Fact] public async Task Cancellation_and_reschedule_reuse_session_invoice_and_payment_coverage()
    {
        var plan=await PlanAsync(3);var request=Treatment(plan,1);request.PricingMode="Package";request.Deposit=100;request.PaymentMethod=PaymentMethod.Cash;
        var first=await service.CreateAsync(request);await service.TransitionAsync(first.Appointments[0].Id,AppointmentStatus.Cancelled);
        var again=Treatment(plan,1);again.StartsAt=[start.AddDays(3)];var second=await service.CreateAsync(again);
        Assert.Equal(first.Appointments[0].TreatmentPlanSessionId,second.Appointments[0].TreatmentPlanSessionId);
        Assert.Equal(first.InvoiceIds,second.InvoiceIds);Assert.Equal(3,await db.Invoices.CountAsync());Assert.Equal(100,await db.Payments.SumAsync(p=>p.Amount));
        Assert.Equal(first.InvoiceIds,(await service.ReceiptAsync(first.IdempotencyKey)).InvoiceIds);
        await service.RescheduleAsync(second.Appointments[0].Id,new(){StartTime=start.AddDays(4),ClinicianId="doctor",RoomId=1});
        var gate=await new CourseFinanceService(db).GateAsync(second.Appointments[0].Id);Assert.True(gate.CanStart);Assert.Equal(0,gate.ConsumedSessions);
    }
    [Theory]
    [InlineData(0,0)] [InlineData(20,20)] [InlineData(100,100)]
    public async Task Insurance_package_uses_saved_copay_and_only_approved_sessions(decimal copay,decimal shortfall)
    {
        var plan=await PlanAsync(6,true);var patient=await db.Patients.FindAsync(patientId);patient!.InsuranceDetails!.CopayPercentage=copay;await db.SaveChangesAsync();
        var request=Treatment(plan,1);var receipt=await service.CreateAsync(request);Assert.Equal(400,receipt.TotalAmount);Assert.Equal(copay*4,receipt.PatientAmount);
        Assert.Equal(4,await db.Invoices.CountAsync());Assert.Equal(shortfall,(await new CourseFinanceService(db).GateAsync(receipt.Appointments[0].Id)).AdditionalPaymentRequired);
        if(copay==0) await service.TransitionAsync(receipt.Appointments[0].Id,AppointmentStatus.InProgress);
        Assert.All(await db.Invoices.ToListAsync(),i=>Assert.NotEqual(InvoiceStatus.Paid,i.Status));
    }
    [Fact] public async Task Insurance_earnings_claims_and_actual_settlements_remain_separate()
    {
        var request=Treatment(await PlanAsync(4,true),1);request.Deposit=20;request.PaymentMethod=PaymentMethod.Cash;
        var receipt=await service.CreateAsync(request);var finance=new CourseFinanceService(db);var id=receipt.Appointments[0].Id;var invoice=receipt.InvoiceIds[0];
        var accounts=await finance.InsuranceAccountsAsync();Assert.Equal(320,accounts.Companies.Single().Planned);Assert.Equal(0,accounts.Companies.Single().Earned);
        var claimRequest=new SubmitInsuranceClaimRequest(Guid.NewGuid().ToString(),invoice," claim-001 ");
        await Assert.ThrowsAsync<BookingRuleException>(()=>finance.SubmitClaimAsync(claimRequest,"ceo"));
        await service.TransitionAsync(id,AppointmentStatus.InProgress);await FinishClinicalForTest(id);await service.TransitionAsync(id,AppointmentStatus.Completed);
        var claim=await finance.SubmitClaimAsync(claimRequest,"ceo");Assert.Equal("claim-001",claim.SubmissionReference);Assert.Equal(claim.Id,(await finance.SubmitClaimAsync(claimRequest,"ceo")).Id);
        var settle=new SettleInsuranceClaimRequest(Guid.NewGuid().ToString(),30,PaymentMethod.Online,"settlement-001");
        await finance.SettleClaimAsync(claim.Id,settle);await finance.SettleClaimAsync(claim.Id,settle);
        accounts=await finance.InsuranceAccountsAsync();var company=accounts.Companies.Single();
        Assert.Equal(320,company.Planned);Assert.Equal(80,company.Earned);Assert.Equal(80,company.Submitted);Assert.Equal(30,company.Collected);Assert.Equal(50,company.Outstanding);
        Assert.Equal(20,(await service.ReceiptAsync(request.IdempotencyKey)).PaidAmount);
        var row=accounts.Receivables.Single(r=>r.InvoiceId==invoice);Assert.NotNull(row.ApprovalReference);Assert.Equal(claim.Id,row.ClaimId);Assert.NotNull(row.DeliveredAt);
        await Assert.ThrowsAsync<BookingRuleException>(()=>finance.SettleClaimAsync(claim.Id,settle with {IdempotencyKey=Guid.NewGuid().ToString(),Amount=51}));
        await finance.SettleClaimAsync(claim.Id,settle with {IdempotencyKey=Guid.NewGuid().ToString(),Amount=50,SettlementReference="final"});
        Assert.Equal(InvoiceStatus.Paid,(await db.Invoices.SingleAsync(i=>i.Id==invoice)).Status);
    }
    [Fact] public async Task Insurance_discount_and_two_discount_modes_are_rejected_atomically()
    {
        var request=Treatment(await PlanAsync(4,true),1);request.DiscountType="Percent";request.DiscountValue=5;
        Assert.Contains("InsuranceDiscountUnavailable",(await service.PreviewAsync(request)).Errors);
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.CreateAsync(request));
        request.Discount=1;Assert.Contains("DiscountExclusive",(await service.PreviewAsync(request)).Errors);
        Assert.Empty(await db.TreatmentCourseAgreements.ToListAsync());Assert.Empty(await db.Payments.ToListAsync());
    }
    private async Task<(int Plan, int Invoice, int Payment)> LegacyFinancialRecordAsync(decimal charge)
    {
        var plan=await PlanAsync(3);var session=new TreatmentPlanSession{TreatmentPlanId=plan,SessionNumber=1,Status=SessionStatus.Pending};db.TreatmentPlanSessions.Add(session);await db.SaveChangesAsync();
        var appointment=new Appointment{PatientId=patientId,DoctorId="doctor",RoomId=1,TreatmentPlanSessionId=session.Id,AppointmentType="Session",StartTime=start.UtcDateTime,EndTime=start.AddHours(1).UtcDateTime};db.Appointments.Add(appointment);await db.SaveChangesAsync();
        var invoice=new Invoice{PatientId=patientId,AppointmentId=appointment.Id,InvoiceNumber="LEGACY-1",Type=InvoiceType.Session,TotalAmount=charge,PatientAmount=charge,Status=InvoiceStatus.Pending};db.Invoices.Add(invoice);await db.SaveChangesAsync();
        var payment=new Payment{InvoiceId=invoice.Id,Amount=25,PaymentMethod=PaymentMethod.Cash,Status=PaymentStatus.Paid,IdempotencyKey="historical-receipt"};db.Payments.Add(payment);await db.SaveChangesAsync();return(plan,invoice.Id,payment.Id);
    }
    [Fact] public async Task Compatible_historical_charges_and_receipts_are_reused_without_rewriting_amounts()
    {
        var legacy=await LegacyFinancialRecordAsync(100);var request=Treatment(legacy.Plan,1);request.StartsAt=[start.AddDays(3)];request.PricingMode="Package";
        var quote=await service.PreviewAsync(request);Assert.True(quote.CanSave);Assert.Equal(25,quote.Financial!.PaidAmount);Assert.Equal(275,quote.RemainingAmount);
        var receipt=await service.CreateAsync(request);Assert.Equal(300,receipt.TotalAmount);Assert.Equal(25,receipt.PaidAmount);
        Assert.Equal(3,await db.Invoices.CountAsync());Assert.Equal(100,(await db.Invoices.FindAsync(legacy.Invoice))!.TotalAmount);
        var payment=(await db.Payments.FindAsync(legacy.Payment))!;Assert.Equal(25,payment.Amount);Assert.Equal("historical-receipt",payment.IdempotencyKey);
        Assert.Null(payment.PayerType);Assert.NotNull((await db.Invoices.FindAsync(legacy.Invoice))!.CourseAgreementId);
    }
    [Fact] public async Task Ambiguous_historical_prices_require_review_without_adding_or_rewriting_charges()
    {
        var legacy=await LegacyFinancialRecordAsync(90);var request=Treatment(legacy.Plan,1);request.StartsAt=[start.AddDays(3)];request.PricingMode="Package";
        Assert.Contains("LegacyAgreementReviewRequired",(await service.PreviewAsync(request)).Errors);
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.CreateAsync(request));Assert.Empty(await db.TreatmentCourseAgreements.ToListAsync());
        Assert.Single(await db.Invoices.ToListAsync());Assert.Equal(90,(await db.Invoices.FindAsync(legacy.Invoice))!.TotalAmount);Assert.Equal(25,(await db.Payments.FindAsync(legacy.Payment))!.Amount);
    }
    [Fact] public async Task Per_session_gate_and_summary_do_not_use_another_sessions_receipts()
    {
        var plan=await PlanAsync(3);var receipt=await service.CreateAsync(Treatment(plan,2));
        var finance=new CourseFinanceService(db);
        await finance.CollectForAppointmentAsync(receipt.Appointments[1].Id,new(Guid.NewGuid().ToString(),100,PaymentMethod.Cash));
        var gate=await finance.GateAsync(receipt.Appointments[0].Id);
        Assert.Equal(100,gate.AdditionalPaymentRequired);Assert.Equal(100,gate.PatientBalance);
        Assert.Equal(100,(await finance.SummaryAsync(plan))!.AdditionalPaymentRequired);
        await finance.CollectForAppointmentAsync(receipt.Appointments[0].Id,new(Guid.NewGuid().ToString(),25,PaymentMethod.Cash));
        Assert.Equal(75,(await finance.GateAsync(receipt.Appointments[0].Id)).PatientBalance);
    }
    [Fact] public async Task Historical_package_shortfall_can_be_collected_across_saved_batch_invoices()
    {
        var legacy=await LegacyFinancialRecordAsync(100);
        var first=await db.Invoices.Include(i=>i.Appointment).SingleAsync(i=>i.Id==legacy.Invoice);
        var batch=new BookingBatch{IdempotencyKey="legacy-package",RequestHash="historical",PatientId=patientId,PricingMode="Package",TotalAmount=200,PatientAmount=200,InstallmentsJson="[]"};
        db.BookingBatches.Add(batch);await db.SaveChangesAsync();
        first.Appointment!.BookingBatchId=batch.Id;first.Appointment.ActualCheckIn=DateTime.UtcNow.AddHours(-2);first.Appointment.ActualCheckOut=DateTime.UtcNow.AddHours(-1);first.Appointment.Status=AppointmentStatus.Completed;
        var session=new TreatmentPlanSession{TreatmentPlanId=legacy.Plan,SessionNumber=2};db.TreatmentPlanSessions.Add(session);await db.SaveChangesAsync();
        var second=new Appointment{PatientId=patientId,DoctorId="doctor",RoomId=1,TreatmentPlanSessionId=session.Id,BookingBatchId=batch.Id,BookingSequence=1,AppointmentType="Session",StartTime=start.AddDays(1).UtcDateTime,EndTime=start.AddDays(1).AddHours(1).UtcDateTime};
        db.Appointments.Add(second);await db.SaveChangesAsync();
        db.Invoices.Add(new(){PatientId=patientId,AppointmentId=second.Id,InvoiceNumber="LEGACY-2",Type=InvoiceType.Session,TotalAmount=100,PatientAmount=100});await db.SaveChangesAsync();
        var finance=new CourseFinanceService(db);var gate=await finance.GateAsync(second.Id);Assert.Equal(175,gate.AdditionalPaymentRequired);
        var payment=new CollectBookingPaymentRequest(Guid.NewGuid().ToString(),175,PaymentMethod.Visa);
        Assert.True((await finance.CollectForAppointmentAsync(second.Id,payment)).CanStart);
        await finance.CollectForAppointmentAsync(second.Id,payment);Assert.Equal(200,await db.Payments.SumAsync(p=>p.Amount));
        Assert.Empty(await db.TreatmentCourseAgreements.ToListAsync());Assert.Equal(200,await db.Invoices.SumAsync(i=>i.TotalAmount));
    }
    [Fact] public async Task Consumed_legacy_sessions_without_a_reconcilable_invoice_are_not_charged_again()
    {
        var plan=await PlanAsync(3);db.TreatmentPlanSessions.Add(new(){TreatmentPlanId=plan,SessionNumber=1,Status=SessionStatus.Completed});await db.SaveChangesAsync();
        var request=Treatment(plan,1);request.PricingMode="Package";
        Assert.Contains("LegacyAgreementReviewRequired",(await service.PreviewAsync(request)).Errors);
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.CreateAsync(request));Assert.Empty(await db.Invoices.ToListAsync());
    }
}
