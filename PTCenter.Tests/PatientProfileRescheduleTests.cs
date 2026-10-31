using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;
using PTCenter.Infrastructure.Services;
using Xunit;

namespace PTCenter.Tests;

public sealed partial class BookingDatabaseTests
{
    private DateTimeOffset Upcoming => new(DateTime.UtcNow.Date.AddDays(10).AddHours(10),TimeSpan.Zero);
    private DateOnly CairoDate(DateTime value)=>DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value,DateTimeKind.Utc),TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo")));
    private async Task<(BookingReceipt Receipt,int Plan)> ProfileBooking(bool insurance=false)
    {
        var plan=await PlanAsync(3,insurance);var request=Treatment(plan,3);request.StartsAt=Enumerable.Range(0,3).Select(i=>Upcoming.AddDays(i)).ToList();
        (await db.TreatmentPlans.FindAsync(plan))!.FrequencyPerWeek=3;await db.SaveChangesAsync();
        request.PricingMode="Package";request.Deposit=insurance?60:100;request.PaymentMethod=PaymentMethod.Cash;
        return(await service.CreateAsync(request),plan);
    }
    private async Task<(RescheduleAvailability Availability,RescheduleSlot Slot)> SlotFor(int appointment,DateTimeOffset start)
    {
        var availability=await service.RescheduleAvailabilityAsync(appointment,CairoDate(start.UtcDateTime));
        return(availability,availability.Slots.First(s=>s.StartTime==start.UtcDateTime));
    }
    [Theory][InlineData(0)][InlineData(5)]
    public async Task Inline_reschedule_preserves_course_other_sessions_finances_and_audits_once(int dayOffset)
    {
        var (receipt,plan)=await ProfileBooking(true);var id=receipt.Appointments[0].Id;
        var before=await service.ProfileAsync(patientId);var original=before.Courses.Single();
        var invoicesBefore=JsonSerializer.Serialize(await db.Invoices.AsNoTracking().Include(i=>i.Payments).Select(i=>new{i.Id,i.AppointmentId,i.TotalAmount,i.PatientAmount,i.InsuranceAmount,i.CourseAgreementId,i.TreatmentPlanSessionId,Payments=i.Payments.Select(p=>new{p.Id,p.Amount,p.PaymentMethod}).ToList()}).ToListAsync());
        var otherTimes=await db.Appointments.AsNoTracking().Where(a=>a.Id!=id).Select(a=>new{a.Id,a.StartTime,a.EndTime,a.RoomId,a.Status}).ToListAsync();
        var (availability,slot)=await SlotFor(id,Upcoming.AddDays(dayOffset).AddHours(2));
        var request=new RescheduleSessionRequest{OperationKey=Guid.NewGuid().ToString(),ExpectedVersion=availability.Version,StartTime=slot.StartTime,RoomId=slot.RoomId,Reason="Patient requested a one-off change"};
        var saved=await service.RescheduleSessionAsync(id,request,"doctor");await service.RescheduleSessionAsync(id,request,"doctor");
        db.ChangeTracker.Clear();
        var changed=await db.Appointments.Include(a=>a.TreatmentPlanSession).SingleAsync(a=>a.Id==id);
        Assert.Equal(saved.StartTime,changed.StartTime);Assert.Equal(changed.StartTime.AddHours(1),changed.EndTime);Assert.Equal(changed.StartTime,changed.TreatmentPlanSession!.SessionDate);
        Assert.Equal(AppointmentStatus.Pending,changed.Status);Assert.Null(changed.ActualCheckIn);Assert.Equal("doctor",changed.DoctorId);
        Assert.Equal(otherTimes,await db.Appointments.AsNoTracking().Where(a=>a.Id!=id).Select(a=>new{a.Id,a.StartTime,a.EndTime,a.RoomId,a.Status}).ToListAsync());
        Assert.Equal(invoicesBefore,JsonSerializer.Serialize(await db.Invoices.AsNoTracking().Include(i=>i.Payments).Select(i=>new{i.Id,i.AppointmentId,i.TotalAmount,i.PatientAmount,i.InsuranceAmount,i.CourseAgreementId,i.TreatmentPlanSessionId,Payments=i.Payments.Select(p=>new{p.Id,p.Amount,p.PaymentMethod}).ToList()}).ToListAsync()));
        Assert.Equal(3,await db.Appointments.CountAsync());Assert.Equal(3,await db.TreatmentPlanSessions.CountAsync());Assert.Single(await db.AppointmentScheduleChanges.ToListAsync());
        var profile=(await service.ProfileAsync(patientId)).Courses.Single();Assert.Equal(original.PatientAmount,profile.PatientAmount);Assert.Equal(original.PatientPaid,profile.PatientPaid);Assert.Equal(original.InsurerBalance,profile.InsurerBalance);
        Assert.Equal(1,(await service.ContextAsync(patientId)).RemainingApprovedSessions);Assert.Equal(3,profile.PrescribedSessions);
        Assert.Equal("Doctor",profile.Activity.Single().ActorName);Assert.Equal("Patient requested a one-off change",profile.Activity.Single().Reason);
        Assert.Equal(saved,await service.RescheduleReceiptAsync(id,request.OperationKey,"doctor"));
    }
    [Fact] public async Task Slot_list_excludes_itself_and_uses_Cairo_day_and_UTC_with_saved_hours()
    {
        var (receipt,_)=await ProfileBooking();var id=receipt.Appointments[0].Id;
        var available=await service.RescheduleAvailabilityAsync(id,CairoDate(Upcoming.UtcDateTime));
        Assert.Contains(available.Slots,s=>s.StartTime==Upcoming.UtcDateTime&&s.RoomId==1);Assert.All(available.Slots,s=>Assert.Equal(TimeSpan.FromHours(1),s.EndTime-s.StartTime));
        Assert.All(available.Slots,s=>Assert.Equal(CairoDate(Upcoming.UtcDateTime),CairoDate(s.StartTime)));
        foreach(var schedule in await db.DoctorSchedules.Where(s=>s.DoctorId=="doctor").ToListAsync())db.DoctorSchedules.Remove(schedule);
        await db.SaveChangesAsync();var noHours=await service.RescheduleAvailabilityAsync(id,CairoDate(Upcoming.UtcDateTime));
        Assert.Empty(noHours.Slots);Assert.Contains("WorkingHoursNotConfigured",noHours.Notices);
    }
    [Theory][InlineData("patient")][InlineData("doctor")][InlineData("room")][InlineData("maintenance")][InlineData("absence")][InlineData("zeroCapacity")]
    public async Task Availability_and_atomic_save_reject_real_conflicts_and_preserve_draft_target(string conflict)
    {
        var (receipt,_)=await ProfileBooking();var id=receipt.Appointments[0].Id;var desired=Upcoming.AddDays(5);
        var (available,slot)=await SlotFor(id,desired);
        if(conflict=="maintenance"){(await db.Rooms.FindAsync(1))!.IsActive=false;}
        else if(conflict=="zeroCapacity"){(await db.Rooms.FindAsync(1))!.Capacity=0;}
        else if(conflict=="absence")db.DoctorAbsences.Add(new(){DoctorId="doctor",AbsentDate=CairoDate(desired.UtcDateTime)});
        else {
            var other=new Patient{ApplicationUserId="female",PaymentType="Cash"};db.Patients.Add(other);await db.SaveChangesAsync();
            db.Rooms.Add(new(){Id=2,Name="Other",Capacity=2});await db.SaveChangesAsync();
            db.Appointments.Add(new(){PatientId=conflict=="patient"?patientId:other.Id,DoctorId=conflict=="doctor"?"doctor":"senior",RoomId=conflict=="room"?1:2,StartTime=desired.UtcDateTime,EndTime=desired.UtcDateTime.AddHours(1),Status=AppointmentStatus.Pending,AppointmentType="Session"});
        }
        if(conflict=="doctor") {
            var third=await OtherTreatment(3,desired);
            db.Appointments.Add(new(){PatientId=third.PatientId,DoctorId="doctor",RoomId=3,StartTime=desired.UtcDateTime,EndTime=desired.UtcDateTime.AddHours(1),Status=AppointmentStatus.Pending,AppointmentType="Session"});
        }
        await db.SaveChangesAsync();
        Assert.DoesNotContain((await service.RescheduleAvailabilityAsync(id,CairoDate(desired.UtcDateTime))).Slots,s=>s.StartTime==slot.StartTime&&s.RoomId==1);
        var error=await Assert.ThrowsAsync<BookingRuleException>(()=>service.RescheduleSessionAsync(id,new(){OperationKey=Guid.NewGuid().ToString(),ExpectedVersion=available.Version,StartTime=desired,RoomId=1},"doctor"));
        Assert.Equal("Booking.SlotUnavailable",error.Code);Assert.Equal(Upcoming.UtcDateTime,(await db.Appointments.FindAsync(id))!.StartTime);Assert.Empty(await db.AppointmentScheduleChanges.ToListAsync());
    }
    [Theory][InlineData(AppointmentStatus.Completed)][InlineData(AppointmentStatus.InProgress)][InlineData(AppointmentStatus.Cancelled)][InlineData(AppointmentStatus.NoShow)]
    public async Task Ineligible_records_are_not_reopened_by_either_reschedule_path(AppointmentStatus status)
    {
        var (receipt,_)=await ProfileBooking();var appointment=await db.Appointments.FindAsync(receipt.Appointments[0].Id);appointment!.Status=status;await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.RescheduleAvailabilityAsync(appointment.Id,CairoDate(Upcoming.UtcDateTime)));
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.RescheduleAsync(appointment.Id,new(){StartTime=Upcoming.AddHours(2),ClinicianId="doctor",RoomId=1}));
        await Assert.ThrowsAsync<BookingRuleException>(()=>service.RescheduleSessionAsync(appointment.Id,new(){OperationKey=Guid.NewGuid().ToString(),ExpectedVersion="old",StartTime=Upcoming.AddHours(2),RoomId=1},"doctor"));
        Assert.Equal(status,appointment.Status);
    }
    [Fact] public async Task Stale_expected_version_rejects_second_receptionist_change_and_changed_retry_intent()
    {
        var (receipt,_)=await ProfileBooking();var id=receipt.Appointments[0].Id;var (available,slot)=await SlotFor(id,Upcoming.AddHours(2));
        var request=new RescheduleSessionRequest{OperationKey=Guid.NewGuid().ToString(),ExpectedVersion=available.Version,StartTime=slot.StartTime,RoomId=slot.RoomId};
        await service.RescheduleSessionAsync(id,request,"doctor");
        request.OperationKey=Guid.NewGuid().ToString();var stale=await Assert.ThrowsAsync<BookingRuleException>(()=>service.RescheduleSessionAsync(id,request,"doctor"));Assert.Equal("Booking.StaleSchedule",stale.Code);
        var audit=await db.AppointmentScheduleChanges.SingleAsync();request.OperationKey=audit.OperationKey;request.StartTime=request.StartTime.AddHours(1);
        Assert.Equal("Booking.RetryKeyConflict",(await Assert.ThrowsAsync<BookingRuleException>(()=>service.RescheduleSessionAsync(id,request,"doctor"))).Code);
    }
    [Fact] public async Task Profile_separates_patient_coverage_from_insurer_settlement_and_multiple_courses()
    {
        var (_,plan)=await ProfileBooking(true);await PlanAsync(2);
        var data=await service.ProfileAsync(patientId);Assert.Equal(2,data.Courses.Count);var course=data.Courses.Single(c=>c.Id==plan);
        Assert.All(course.Sessions,s=>Assert.Equal("Covered",s.PatientCoverage));Assert.Equal(0,course.PatientBalance);Assert.Equal(240,course.InsurerBalance);
        Assert.Equal(0,data.Courses.Single(c=>c.Id!=plan).PatientPaid);Assert.Equal(2,data.Courses.Single(c=>c.Id!=plan).PrescribedSessions);
    }
    [Theory][InlineData(2027,1,15,6)][InlineData(2027,4,30,5)]
    public async Task Available_times_use_saved_Cairo_hours_across_DST_without_converting_invalid_midnight(int year,int month,int day,int utcHour)
    {
        var (receipt,_)=await ProfileBooking();var available=await service.RescheduleAvailabilityAsync(receipt.Appointments[0].Id,new(year,month,day));
        Assert.NotEmpty(available.Slots);Assert.Equal(utcHour,available.Slots[0].StartTime.Hour);Assert.All(available.Slots,s=>Assert.Equal(new DateOnly(year,month,day),CairoDate(s.StartTime)));
    }
    [Fact] public async Task Slot_discovery_does_not_introduce_a_new_booking_horizon_policy()
    {
        var (receipt,_)=await ProfileBooking();var future=await service.RescheduleAvailabilityAsync(receipt.Appointments[0].Id,new(2030,2,4));Assert.NotEmpty(future.Slots);
    }
}

public class ProfileRescheduleHttpTests
{
    [Fact] public async Task Real_reception_login_can_reschedule_and_reconcile_while_nonstaff_and_clinical_only_writes_are_denied()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});await ReceptionAccessFixture.SeedAsync(host);await ReceptionAccessFixture.LoginAsync(client);
        var context=(await client.GetFromJsonAsync<BookingContext>("/api/Bookings/patients/1"))!;
        var start=new DateTimeOffset(DateTime.UtcNow.Date.AddDays(10).AddHours(10),TimeSpan.Zero);
        var booking=await client.PostAsJsonAsync("/api/Bookings",new BookingRequest{IdempotencyKey=Guid.NewGuid().ToString(),PatientId=1,ClinicianId="access-doctor",RoomId=1,TreatmentPlanId=context.Plans[0].Id,StartsAt=[start],UnitPrice=100});
        Assert.Equal(HttpStatusCode.OK,booking.StatusCode);var id=(await booking.Content.ReadFromJsonAsync<BookingReceipt>())!.Appointments[0].Id;
        var slotsUrl=$"/api/Bookings/appointments/{id}/reschedule-slots?date={start:yyyy-MM-dd}";
        var slots=(await client.GetFromJsonAsync<RescheduleAvailability>(slotsUrl))!;var slot=slots.Slots.First(s=>s.StartTime==start.UtcDateTime.AddHours(2));
        var request=new RescheduleSessionRequest{OperationKey=Guid.NewGuid().ToString(),ExpectedVersion=slots.Version,StartTime=slot.StartTime,RoomId=slot.RoomId,Reason="HTTP test"};
        var saves=await Task.WhenAll(client.PutAsJsonAsync($"/api/Bookings/appointments/{id}/reschedule",request),client.PutAsJsonAsync($"/api/Bookings/appointments/{id}/reschedule",request));
        Assert.All(saves,r=>Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict));Assert.Contains(saves,r=>r.StatusCode==HttpStatusCode.OK);
        var reconcile=$"/api/Bookings/appointments/{id}/reschedules/{request.OperationKey}";Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(reconcile)).StatusCode);
        var profile=await client.GetFromJsonAsync<PatientProfileData>("/api/Bookings/patients/1/profile");Assert.Single(profile!.Courses.Single().Activity);
        await ReceptionAccessFixture.LoginAsync(client,"Doctor");Assert.Equal(HttpStatusCode.Forbidden,(await client.PutAsJsonAsync($"/api/Bookings/appointments/{id}/reschedule",request)).StatusCode);
        await ReceptionAccessFixture.LoginAsync(client,"Patient");Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(slotsUrl)).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/Bookings/patients/1/profile")).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await client.PutAsJsonAsync($"/api/Bookings/appointments/{id}/reschedule",request)).StatusCode);
        client.DefaultRequestHeaders.Authorization=null;Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(reconcile)).StatusCode);
    }
}
