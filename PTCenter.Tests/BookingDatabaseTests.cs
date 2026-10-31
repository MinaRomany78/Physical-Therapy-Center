using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;
using PTCenter.Infrastructure.Services;
using Xunit;

namespace PTCenter.Tests;

/// <summary>Real relational SQLite transactions, foreign keys and unique constraints. Never uses configured connection strings.</summary>
public sealed partial class BookingDatabaseTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private ApplicationDbContext db = null!;
    private BookingService service = null!;
    private int patientId;
    private readonly DateTimeOffset start = new(2030, 2, 4, 10, 0, 0, TimeSpan.Zero);
    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var users = new[] {
            new ApplicationUser { Id="patient", FullName="Test patient", UserName="test.patient", Gender=Gender.Male },
            new ApplicationUser { Id="senior", FullName="Senior", UserName="test.senior", Gender=Gender.Male },
            new ApplicationUser { Id="doctor", FullName="Doctor", UserName="test.doctor", Gender=Gender.Male },
            new ApplicationUser { Id="female", FullName="Female Senior", UserName="test.female", Gender=Gender.Female }
        };
        db.Users.AddRange(users);
        var seniorRole = await db.Roles.SingleAsync(r=>r.Name=="Senior");
        var doctorRole = await db.Roles.SingleAsync(r=>r.Name=="Doctor");
        db.UserRoles.AddRange(new IdentityUserRole<string> { UserId="senior", RoleId=seniorRole.Id },
            new IdentityUserRole<string>{UserId="female",RoleId=seniorRole.Id},new IdentityUserRole<string>{UserId="doctor",RoleId=doctorRole.Id});
        var patient = new Patient { ApplicationUserId="patient", PaymentType="Cash" }; db.Patients.Add(patient);
        db.Rooms.Add(new Room{Id=1,Name="Room",Capacity=1,IsActive=true});
        for(var day=0;day<7;day++) foreach(var clinician in new[]{"senior","doctor","female"})
            db.DoctorSchedules.Add(new DoctorSchedule{DoctorId=clinician,DayOfWeek=day,StartTime=new(8,0),EndTime=new(20,0)});
        await db.SaveChangesAsync(); patientId=patient.Id; service=new(db);
    }
    public async Task DisposeAsync(){await db.DisposeAsync();await connection.DisposeAsync();}
    private BookingRequest Assessment(string charge="Free") => new(){IdempotencyKey=Guid.NewGuid().ToString(),PatientId=patientId,ClinicianId="senior",RoomId=1,StartsAt=[start],AssessmentCharge=charge};
    private async Task<int> PlanAsync(int count=6, bool insurance=false)
    {
        if(insurance){var p=await db.Patients.FindAsync(patientId);p!.PaymentType="Insurance";
            var company=new InsuranceCompany{Name="Test insurer"};db.InsuranceCompanies.Add(company);await db.SaveChangesAsync();
            p.InsuranceCompanyId=company.Id;p.InsuranceDetails=new(){Status=InsuranceStatus.Approved,ApprovedSessions=4,CopayPercentage=20};}
        var assessment=new Assessment{PatientId=patientId,SeniorId="senior",Diagnosis="Diagnosis",ConditionType="Physio",Priority="Routine",CompletedAt=DateTime.UtcNow};
        var plan=new TreatmentPlan{Assessment=assessment,DoctorId="doctor",NumberOfSessions=count,FrequencyPerWeek=2,TreatmentType="Physio",Status=StatusPlan.Active};
        db.TreatmentPlans.Add(plan);await db.SaveChangesAsync();return plan.Id;
    }
    private BookingRequest Treatment(int plan, int count=2)=>new(){IdempotencyKey=Guid.NewGuid().ToString(),PatientId=patientId,ClinicianId="doctor",RoomId=1,TreatmentPlanId=plan,
        StartsAt=Enumerable.Range(0,count).Select(i=>start.AddDays((i/2)*7+i%2)).ToList(),UnitPrice=100};

    [Fact] public async Task Context_requires_explicit_completion_not_severity_or_empty_cache()
    {
        db.Assessments.Add(new(){PatientId=patientId,SeniorId="senior",Diagnosis="Draft",ConditionType="Physio",Priority="High",Status=Status.High});await db.SaveChangesAsync();
        Assert.False((await service.ContextAsync(patientId)).HasCompletedAssessment);
        var clinicians=await service.CliniciansAsync(patientId,null);
        Assert.Single(clinicians);Assert.Equal("senior",clinicians[0].Id);
    }
    [Fact] public async Task Cash_free_assessment_creates_waiver_and_no_payment()
    {
        var result=await service.CreateAsync(Assessment());
        Assert.Equal(0,result.TotalAmount);Assert.Equal("FreeAssessment",result.WaiverReason);
        Assert.Equal(InvoiceStatus.Waived,(await db.Invoices.SingleAsync()).Status);
        Assert.Empty(await db.Payments.ToListAsync());
        var appointment=await db.Appointments.SingleAsync();Assert.Equal(TimeSpan.FromHours(1),appointment.EndTime-appointment.StartTime);
        Assert.Null(appointment.ActualCheckOut);
    }
    [Fact] public async Task Paid_assessment_requires_explicit_price_and_booking_is_not_payment()
    {
        var request=Assessment("Paid");Assert.Contains("PriceRequired",(await service.PreviewAsync(request)).Errors);
        request.UnitPrice=150;var result=await service.CreateAsync(request);
        Assert.Equal(150,result.PatientAmount);Assert.Equal(0,result.PaidAmount);Assert.Empty(await db.Payments.ToListAsync());
        Assert.Equal(InvoiceStatus.Pending,(await db.Invoices.SingleAsync()).Status);
    }
    [Fact] public async Task Pending_insurance_assessment_is_waived_with_no_liability()
    {
        var patient=await db.Patients.FindAsync(patientId);patient!.PaymentType="Insurance";patient.InsuranceDetails=new(){Status=InsuranceStatus.Pending};await db.SaveChangesAsync();
        var receipt=await service.CreateAsync(Assessment());
        Assert.Equal("InsuranceAssessmentTemporaryPolicy",receipt.WaiverReason);
        Assert.Equal(0,receipt.PatientAmount);Assert.Equal(0,receipt.InsuranceAmount);Assert.Empty(await db.Payments.ToListAsync());
    }
    [Fact] public async Task Cash_paid_assessment_cannot_be_booked_with_a_Doctor_or_wrong_gender_Senior()
    {
        foreach(var id in new[]{"doctor","female"}) {var request=Assessment();request.ClinicianId=id;Assert.Contains("ClinicianEligibility",(await service.PreviewAsync(request)).Errors);}
    }
    [Fact] public async Task Bulk_package_allocates_discount_deposit_and_installments_without_marking_all_sessions_paid()
    {
        var request=Treatment(await PlanAsync(),3);request.PricingMode="Package";request.UnitPrice=150;request.DiscountType="Fixed";request.DiscountValue=100;request.Deposit=100;request.PaymentMethod=PaymentMethod.Cash;
        request.Installments=[new(new(2030,3,1),700)];
        var receipt=await service.CreateAsync(request);
        Assert.Equal(800,receipt.TotalAmount);Assert.Equal(100,receipt.PaidAmount);Assert.Equal(700,receipt.RemainingAmount);
        Assert.Equal(6,await db.Invoices.CountAsync());Assert.Equal(800,await db.Invoices.SumAsync(i=>i.TotalAmount));
        Assert.Equal(100,await db.Payments.SumAsync(p=>p.Amount));
        Assert.All(await db.TreatmentPlanSessions.ToListAsync(),s=>Assert.Equal(PaymentStatus.Pending,s.PaymentStatus));
        var context=await service.ContextAsync(patientId);Assert.Equal(6,context.Plans[0].PlannedSessions);Assert.Equal(3,context.Plans[0].BookedSessions);Assert.Equal(0,context.Plans[0].CompletedSessions);
    }
    [Fact] public async Task Retry_replays_committed_records_and_rejects_changed_intent()
    {
        var request=Treatment(await PlanAsync());var first=await service.CreateAsync(request);var retry=await service.CreateAsync(request);
        Assert.Equal(first.Appointments.Select(a=>a.Id),retry.Appointments.Select(a=>a.Id));Assert.Equal(2,await db.Invoices.CountAsync());
        request.UnitPrice=200;var ex=await Assert.ThrowsAsync<BookingRuleException>(()=>service.CreateAsync(request));Assert.Equal("Booking.RetryKeyConflict",ex.Code);
        Assert.Equal(2,(await service.ReceiptAsync(request.IdempotencyKey)).Appointments.Count);
    }
    [Fact] public async Task Conflicts_reject_the_whole_batch_without_partial_booking_or_charge()
    {
        var first=Assessment();await service.CreateAsync(first);
        var second=Assessment();var ex=await Assert.ThrowsAsync<BookingRuleException>(()=>service.CreateAsync(second));
        Assert.Contains("Conflict",ex.Message);Assert.Equal(1,await db.Appointments.CountAsync());Assert.Equal(1,await db.Invoices.CountAsync());Assert.Equal(1,await db.BookingBatches.CountAsync());
    }
    [Fact] public async Task Insurance_treatment_enforces_approval_and_remaining_limits_and_uses_percentage_as_percentage()
    {
        var plan=await PlanAsync(6,true);var request=Treatment(plan,2);var receipt=await service.CreateAsync(request);
        Assert.Equal(80,receipt.PatientAmount);Assert.Equal(320,receipt.InsuranceAmount);Assert.Equal(0,receipt.PaidAmount);
        Assert.Equal(2,(await service.ContextAsync(patientId)).RemainingApprovedSessions);
        var tooMany=Treatment(plan,3);tooMany.StartsAt=tooMany.StartsAt.Select(d=>d.AddDays(10)).ToList();Assert.Contains("InsuranceLimit",(await service.PreviewAsync(tooMany)).Errors);
        var p=await db.Patients.FindAsync(patientId);p!.InsuranceDetails!.Status=InsuranceStatus.Pending;await db.SaveChangesAsync();
        Assert.Contains("InsuranceApprovalRequired",(await service.PreviewAsync(tooMany)).Errors);
    }
    [Fact] public async Task Rescheduling_preserves_appointment_invoice_payment_and_package_balances()
    {
        var request=Treatment(await PlanAsync(),1);request.Deposit=50;request.PaymentMethod=PaymentMethod.Cash;
        var receipt=await service.CreateAsync(request);var id=receipt.Appointments[0].Id;
        await service.RescheduleAsync(id,new(){StartTime=start.AddDays(5),ClinicianId="doctor",RoomId=1});
        await service.RescheduleAsync(id,new(){StartTime=start.AddDays(5),ClinicianId="doctor",RoomId=1});
        Assert.Equal(1,await db.Appointments.CountAsync());Assert.Equal(1,await db.Invoices.CountAsync());Assert.Equal(1,await db.Payments.CountAsync());
        Assert.Equal(50,(await service.ReceiptAsync(request.IdempotencyKey)).RemainingAmount);
    }
    [Fact] public async Task Actual_check_in_and_out_are_explicit_and_completion_is_counted_once()
    {
        var request=Treatment(await PlanAsync(),1);request.Deposit=100;request.PaymentMethod=PaymentMethod.Cash;
        var receipt=await service.CreateAsync(request);var id=receipt.Appointments[0].Id;
        await service.TransitionAsync(id,AppointmentStatus.InProgress);
        var appointment=await db.Appointments.SingleAsync();var entered=appointment.ActualCheckIn;
        await service.TransitionAsync(id,AppointmentStatus.InProgress);Assert.Equal(entered,appointment.ActualCheckIn);Assert.Null(appointment.ActualCheckOut);
        await FinishClinicalForTest(id);await service.TransitionAsync(id,AppointmentStatus.Completed);await FinishClinicalForTest(id);await service.TransitionAsync(id,AppointmentStatus.Completed);
        Assert.NotNull(appointment.ActualCheckOut);Assert.Equal(1,(await db.TreatmentPlans.SingleAsync()).CompletedSessions);
    }
    [Fact] public async Task Completed_assessment_and_plan_are_saved_atomically_by_assigned_Senior()
    {
        var receipt=await service.CreateAsync(Assessment());var id=receipt.Appointments[0].Id;
        await service.TransitionAsync(id,AppointmentStatus.InProgress);
        var request=new CompleteAssessmentRequest{AppointmentId=id,Diagnosis="Diagnosis",DoctorId="doctor",NumberOfSessions=5,FrequencyPerWeek=2};
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.CompleteAssessmentAsync(request,"other",false));
        var plan=await service.CompleteAssessmentAsync(request,"senior",false);var replay=await service.CompleteAssessmentAsync(request,"senior",false);
        Assert.Equal(plan.Id,replay.Id);Assert.True((await service.ContextAsync(patientId)).HasCompletedAssessment);Assert.Equal(5,await db.TreatmentPlanSessions.CountAsync());
    }
    [Fact] public async Task Installment_collection_is_atomic_idempotent_and_updates_actual_package_coverage()
    {
        var request=Treatment(await PlanAsync(),2);request.Installments=[new(new(2030,3,1),200)];
        var receipt=await service.CreateAsync(request);
        var collect=new CollectBookingPaymentRequest(Guid.NewGuid().ToString(),150,PaymentMethod.Cash);
        var first=await service.CollectAsync(receipt.IdempotencyKey,collect);var retry=await service.CollectAsync(receipt.IdempotencyKey,collect);
        Assert.Equal(150,first.PaidAmount);Assert.Equal(50,retry.RemainingAmount);
        Assert.Equal(150,await db.Payments.SumAsync(p=>p.Amount));Assert.Equal(2,await db.Payments.CountAsync());
        Assert.Equal(1,await db.TreatmentPlanSessions.CountAsync(s=>s.PaymentStatus==PaymentStatus.Paid));
        Assert.Equal(50,(await service.ReconcileCollectionAsync(receipt.IdempotencyKey,collect.IdempotencyKey)).RemainingAmount);
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.CollectAsync(receipt.IdempotencyKey,collect with {Amount=151}));
        Assert.Equal(50,(await service.AccountsAsync()).Single().RemainingAmount);
    }
    [Fact] public async Task Copay_collection_does_not_mark_unsettled_insurer_liability_paid()
    {
        var receipt=await service.CreateAsync(Treatment(await PlanAsync(2,true),1));
        var collected=await service.CollectAsync(receipt.IdempotencyKey,new(Guid.NewGuid().ToString(),20,PaymentMethod.Cash));
        Assert.Equal(20,collected.RemainingAmount);Assert.Equal(160,collected.InsuranceAmount);
        Assert.All(await db.Invoices.ToListAsync(), i => Assert.Equal(InvoiceStatus.Pending,i.Status));
        Assert.Equal(PaymentStatus.Pending,(await db.TreatmentPlanSessions.FirstAsync()).PaymentStatus);
    }
    [Fact] public async Task Actual_payment_method_is_required_and_invoice_payment_retry_cannot_duplicate_collection()
    {
        var receipt=await service.CreateAsync(Treatment(await PlanAsync(),1));
        var billing=new PTCenter.Application.Services.BillingService(db);
        var request=new PTCenter.Application.Contracts.Billing.CreatePaymentRequest{InvoiceId=receipt.InvoiceIds[0],Amount=25,IdempotencyKey=Guid.NewGuid().ToString()};
        Assert.True((await billing.AddPaymentAsync(request)).IsFailure);Assert.Empty(await db.Payments.ToListAsync());
        request.Method=PaymentMethod.Cash;var first=await billing.AddPaymentAsync(request);var retry=await billing.AddPaymentAsync(request);
        Assert.True(first.IsSuccess);Assert.Equal(first.Value.Id,retry.Value.Id);Assert.Equal(1,await db.Payments.CountAsync());
        Assert.Equal(75,(await billing.GetInvoiceByIdAsync(request.InvoiceId)).Value.RemainingAmount);
        Assert.True((await billing.UpdateInvoiceStatusAsync(request.InvoiceId,new(){Status=InvoiceStatus.Paid})).IsFailure);
    }
    [Fact] public async Task Saved_availability_and_absences_are_enforced_before_any_records_are_created()
    {
        var request=Assessment();request.StartsAt=[new DateTimeOffset(2030,2,4,2,0,0,TimeSpan.Zero)];
        Assert.Contains("OutsideWorkingHours",(await service.PreviewAsync(request)).Appointments[0].Conflicts);
        db.DoctorAbsences.Add(new(){DoctorId="senior",AbsentDate=new(2030,2,4)});await db.SaveChangesAsync();
        request.StartsAt=[start];Assert.Contains("ClinicianAbsent",(await service.PreviewAsync(request)).Appointments[0].Conflicts);
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.CreateAsync(request));Assert.Empty(await db.BookingBatches.ToListAsync());
    }
    [Fact] public async Task Invalid_financial_agreements_never_create_bookings_or_payments()
    {
        var id=await PlanAsync();
        var invalid= new List<BookingRequest>();
        var discount=Treatment(id);discount.Discount=201;invalid.Add(discount);
        var negativeDiscount=Treatment(id);negativeDiscount.Discount=-1;invalid.Add(negativeDiscount);
        var deposit=Treatment(id);deposit.Deposit=201;deposit.PaymentMethod=PaymentMethod.Cash;invalid.Add(deposit);
        var noMethod=Treatment(id);noMethod.Deposit=1;invalid.Add(noMethod);
        var installments=Treatment(id);installments.Installments=[new(new(2030,3,1),199)];invalid.Add(installments);
        var cents=Treatment(id);cents.UnitPrice=100.001m;invalid.Add(cents);
        foreach(var request in invalid) await Assert.ThrowsAsync<BookingRuleException>(()=>service.CreateAsync(request));
        Assert.Empty(await db.Appointments.ToListAsync());Assert.Empty(await db.Invoices.ToListAsync());Assert.Empty(await db.Payments.ToListAsync());
    }
    [Fact] public async Task A_legacy_session_update_cannot_claim_payment_or_actual_completion()
    {
        var receipt=await service.CreateAsync(Treatment(await PlanAsync(),1));
        var session=await db.TreatmentPlanSessions.SingleAsync(s=>s.Id==receipt.Appointments[0].TreatmentPlanSessionId);
        var legacy=new TreatmentPlanSessionService(db);
        var request=new PTCenter.Application.Contracts.TreatmentPlanSession.UpdateTreatmentPlanSessionRequest{SessionNumber=session.SessionNumber,SessionDate=session.SessionDate,Status=SessionStatus.Pending,PaymentStatus=PaymentStatus.PackageCovered};
        Assert.True((await legacy.UpdateAsync(session.Id,request)).IsFailure);
        request.PaymentStatus=PaymentStatus.Pending;request.Status=SessionStatus.Completed;
        Assert.True((await legacy.UpdateAsync(session.Id,request)).IsFailure);Assert.Equal(0,(await db.TreatmentPlans.SingleAsync()).CompletedSessions);
    }
    [Fact] public async Task An_overdue_appointment_remains_occupied_until_explicit_check_out()
    {
        var request=Treatment(await PlanAsync(),2);request.Deposit=200;request.PaymentMethod=PaymentMethod.Cash;
        var receipt=await service.CreateAsync(request);
        await service.TransitionAsync(receipt.Appointments[0].Id,AppointmentStatus.InProgress);
        var current=await db.Appointments.FindAsync(receipt.Appointments[0].Id);current!.ActualCheckIn=DateTime.UtcNow.AddHours(-2);current.StartTime=DateTime.UtcNow.AddHours(-3);current.EndTime=DateTime.UtcNow.AddHours(-2);await db.SaveChangesAsync();
        var error=await Assert.ThrowsAsync<BookingRuleException>(()=>service.TransitionAsync(receipt.Appointments[1].Id,AppointmentStatus.InProgress));
        Assert.Equal("Booking.ActualOccupancy",error.Code);Assert.Null(current.ActualCheckOut);
        await FinishClinicalForTest(current.Id);await service.TransitionAsync(current.Id,AppointmentStatus.Completed);
        await service.TransitionAsync(receipt.Appointments[1].Id,AppointmentStatus.InProgress);
    }
    [Fact] public async Task A_new_assessment_requires_rescheduling_the_existing_active_appointment()
    {
        await service.CreateAsync(Assessment());var again=Assessment();again.StartsAt=[start.AddDays(1)];
        Assert.Contains("AssessmentAlreadyBooked",(await service.PreviewAsync(again)).Errors);
    }
    [Fact] public async Task Legacy_completed_sessions_consume_plan_capacity_without_inventing_appointments()
    {
        var plan=await PlanAsync(2);db.TreatmentPlanSessions.Add(new(){TreatmentPlanId=plan,SessionNumber=1,Status=SessionStatus.Completed});await db.SaveChangesAsync();
        var context=await service.ContextAsync(patientId);Assert.Equal(1,context.Plans[0].CompletedSessions);Assert.Equal(0,context.Plans[0].BookedSessions);Assert.Equal(1,context.Plans[0].RemainingSessions);
        Assert.Contains("PlanLimit",(await service.PreviewAsync(Treatment(plan,2))).Errors);
    }
    [Fact] public async Task Unknown_funding_is_not_silently_treated_as_cash()
    {
        var patient=await db.Patients.FindAsync(patientId);patient!.PaymentType=null;await db.SaveChangesAsync();
        Assert.Contains("FundingTypeRequired",(await service.PreviewAsync(Assessment())).Errors);
    }
    [Fact] public async Task Room_availability_excludes_reservations_for_any_selected_slot_and_cancellation_releases_them()
    {
        var rooms = new RoomService(db);
        var receipt = await service.CreateAsync(Assessment());
        Assert.Empty((await rooms.GetAvailableAsync(new() { StartsAt = [start] })).Value);
        Assert.Single((await rooms.GetAvailableAsync(new() { StartsAt = [start.AddHours(1)] })).Value);
        Assert.Empty((await rooms.GetAvailableAsync(new() { StartsAt = [start.AddDays(1), start] })).Value);
        await service.TransitionAsync(receipt.Appointments[0].Id, AppointmentStatus.Cancelled);
        Assert.Single((await rooms.GetAvailableAsync(new() { StartsAt = [start] })).Value);
    }

    [Fact] public async Task Live_occupied_room_does_not_reserve_future_planned_slots_indefinitely()
    {
        var rooms = new RoomService(db);
        var receipt = await service.CreateAsync(Assessment());
        await service.TransitionAsync(receipt.Appointments[0].Id, AppointmentStatus.InProgress);
        var appointment = await db.Appointments.FindAsync(receipt.Appointments[0].Id);
        appointment!.StartTime = DateTime.UtcNow.AddHours(-3); appointment.EndTime = DateTime.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();
        Assert.Equal(PTCenter.Application.Contracts.Rooms.StatusRoom.Occupied, (await rooms.GetByIdAsync(1)).Value.Status);
        Assert.Single((await rooms.GetAvailableAsync(new() { StartsAt = [start] })).Value);
        Assert.DoesNotContain("RoomConflict", (await service.PreviewAsync(Assessment())).Appointments[0].Conflicts);
        await service.TransitionAsync(appointment.Id, AppointmentStatus.Completed);
        Assert.Equal(PTCenter.Application.Contracts.Rooms.StatusRoom.Available, (await rooms.GetByIdAsync(1)).Value.Status);
        Assert.Single((await rooms.GetAvailableAsync(new() { StartsAt = [start] })).Value);
    }

    [Fact] public async Task Early_checkout_releases_the_original_reserved_interval_without_reactivating_maintenance_rooms()
    {
        var rooms = new RoomService(db);
        var receipt = await service.CreateAsync(Assessment());
        await service.TransitionAsync(receipt.Appointments[0].Id, AppointmentStatus.InProgress);
        await service.TransitionAsync(receipt.Appointments[0].Id, AppointmentStatus.Completed);
        Assert.Single((await rooms.GetAvailableAsync(new() { StartsAt = [start] })).Value);
        Assert.True((await service.PreviewAsync(Assessment())).CanSave);
        var room = await db.Rooms.FindAsync(1); room!.IsActive = false; await db.SaveChangesAsync();
        Assert.Empty((await rooms.GetAvailableAsync(new() { StartsAt = [start] })).Value);
        Assert.Equal(PTCenter.Application.Contracts.Rooms.StatusRoom.Maintenance, (await rooms.GetByIdAsync(1)).Value.Status);
    }
    [Fact] public async Task Saving_explicit_Cairo_working_hours_unblocks_a_missing_schedule_and_allows_booking()
    {
        db.DoctorSchedules.RemoveRange(await db.DoctorSchedules.Where(s => s.DoctorId == "senior").ToListAsync());
        await db.SaveChangesAsync();
        var request = Assessment(); // Monday 10:00 UTC = 12:00 Cairo in February.
        Assert.Contains("WorkingHoursNotConfigured", (await service.PreviewAsync(request)).Appointments[0].Conflicts);
        db.DoctorSchedules.Add(new DoctorSchedule { DoctorId = "senior", DayOfWeek = (int)DayOfWeek.Monday, StartTime = new(12, 0), EndTime = new(13, 0) });
        await db.SaveChangesAsync();
        Assert.True((await service.PreviewAsync(request)).CanSave);
        var receipt = await service.CreateAsync(request);
        Assert.Single(receipt.Appointments);
        Assert.Equal(request.StartsAt[0], receipt.Appointments[0].StartTime);
        request.StartsAt = [start.AddMinutes(30)];
        Assert.Contains("OutsideWorkingHours", (await service.PreviewAsync(request)).Appointments[0].Conflicts);
    }
}
