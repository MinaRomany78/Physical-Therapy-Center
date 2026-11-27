using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Services;
using PTCenter.Infrastructure.Persistence;
using Xunit;

namespace PTCenter.Tests;

public sealed partial class BookingDatabaseTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")]
    public async Task Reschedule_restoration_requires_a_nonblank_reason_without_any_write(string? reason)
    {
        var (booking, _) = await ProfileBooking(); var id = booking.Appointments[0].Id;
        var (a, slot) = await SlotFor(id, Upcoming.AddHours(2));
        var error = await Assert.ThrowsAsync<BookingRuleException>(() => service.RescheduleSessionAsync(id,
            new() { OperationKey = Guid.NewGuid().ToString(), ExpectedVersion = a.Version, StartTime = slot.StartTime, RoomId = slot.RoomId, Reason = reason }, "doctor"));
        Assert.Equal("Booking.RescheduleRequest", error.Code);
        Assert.Equal(Upcoming.UtcDateTime, (await db.Appointments.FindAsync(id))!.StartTime);
        Assert.Empty(await db.AppointmentScheduleChanges.ToListAsync());
    }

    [Theory]
    [InlineData("arrived")] [InlineData("started")] [InlineData("clinical-session")]
    public async Task Reschedule_restoration_rejects_clinical_events_even_if_status_remains_pending(string eventName)
    {
        var (booking, _) = await ProfileBooking(); var id = booking.Appointments[0].Id;
        var (availability, slot) = await SlotFor(id, Upcoming.AddHours(2));
        var a = await db.Appointments.Include(a => a.TreatmentPlanSession).SingleAsync(a => a.Id == id);
        if (eventName == "arrived") a.ActualCheckIn = DateTime.UtcNow;
        if (eventName == "started") a.TreatmentStartedAt = DateTime.UtcNow;
        if (eventName == "clinical-session") a.TreatmentPlanSession!.Status = SessionStatus.InProgress;
        await db.SaveChangesAsync();
        Assert.Equal("Booking.NotUpcoming", (await Assert.ThrowsAsync<BookingRuleException>(() => service.RescheduleSessionAsync(id,
            new() { OperationKey = Guid.NewGuid().ToString(), ExpectedVersion = availability.Version, StartTime = slot.StartTime, RoomId = slot.RoomId, Reason = "Clinical event guard" }, "doctor"))).Code);
        Assert.Empty(await db.AppointmentScheduleChanges.ToListAsync());
    }

    [Fact]
    public async Task Reschedule_restoration_preserves_nonstandard_duration_and_checks_entire_interval()
    {
        var (booking, _) = await ProfileBooking(); var id = booking.Appointments[0].Id;
        var a = await db.Appointments.FindAsync(id); a!.EndTime = a.StartTime.AddMinutes(90); await db.SaveChangesAsync();
        var (availability, slot) = await SlotFor(id, Upcoming.AddHours(2));
        Assert.Equal(TimeSpan.FromMinutes(90), slot.EndTime - slot.StartTime);
        await service.RescheduleSessionAsync(id, new() { OperationKey = Guid.NewGuid().ToString(), ExpectedVersion = availability.Version, StartTime = slot.StartTime, RoomId = slot.RoomId, Reason = "Retain original duration" }, "doctor");
        Assert.Equal(TimeSpan.FromMinutes(90), a.EndTime - a.StartTime);
    }

    [Fact]
    public async Task Reschedule_restoration_retains_exclusive_Senior_coverage_for_a_dual_role_employee()
    {
        var (_, options) = await MergeRecoveryBooking();
        var candidate = options.Matching.First(c => c.ClinicianId == "senior");
        var id = options.Appointment.AppointmentId;
        await service.ResolveAbsenceAsync(id, new(Guid.NewGuid().ToString(),options.Appointment.Version,options.Appointment.AbsenceIds[0],
            "senior",new DateTimeOffset(candidate.Start,TimeSpan.Zero),candidate.RoomId,"Replace","Retain existing Senior coverage"),"reception");
        db.UserRoles.Add(new() {UserId="senior",RoleId=await db.Roles.Where(r=>r.Name=="Doctor").Select(r=>r.Id).SingleAsync()});await db.SaveChangesAsync();
        var desired=new DateTimeOffset(candidate.Start,TimeSpan.Zero).AddDays(1);
        var other=await OtherTreatment(2,desired);other.ClinicianId="senior";await service.CreateAsync(other);
        var available=await service.RescheduleAvailabilityAsync(id,CairoDate(desired.UtcDateTime));
        Assert.Equal("Senior",available.Clinicians.Single(c=>c.Id=="senior").Role);
        Assert.DoesNotContain(available.Slots,s=>s.StartTime==desired.UtcDateTime);
        var failure=await Assert.ThrowsAsync<BookingRuleException>(()=>service.RescheduleSessionAsync(id,
            new(){OperationKey=Guid.NewGuid().ToString(),ExpectedVersion=available.Version,StartTime=desired,RoomId=candidate.RoomId,Reason="Preserve Senior capacity"},"reception"));
        Assert.Equal("Booking.SlotUnavailable",failure.Code);
    }

    [Fact]
    public async Task Reschedule_restoration_allows_eligible_one_off_clinician_without_reassigning_plan()
    {
        var (booking, planId) = await ProfileBooking(); var id = booking.Appointments[0].Id;
        // The existing fixture's Senior also holds the Doctor role and matches the patient gender.
        var role = await db.Roles.SingleAsync(r => r.Name == "Doctor");
        db.UserRoles.Add(new() { UserId = "senior", RoleId = role.Id }); await db.SaveChangesAsync();
        var available = await service.RescheduleAvailabilityAsync(id, CairoDate(Upcoming.UtcDateTime), clinicianId: "senior");
        var slot = available.Slots.First(s => s.StartTime == Upcoming.AddHours(2).UtcDateTime);
        await service.RescheduleSessionAsync(id, new() { OperationKey = Guid.NewGuid().ToString(), ExpectedVersion = available.Version, StartTime = slot.StartTime, RoomId = slot.RoomId, ClinicianId = "senior", Reason = "One appointment only" }, "doctor");
        Assert.Equal("senior", (await db.Appointments.FindAsync(id))!.DoctorId);
        Assert.Equal("doctor", (await db.TreatmentPlans.FindAsync(planId))!.DoctorId);
        Assert.All(await db.Appointments.Where(a => a.Id != id).ToListAsync(), a => Assert.Equal("doctor", a.DoctorId));
        var audit = await db.AppointmentScheduleChanges.SingleAsync();
        Assert.Equal("doctor", audit.PreviousClinicianId); Assert.Equal("senior", audit.ClinicianId);
        Assert.Equal("doctor", audit.ActorId); Assert.True(audit.ChangedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Reschedule_restoration_preview_and_save_enforce_plan_and_weekly_limit_with_self_exclusion()
    {
        var (booking, planId) = await ProfileBooking(); var id = booking.Appointments[0].Id;
        var a = await db.Appointments.FindAsync(id);
        // Put all three reservations in one Cairo Saturday-Friday week, already at the prescribed limit.
        var saturday = Upcoming.AddDays(((int)DayOfWeek.Saturday - (int)Upcoming.DayOfWeek + 7) % 7);
        var rows = await db.Appointments.OrderBy(x => x.Id).ToListAsync();
        for (var i = 0; i < rows.Count; i++) { rows[i].StartTime = saturday.AddDays(i).UtcDateTime; rows[i].EndTime = rows[i].StartTime.AddHours(1); }
        await db.SaveChangesAsync();
        var selfExcluded = await service.RescheduleAvailabilityAsync(id, CairoDate(a!.StartTime));
        Assert.Contains(selfExcluded.Slots, s => s.StartTime == a.StartTime);
        (await db.TreatmentPlans.FindAsync(planId))!.FrequencyPerWeek = 2; await db.SaveChangesAsync();
        var full = await service.RescheduleAvailabilityAsync(id, CairoDate(a.StartTime));
        Assert.Empty(full.Slots); Assert.Contains("WeeklyFrequencyLimit", full.Notices);
        var failed = await Assert.ThrowsAsync<BookingRuleException>(() => service.RescheduleSessionAsync(id,
            new() { OperationKey = Guid.NewGuid().ToString(), ExpectedVersion = full.Version, StartTime = new DateTimeOffset(a.StartTime.AddHours(2), TimeSpan.Zero), RoomId = 1, Reason = "Weekly conflict" }, "doctor"));
        Assert.Equal("Booking.SlotUnavailable", failed.Code);
        (await db.TreatmentPlans.FindAsync(planId))!.Status = StatusPlan.Inactive; await db.SaveChangesAsync();
        var inactive = await service.RescheduleAvailabilityAsync(id, CairoDate(a.StartTime));
        Assert.Empty(inactive.Slots); Assert.Contains("PlanRequired", inactive.Notices);
    }
}

public class RestoredRescheduleHttpTests
{
    [Fact]
    public async Task Legacy_HTTP_aliases_require_audited_intent_and_use_the_authenticated_employee()
    {
        using var host = new BookingTestHost();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        await ReceptionAccessFixture.SeedAsync(host); await ReceptionAccessFixture.LoginAsync(client);
        var context = (await client.GetFromJsonAsync<BookingContext>("/api/Bookings/patients/1"))!;
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(15).AddHours(10), TimeSpan.Zero);
        var booking = await client.PostAsJsonAsync("/api/Bookings", new BookingRequest { IdempotencyKey = Guid.NewGuid().ToString(), PatientId = 1, TreatmentPlanId = context.Plans[0].Id, ClinicianId = "access-doctor", RoomId = 1, StartsAt = [start], UnitPrice = 100 });
        Assert.Equal(HttpStatusCode.OK, booking.StatusCode);
        var id = (await booking.Content.ReadFromJsonAsync<BookingReceipt>())!.Appointments[0].Id;
        var date = TimeZoneInfo.ConvertTime(start, TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo")).ToString("yyyy-MM-dd");
        var slotsUrl = $"/api/Bookings/appointments/{id}/reschedule-slots?date={date}";
        var a = (await client.GetFromJsonAsync<RescheduleAvailability>(slotsUrl))!;
        var request = new { operationKey = Guid.NewGuid().ToString(), expectedVersion = a.Version, startTime = start.AddHours(2), roomId = 1, clinicianId = "access-doctor", reason = "Audit both HTTP aliases", actorId = "spoofed", changedAt = "2000-01-01T00:00:00Z" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/Bookings/appointments/{id}/schedule", new { startTime = start.AddHours(2), clinicianId = "access-doctor", roomId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/Bookings/appointments/{id}/schedule", request)).StatusCode);
        a = (await client.GetFromJsonAsync<RescheduleAvailability>(slotsUrl))!;
        var updated = await client.PutAsJsonAsync($"/api/Appointments/{id}", new { operationKey = Guid.NewGuid().ToString(), expectedVersion = a.Version, startTime = start.AddHours(4), endTime = start.AddHours(5), roomId = 1, doctorId = "access-doctor", appointmentType = "Session", status = "Pending", reason = "Legacy appointment route, same audit" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var actor = await db.Users.Where(u => u.UserName == ReceptionAccessFixture.UserName).Select(u => u.Id).SingleAsync();
        var changes = await db.AppointmentScheduleChanges.Where(c => c.AppointmentId == id).ToListAsync();
        Assert.Equal(2, changes.Count); Assert.All(changes, c => { Assert.Equal(actor, c.ActorId); Assert.True(c.ChangedAt > DateTime.UtcNow.AddMinutes(-1)); });
        Assert.Equal(1, await db.Appointments.CountAsync(x => x.Id == id));
    }
}
