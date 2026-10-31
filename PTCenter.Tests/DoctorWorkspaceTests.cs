using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Application.Contracts.Doctor;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Services;
using Xunit;

namespace PTCenter.Tests;

public sealed partial class BookingDatabaseTests
{
    private sealed class ClinicalClock(DateTimeOffset instant) : TimeProvider { public override DateTimeOffset GetUtcNow() => instant; }
    private DoctorWorkspaceService Clinical(DateTimeOffset? at = null) => new(db,service,new ClinicalClock(at ?? start.AddMinutes(5)));
    private async Task FinishClinicalForTest(int id)
    {
        var a=await db.Appointments.SingleAsync(a=>a.Id==id);
        if(a.AppointmentType!="Session" || a.TreatmentEndedAt!=null)return;
        var at=a.StartTime.AddMinutes(5);if(a.ActualCheckIn>at)at=a.ActualCheckIn.Value.AddMinutes(1);
        var clinical=Clinical(new DateTimeOffset(DateTime.SpecifyKind(at,DateTimeKind.Utc)));
        var started=await clinical.StartAsync(id,a.DoctorId!,false);
        await clinical.SaveAsync(id,new(started.Notes.Version,"Synthetic financial regression: treatment completed","","",""),a.DoctorId!,false,true);
    }
    private async Task<BookingRequest> OtherTreatment(int room, DateTimeOffset at, decimal deposit = 100)
    {
        var key=Guid.NewGuid().ToString("N");var user=new ApplicationUser{Id=key,UserName=key,FullName="Isolated clinical patient",Gender=Gender.Male};
        var patient=new Patient{ApplicationUser=user,PaymentType="Cash"};db.Patients.Add(patient);
        if(!await db.Rooms.AnyAsync(r=>r.Id==room))db.Rooms.Add(new(){Id=room,Name="Clinical room "+room,Capacity=1,IsActive=true});
        var plan=new TreatmentPlan{Assessment=new(){Patient=patient,SeniorId="senior",Diagnosis="Saved diagnosis",ConditionType="Physio",Priority="Routine",CompletedAt=DateTime.UtcNow},DoctorId="doctor",NumberOfSessions=3,FrequencyPerWeek=2,TreatmentType="Physio",Status=StatusPlan.Active};
        db.TreatmentPlans.Add(plan);await db.SaveChangesAsync();
        return new(){IdempotencyKey=key,PatientId=patient.Id,TreatmentPlanId=plan.Id,ClinicianId="doctor",RoomId=room,StartsAt=[at],UnitPrice=100,Deposit=deposit,PaymentMethod=PaymentMethod.Cash};
    }
    [Fact] public async Task Arrival_treatment_notes_completion_and_departure_are_distinct_and_replay_safe()
    {
        var request=Treatment(await PlanAsync(),1);request.UnitPrice=100;request.Deposit=100;request.PaymentMethod=PaymentMethod.Cash;
        var id=(await service.CreateAsync(request)).Appointments[0].Id;
        await service.TransitionAsync(id,AppointmentStatus.InProgress);
        var arrival=await db.Appointments.Include(a=>a.TreatmentPlanSession).SingleAsync();
        Assert.NotNull(arrival.ActualCheckIn);Assert.Null(arrival.TreatmentStartedAt);Assert.Equal(SessionStatus.Pending,arrival.TreatmentPlanSession!.Status);
        Assert.Equal("Booking.TreatmentNotFinished",(await Assert.ThrowsAsync<BookingRuleException>(()=>service.TransitionAsync(id,AppointmentStatus.Completed))).Code);
        var clinical=Clinical();var started=await clinical.StartAsync(id,"doctor",false);var actualStart=started.Visit.TreatmentStartedAt;
        Assert.Equal("Treating",started.Visit.State);Assert.Equal(actualStart,(await clinical.StartAsync(id,"doctor",false)).Visit.TreatmentStartedAt);
        var draft=new NotesMutation(started.Notes.Version,"Improving","No new symptoms","Tolerated","Saved exercises performed");
        var saved=await clinical.SaveAsync(id,draft,"doctor",false,false);
        Assert.Equal(saved.Notes.Version,(await clinical.SaveAsync(id,draft,"doctor",false,false)).Notes.Version);
        Assert.Equal("Improving",(await Clinical().GetAsync(id,"doctor",false)).Notes.ProgressNotes);
        Assert.Equal("Doctor.ConcurrentEdit",(await Assert.ThrowsAsync<ClinicalRuleException>(()=>clinical.SaveAsync(id,draft with {ProgressNotes="Stale writer"},"doctor",false,false))).Code);
        var final=draft with {ExpectedVersion=saved.Notes.Version};var ended=await clinical.SaveAsync(id,final,"doctor",false,true);
        Assert.Equal("TreatmentFinished",ended.Visit.State);Assert.NotNull(ended.Visit.TreatmentEndedAt);Assert.Null(ended.Visit.DepartedAt);
        Assert.Equal(1,ended.Plan.Completed);Assert.Single(await db.TreatmentSessionRecords.ToListAsync());Assert.NotNull((await db.Invoices.SingleAsync()).DeliveredAt);
        Assert.Equal(ended.Notes.Version,(await clinical.SaveAsync(id,final,"doctor",false,true)).Notes.Version);
        Assert.Equal("Doctor.Finalized",(await Assert.ThrowsAsync<ClinicalRuleException>(()=>clinical.SaveAsync(id,final,"doctor",false,false))).Code);
        await service.TransitionAsync(id,AppointmentStatus.Completed);await service.TransitionAsync(id,AppointmentStatus.Completed);
        Assert.Equal(1,(await db.TreatmentPlans.SingleAsync()).CompletedSessions);Assert.Equal("Departed",(await clinical.GetAsync(id,"doctor",false)).Visit.State);
    }
    [Fact] public async Task Full_interval_sweep_allows_two_distinct_rooms_and_rejects_third_partial_overlap()
    {
        var first=await OtherTreatment(2,start);await service.CreateAsync(first);
        var second=await OtherTreatment(3,start.AddMinutes(30));await service.CreateAsync(second);
        var third=await OtherTreatment(4,start.AddMinutes(45));
        Assert.Contains("DoctorCapacity",(await service.PreviewAsync(third)).Appointments.Single().Conflicts);
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.CreateAsync(third));
        third.StartsAt=[start.AddMinutes(75)];Assert.True((await service.PreviewAsync(third)).CanSave);await service.CreateAsync(third);
        var sameRoom=await OtherTreatment(2,start.AddMinutes(10));Assert.Contains("RoomConflict",(await service.PreviewAsync(sameRoom)).Appointments[0].Conflicts);
    }
    [Fact] public async Task Nonconcurrent_partial_overlaps_do_not_become_a_false_third_patient()
    {
        await service.CreateAsync(await OtherTreatment(2,start));await service.CreateAsync(await OtherTreatment(3,start.AddHours(1)));
        var between=await OtherTreatment(4,start.AddMinutes(30));Assert.True((await service.PreviewAsync(between)).CanSave);
        var booked=await service.CreateAsync(between);var before=await db.Invoices.AsNoTracking().Where(i=>i.AppointmentId==booked.Appointments[0].Id).Select(i=>i.TotalAmount).SingleAsync();
        await service.RescheduleAsync(booked.Appointments[0].Id,new(){StartTime=between.StartsAt[0],ClinicianId="doctor",RoomId=4});
        Assert.Equal(before,await db.Invoices.AsNoTracking().Where(i=>i.AppointmentId==booked.Appointments[0].Id).Select(i=>i.TotalAmount).SingleAsync());
    }
    [Fact] public async Task Bulk_checks_each_interval_and_actual_overdue_treatments_keep_their_capacity()
    {
        await service.CreateAsync(await OtherTreatment(2,start.AddDays(2)));await service.CreateAsync(await OtherTreatment(3,start.AddDays(2)));
        var bulk=Treatment(await PlanAsync(),2);bulk.StartsAt=[start.AddDays(1),start.AddDays(2)];
        var preview=await service.PreviewAsync(bulk);Assert.Empty(preview.Appointments[0].Conflicts);Assert.Contains("DoctorCapacity",preview.Appointments[1].Conflicts);
        var a=(await service.CreateAsync(await OtherTreatment(4,start))).Appointments[0].Id;
        var b=(await service.CreateAsync(await OtherTreatment(5,start.AddHours(1)))).Appointments[0].Id;
        var c=(await service.CreateAsync(await OtherTreatment(6,start.AddHours(4)))).Appointments[0].Id;
        foreach(var id in new[]{a,b,c})await service.TransitionAsync(id,AppointmentStatus.InProgress);
        var clinical=Clinical(start.AddHours(2).AddMinutes(5));await clinical.StartAsync(a,"doctor",false);await clinical.StartAsync(b,"doctor",false);
        var error=await Assert.ThrowsAsync<ClinicalRuleException>(()=>clinical.StartAsync(c,"doctor",false));Assert.Equal("Doctor.CapacityOrEligibility",error.Code);
        Assert.Equal(2,(await clinical.ScheduleAsync(DateOnly.FromDateTime(start.Date),"doctor",false)).ActiveTreatments.Count);
    }
    [Fact] public async Task Patient_shortfall_still_blocks_start_while_insurer_outstanding_does_not()
    {
        var request=Treatment(await PlanAsync(3,true),1);request.UnitPrice=100;request.Deposit=20;request.PaymentMethod=PaymentMethod.Cash;
        var id=(await service.CreateAsync(request)).Appointments[0].Id;await service.TransitionAsync(id,AppointmentStatus.InProgress);
        var payment=await db.Payments.SingleAsync();payment.Status=PaymentStatus.Pending;await db.SaveChangesAsync();
        var blocked=await Assert.ThrowsAsync<ClinicalRuleException>(()=>Clinical().StartAsync(id,"doctor",false));
        Assert.Equal("Booking.PaymentCoverageRequired",blocked.Code);Assert.Equal(20,Assert.IsType<PaymentGate>(blocked.Details).AdditionalPaymentRequired);
        payment.Status=PaymentStatus.Paid;await db.SaveChangesAsync();
        Assert.Equal("Treating",(await Clinical().StartAsync(id,"doctor",false)).Visit.State);
        Assert.False(await db.Payments.AnyAsync(p=>p.PayerType=="Insurer"));
        Assert.Equal("Doctor.AccessDenied",(await Assert.ThrowsAsync<ClinicalRuleException>(()=>Clinical().GetAsync(id,"female",false))).Code);
    }
    [Fact] public async Task Reassessment_is_a_direct_clinical_review_even_with_unpaid_patient_and_unapproved_insurance()
    {
        var planId=await PlanAsync(3,true);var booking=Treatment(planId,1);booking.UnitPrice=100;
        var appointmentId=(await service.CreateAsync(booking)).Appointments[0].Id;
        var patient=await db.Patients.Include(p=>p.InsuranceDetails).SingleAsync();patient.InsuranceDetails!.Status=InsuranceStatus.Pending;await db.SaveChangesAsync();
        var oldPlan=await db.TreatmentPlans.SingleAsync();var originalAssessment=oldPlan.AssessmentId;
        var moneyBefore=await db.Invoices.AsNoTracking().Select(i=>new{i.Id,i.TotalAmount,i.PatientAmount,i.InsuranceAmount,i.PaidAmount}).ToListAsync();
        var serviceReview=new ReassessmentService(db,service,Clinical());
        var body=new RequestReassessment("review-original","ProgressConcern","Review symptoms","Pause");
        var requested=await serviceReview.RequestAsync(appointmentId,body,"doctor",false);
        Assert.Equal(requested.Id,(await serviceReview.RequestAsync(appointmentId,body,"doctor",false)).Id);
        Assert.Equal("Reassessment.ExistingActive",(await Assert.ThrowsAsync<ClinicalRuleException>(()=>serviceReview.RequestAsync(appointmentId,body with {OperationKey="different"},"doctor",false))).Code);
        Assert.Single(await serviceReview.ListAsync("senior",false,true));
        var inReview=await serviceReview.StartAsync(requested.Id,new(requested.Version),"senior",false);Assert.Equal("InReview",inReview.State);
        var complete=new CompleteReassessment(inReview.Version,"Senior reviewed","Revise","Revised diagnosis","doctor",5,2,"Physio","Revised instructions");
        var finished=await serviceReview.CompleteAsync(requested.Id,complete,"senior",false);Assert.Equal("Completed",finished.State);Assert.NotNull(finished.RevisedPlanId);Assert.NotNull(finished.CompletedAssessmentId);Assert.NotNull(finished.ResolvedAt);Assert.Equal("senior",(await db.ReassessmentRequests.SingleAsync()).ResolvedBy);
        Assert.Equal(finished.RevisedPlanId,(await serviceReview.CompleteAsync(requested.Id,complete,"senior",false)).RevisedPlanId);
        Assert.Equal(originalAssessment,oldPlan.AssessmentId);Assert.Equal(3,oldPlan.NumberOfSessions);Assert.True((await Clinical().GetAsync(appointmentId,"doctor",false)).Plan.StaffReviewRequired);
        Assert.Single(await db.Appointments.ToListAsync());Assert.Single(await db.TreatmentCourseAgreements.ToListAsync());Assert.Empty(await db.Payments.ToListAsync());Assert.Empty(await db.InsuranceClaims.ToListAsync());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(moneyBefore),System.Text.Json.JsonSerializer.Serialize(await db.Invoices.AsNoTracking().Select(i=>new{i.Id,i.TotalAmount,i.PatientAmount,i.InsuranceAmount,i.PaidAmount}).ToListAsync()));
        Assert.Equal(5,(await db.TreatmentPlans.SingleAsync(p=>p.Id==finished.RevisedPlanId)).NumberOfSessions);
        Assert.All((await service.ContextAsync(patientId)).Plans,p=>Assert.True(p.StaffReviewRequired));
    }
    [Fact] public async Task Review_cancellation_requires_reason_and_does_not_change_ordinary_treatment()
    {
        var request=Treatment(await PlanAsync(),1);request.UnitPrice=100;var id=(await service.CreateAsync(request)).Appointments[0].Id;
        var reviews=new ReassessmentService(db,service,Clinical());var r=await reviews.RequestAsync(id,new("cancel-review","PlanReview","Please review","Continue"),"doctor",false);
        await Assert.ThrowsAsync<ClinicalRuleException>(()=>reviews.CancelAsync(r.Id,new(r.Version,""),"doctor"));
        var result=await reviews.CancelAsync(r.Id,new(r.Version,"Duplicate clinical concern resolved"),"doctor");Assert.Equal("Cancelled",result.State);
        Assert.Equal(AppointmentStatus.Pending,(await db.Appointments.SingleAsync()).Status);Assert.Null((await db.ReassessmentRequests.SingleAsync()).ActivePlanId);
    }
    [Fact] public async Task Historical_completed_status_does_not_invent_treatment_or_departure_timestamps()
    {
        var request=Treatment(await PlanAsync(),1);request.UnitPrice=100;var id=(await service.CreateAsync(request)).Appointments[0].Id;
        var a=await db.Appointments.SingleAsync();a.Status=AppointmentStatus.Completed;a.TreatmentPlanSession!.Status=SessionStatus.Completed;a.TreatmentPlanSession.Notes="Recorded historical clinical note";await db.SaveChangesAsync();
        var record=await Clinical().GetAsync(id,"doctor",false);Assert.Equal("LegacyCompleted",record.Visit.State);
        Assert.Null(record.Visit.TreatmentStartedAt);Assert.Null(record.Visit.TreatmentEndedAt);Assert.Null(record.Visit.DepartedAt);Assert.Equal("Recorded historical clinical note",record.Notes.ProgressNotes);Assert.Null(record.Notes.AuthorName);Assert.Null(record.Notes.SavedAt);
        a.Status=AppointmentStatus.Pending;await db.SaveChangesAsync();Assert.Equal("Booking.ClinicalStateReviewRequired",(await Assert.ThrowsAsync<BookingRuleException>(()=>service.TransitionAsync(id,AppointmentStatus.InProgress))).Code);Assert.Equal(SessionStatus.Completed,a.TreatmentPlanSession.Status);
    }

}
