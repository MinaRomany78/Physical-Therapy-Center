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
    [Fact] public async Task Room_capacity_sweeps_partial_intervals_and_keeps_one_doctors_patients_in_different_rooms()
    {
        var source=new[]{new Appointment{RoomId=1,DoctorId="other",PatientId=20,StartTime=start.UtcDateTime,EndTime=start.AddHours(1).UtcDateTime},
            new Appointment{RoomId=1,DoctorId="another",PatientId=21,StartTime=start.AddHours(1).UtcDateTime,EndTime=start.AddHours(2).UtcDateTime}};
        Assert.Empty(ClinicalCapacity.Conflicts(start.AddMinutes(30).UtcDateTime,start.AddMinutes(90).UtcDateTime,patientId,"doctor",1,source,true,2));
        source[1].StartTime=start.AddMinutes(45).UtcDateTime;
        Assert.Contains("RoomConflict",ClinicalCapacity.Conflicts(start.AddMinutes(30).UtcDateTime,start.AddMinutes(90).UtcDateTime,patientId,"doctor",1,source,true,2));
        source[0].DoctorId="doctor";
        Assert.Contains("DoctorSameRoom",ClinicalCapacity.Conflicts(start.UtcDateTime,start.AddHours(1).UtcDateTime,patientId,"doctor",1,source,true,4));
        Assert.Equal(2,ClinicalCapacity.Peak(start.UtcDateTime,start.AddHours(2).UtcDateTime,source));
    }
    [Fact] public async Task Weekly_frequency_counts_existing_bookings_and_released_cancellations()
    {
        var plan=await PlanAsync(10);var first=await service.CreateAsync(Treatment(plan,2));
        var third=Treatment(plan,1);third.StartsAt=[start.AddDays(2)];
        Assert.Contains("WeeklyFrequencyLimit",(await service.PreviewAsync(third)).Appointments.Single().Conflicts);
        Assert.Equal("Booking.Validation",(await Assert.ThrowsAsync<BookingRuleException>(()=>service.CreateAsync(third))).Code);
        await service.TransitionAsync(first.Appointments[0].Id,AppointmentStatus.Cancelled);
        Assert.True((await service.PreviewAsync(third)).CanSave);
        (await db.TreatmentPlans.FindAsync(plan))!.FrequencyPerWeek=0;await db.SaveChangesAsync();
        Assert.Contains("FrequencyRequired",(await service.PreviewAsync(third)).Errors);
        Assert.Equal(ClinicCalendar.Week(new DateTimeOffset(2030,2,8,21,0,0,TimeSpan.Zero)),ClinicCalendar.Week(new DateTimeOffset(2030,2,4,10,0,0,TimeSpan.Zero)));
    }
    [Fact] public async Task Fully_allocated_directory_excludes_plan_until_cancellation_but_keeps_general_directory_and_other_plans()
    {
        var plan=await PlanAsync(2);var receipt=await service.CreateAsync(Treatment(plan,2));
        Assert.DoesNotContain((await service.DirectoryAsync()).Items,p=>p.Id==patientId);
        Assert.True(await db.Patients.AnyAsync(p=>p.Id==patientId));
        await service.TransitionAsync(receipt.Appointments[0].Id,AppointmentStatus.Cancelled);
        Assert.Contains((await service.DirectoryAsync()).Items,p=>p.Id==patientId);
        await service.CreateAsync(Treatment(plan,1));
        await PlanAsync(2);
        Assert.Contains((await service.DirectoryAsync()).Items,p=>p.Id==patientId);
    }
    [Theory][InlineData(0)][InlineData(20)][InlineData(100)]
    public async Task Insurance_copay_extremes_reconcile_and_partial_receipts_return_exact_patient_shortfall(int copay)
    {
        var plan=await PlanAsync(3,true);var patient=await db.Patients.FindAsync(patientId);patient!.InsuranceDetails!.CopayPercentage=copay;await db.SaveChangesAsync();
        var request=Treatment(plan,1);request.PricingMode="Package";request.UnitPrice=99.99m;request.Deposit=copay==0?0:1;request.PaymentMethod=PaymentMethod.Cash;
        var receipt=await service.CreateAsync(request);var quote=await new CourseFinanceService(db).SummaryAsync(plan);
        Assert.Equal(299.97m,quote!.TotalAmount);Assert.Equal(decimal.Round(299.97m*copay/100,2,MidpointRounding.AwayFromZero),quote.PatientAmount);
        Assert.Equal(quote.TotalAmount,quote.PatientAmount+quote.InsuranceAmount);
        var gate=await new CourseFinanceService(db).GateAsync(receipt.Appointments.Single().Id);
        Assert.Equal(Math.Max(0,quote.NextSessionPatientAmount-request.Deposit),gate.AdditionalPaymentRequired);
        Assert.Equal(copay==0,gate.CanStart);Assert.Empty(await db.InsuranceClaims.ToListAsync());
    }
    [Fact] public async Task Ten_bulk_rows_keep_dates_assignments_sessions_and_financial_links_after_reload()
    {
        var plan=await PlanAsync(10);var receipt=await service.CreateAsync(Treatment(plan,10));db.ChangeTracker.Clear();
        var reload=await service.ReceiptAsync(receipt.IdempotencyKey);Assert.Equal(10,reload.Appointments.Count);
        Assert.Equal(10,reload.Appointments.Select(a=>a.StartTime).Distinct().Count());Assert.Equal(10,reload.Appointments.Select(a=>a.TreatmentPlanSessionId).Distinct().Count());
        foreach(var row in reload.Appointments){Assert.Equal("doctor",row.ClinicianId);Assert.Equal("Doctor",row.ClinicianName);Assert.Equal(1,row.RoomId);Assert.Equal("Room",row.RoomName);Assert.Equal(plan,row.TreatmentPlanId);Assert.NotNull(row.CourseAgreementId);Assert.Single(row.InvoiceIds);Assert.Equal(TimeSpan.FromHours(1),row.EndTime-row.StartTime);}
    }
    [Fact] public async Task Future_planned_capacity_is_separate_from_actual_overdue_start_capacity()
    {
        var now=start.UtcDateTime;var source=new[]{new Appointment{PatientId=20,DoctorId="doctor",RoomId=2,StartTime=now.AddHours(-2),EndTime=now.AddHours(-1),ActualCheckIn=now.AddHours(-2),TreatmentStartedAt=now.AddHours(-2)}};
        Assert.Empty(ClinicalCapacity.Conflicts(now,now.AddHours(1),patientId,"doctor",1,source,true));
        Assert.Empty(ClinicalCapacity.Conflicts(now,now.AddHours(1),patientId,"doctor",1,source,true,1,true));
        var second=new Appointment{PatientId=21,DoctorId="doctor",RoomId=3,StartTime=now.AddHours(-2),EndTime=now.AddHours(-1),ActualCheckIn=now.AddHours(-2),TreatmentStartedAt=now.AddHours(-2)};
        Assert.Contains("DoctorCapacity",ClinicalCapacity.Conflicts(now,now.AddHours(1),patientId,"doctor",1,source.Append(second),true,1,true));
        Assert.Empty(ClinicalCapacity.Conflicts(now,now.AddHours(1),patientId,"doctor",1,source.Append(second),true));
    }
}

public class ReceptionAcceptanceHttpTests
{
    [Fact] public async Task Concurrent_room_reservations_use_saved_capacity_and_report_actual_multiple_occupants()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});await DoctorWorkspaceFixture.SeedAsync(host);
        var third=await DoctorWorkspaceFixture.AddCashPatientAsync(host);
        var patients=new[]{1,2,third};
        using(var scope=host.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();(await db.Rooms.FindAsync(1))!.Capacity=2;
            var doctorRole=await db.Roles.Where(r=>r.Name=="Doctor").Select(r=>r.Id).SingleAsync();
            for(var i=0;i<3;i++){
                var id="room-capacity-doctor-"+i;db.Users.Add(new(){Id=id,UserName=id,FullName="Room Doctor "+i,Gender=Gender.Male});
                db.UserRoles.Add(new(){UserId=id,RoleId=doctorRole});
                for(var day=0;day<7;day++)db.DoctorSchedules.Add(new(){DoctorId=id,DayOfWeek=day,StartTime=TimeOnly.MinValue,EndTime=new(23,59)});
            }await db.SaveChangesAsync();
        }
        await ReceptionAccessFixture.LoginAsync(client);var requests=new List<BookingRequest>();
        using(var scope=host.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for(var i=0;i<3;i++)requests.Add(new(){IdempotencyKey="capacity-"+i,PatientId=patients[i],TreatmentPlanId=await db.TreatmentPlans.Where(p=>p.Assessment.PatientId==patients[i]).Select(p=>p.Id).SingleAsync(),ClinicianId="room-capacity-doctor-"+i,RoomId=1,StartsAt=[new(2030,2,4,10,0,0,TimeSpan.Zero)],UnitPrice=100,Deposit=i==1?20:100,PaymentMethod=PaymentMethod.Cash});
        }
        var saves=await Task.WhenAll(requests.Select(r=>client.PostAsJsonAsync("/api/Bookings",r)));Assert.Equal(2,saves.Count(r=>r.IsSuccessStatusCode));Assert.Single(saves,r=>r.StatusCode==HttpStatusCode.Conflict);
        var receipts=new List<BookingReceipt>();foreach(var saved in saves.Where(r=>r.IsSuccessStatusCode))receipts.Add((await saved.Content.ReadFromJsonAsync<BookingReceipt>())!);
        foreach(var receipt in receipts)Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync($"/api/Receptionist/appointments/{receipt.Appointments[0].Id}/status",AppointmentStatus.InProgress)).StatusCode);
        var startedId=receipts[0].Appointments[0].Id;
        Assert.Equal(HttpStatusCode.Conflict,(await client.PutAsJsonAsync($"/api/Bookings/appointments/{startedId}/schedule",new RescheduleBookingRequest{StartTime=new(2030,2,5,10,0,0,TimeSpan.Zero),RoomId=2,ClinicianId="room-capacity-doctor-0"})).StatusCode);
        using(var scope=host.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();var room=(await new RoomService(db).GetByIdAsync(1)).Value;Assert.Equal(2,room.CurrentLoad);Assert.Equal(2,room.Occupants.Count);Assert.All(room.Occupants,o=>Assert.StartsWith("Room Doctor",o.ClinicianName));
            var booked=await db.Appointments.FirstAsync();Assert.Equal("Booking.NotUpcoming",(await Assert.ThrowsAsync<BookingRuleException>(()=>new BookingService(db).RescheduleAsync(booked.Id,new(){StartTime=new(2030,2,5,10,0,0,TimeSpan.Zero),RoomId=2,ClinicianId=booked.DoctorId!}))).Code);
        }
    }
    [Fact] public async Task Reception_cannot_collect_insurer_money_but_CEO_can_use_ledger_and_claim_endpoints()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});await ReceptionAccessFixture.SeedAsync(host);await ReceptionAccessFixture.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/Finance/insurance/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/Finance/insurance/claims",new SubmitInsuranceClaimRequest("test",999,"REF"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/Finance/insurance/claims/999/settlements",new SettleInsuranceClaimRequest("test",1,PaymentMethod.Cash,"REF"))).StatusCode);
        await ReceptionAccessFixture.LoginAsync(client,"Ceo");Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/Finance/insurance/accounts")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/Finance/insurance/claims",new SubmitInsuranceClaimRequest("test",999,"REF"))).StatusCode);
    }
    [Fact] public async Task Patient_registration_generates_unique_internal_usernames_without_fake_email_under_concurrency_and_sorts_before_pagination()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});await ReceptionAccessFixture.SeedAsync(host);await ReceptionAccessFixture.LoginAsync(client);
        var names=new[]{"Zara","Aaron","Aaron","\u0623\u062d\u0645\u062f"};
        var responses=await Task.WhenAll(names.Select(fullName=>client.PostAsJsonAsync("/api/Patients/Add",new{fullName,gender=0,birthDate="1990-01-01",phoneNumber="01012345678",paymentType="Cash"})));
        Assert.All(responses,r=>Assert.Equal(HttpStatusCode.OK,r.StatusCode));
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();var users=await db.Users.Where(u=>u.UserName!=null&&u.UserName.StartsWith("patient.")).ToListAsync();
        Assert.Equal(4,users.Count);Assert.Equal(4,users.Select(u=>u.NormalizedUserName).Distinct().Count());Assert.All(users,u=>Assert.Null(u.Email));
        var page=await client.GetFromJsonAsync<JsonElement>("/api/Patients/GetAll?PageNumber=1&PageSize=2");
        var items=page.GetProperty("items").EnumerateArray().ToList();Assert.All(items,p=>Assert.Equal("Aaron",p.GetProperty("fullName").GetString()));Assert.True(items[0].GetProperty("id").GetInt32()<items[1].GetProperty("id").GetInt32());
    }
}
