using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;
using Xunit;

namespace PTCenter.Tests;

public class CourseFinanceHttpTests
{
    [Fact] public async Task Concurrent_course_batches_receipts_and_starts_cannot_duplicate_charges_or_overspend_coverage()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        client.DefaultRequestHeaders.Authorization=new("Bearer",await host.TokenAsync("Receptionist"));
        int patientId,planId;
        using(var scope=host.Services.CreateScope()) {
            var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();await db.Database.EnsureCreatedAsync();
            db.Users.AddRange(new ApplicationUser{Id="con-doctor",FullName="Doctor",UserName="con-doctor",Gender=Gender.Male},new ApplicationUser{Id="con-patient",FullName="Patient",UserName="con-patient",Gender=Gender.Male});await db.SaveChangesAsync();
            var role=await db.Roles.SingleAsync(r=>r.Name=="Doctor");db.UserRoles.Add(new(){UserId="con-doctor",RoleId=role.Id});
            var patient=new Patient{ApplicationUserId="con-patient",PaymentType="Cash"};db.Patients.Add(patient);db.Rooms.Add(new(){Id=1,Name="Room",Capacity=1,IsActive=true});
            for(var day=0;day<7;day++)db.DoctorSchedules.Add(new(){DoctorId="con-doctor",DayOfWeek=day,StartTime=new(8,0),EndTime=new(20,0)});
            await db.SaveChangesAsync();patientId=patient.Id;
            var plan=new TreatmentPlan{Assessment=new(){PatientId=patientId,SeniorId="con-doctor",Diagnosis="Diagnosis",ConditionType="Physio",Priority="Routine",CompletedAt=DateTime.UtcNow},DoctorId="con-doctor",NumberOfSessions=3,FrequencyPerWeek=2,TreatmentType="Physio",Status=StatusPlan.Active};
            db.TreatmentPlans.Add(plan);await db.SaveChangesAsync();planId=plan.Id;
        }
        BookingRequest Request(int day)=>new(){IdempotencyKey=Guid.NewGuid().ToString(),PatientId=patientId,ClinicianId="con-doctor",RoomId=1,TreatmentPlanId=planId,PricingMode="Package",UnitPrice=100,StartsAt=[new DateTimeOffset(2030,2,4+day,10,0,0,TimeSpan.Zero)]};
        var saves=await Task.WhenAll(client.PostAsJsonAsync("/api/Bookings",Request(0)),client.PostAsJsonAsync("/api/Bookings",Request(1)));
        Assert.All(saves,r=>Assert.Equal(HttpStatusCode.OK,r.StatusCode));
        var first=(await saves[0].Content.ReadFromJsonAsync<BookingReceipt>())!;var second=(await saves[1].Content.ReadFromJsonAsync<BookingReceipt>())!;
        Assert.Equal(300,first.TotalAmount);Assert.Equal(300,second.TotalAmount);
        var id=first.Appointments[0].Id;
        var payment=new CollectBookingPaymentRequest(Guid.NewGuid().ToString(),50,PaymentMethod.Cash);
        var simultaneous=await Task.WhenAll(client.PostAsJsonAsync($"/api/Finance/appointments/{id}/payments",payment),client.PostAsJsonAsync($"/api/Finance/appointments/{id}/payments",payment));
        Assert.All(simultaneous,r=>Assert.Equal(HttpStatusCode.OK,r.StatusCode));
        Assert.Equal(50,(await client.GetFromJsonAsync<PaymentGate>($"/api/Finance/appointments/{id}/payment-gate"))!.AdditionalPaymentRequired);
        var balanceRaces=await Task.WhenAll(client.PostAsJsonAsync($"/api/Finance/appointments/{id}/payments",new CollectBookingPaymentRequest(Guid.NewGuid().ToString(),200,PaymentMethod.Visa)),client.PostAsJsonAsync($"/api/Finance/appointments/{id}/payments",new CollectBookingPaymentRequest(Guid.NewGuid().ToString(),200,PaymentMethod.Visa)));
        Assert.Single(balanceRaces,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(balanceRaces,r=>r.StatusCode==HttpStatusCode.BadRequest);
        var starts=await Task.WhenAll(client.PutAsJsonAsync($"/api/Bookings/appointments/{id}/status",4),client.PutAsJsonAsync($"/api/Bookings/appointments/{second.Appointments[0].Id}/status",4));
        Assert.Single(starts,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(starts,r=>r.StatusCode==HttpStatusCode.Conflict);
        using var verify=host.Services.CreateScope();var saved=verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1,await saved.TreatmentCourseAgreements.CountAsync());Assert.Equal(3,await saved.Invoices.CountAsync());Assert.Equal(300,await saved.Invoices.SumAsync(i=>i.TotalAmount));
        Assert.Equal(250,await saved.Payments.SumAsync(p=>p.Amount));Assert.Equal(1,await saved.Appointments.CountAsync(a=>a.ActualCheckIn!=null));
    }
}
