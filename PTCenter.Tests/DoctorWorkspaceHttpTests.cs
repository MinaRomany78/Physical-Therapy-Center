using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PTCenter.Application.Contracts.Authentication;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Application.Contracts.Doctor;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;
using PTCenter.Infrastructure.Services;
using Xunit;

namespace PTCenter.Tests;

public static class DoctorWorkspaceFixture
{
    public const string Password = ReceptionAccessFixture.Password;
    public static async Task SeedAsync(BookingTestHost host)
    {
        await ReceptionAccessFixture.SeedAsync(host);
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var manager=scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach(var (name,role,gender) in new[]{("clinical.otherdoctor","Doctor",Gender.Male),("clinical.senior","Senior",Gender.Male),("clinical.otherSenior","Senior",Gender.Male),("clinical.femaleSenior","Senior",Gender.Female)})
        {
            var user=new ApplicationUser{UserName=name,FullName=name.Replace("clinical.","Local "),Gender=gender,EmailConfirmed=true};
            Assert.True((await manager.CreateAsync(user,Password)).Succeeded);Assert.True((await manager.AddToRoleAsync(user,role)).Succeeded);
        }
        var doctor=await manager.FindByNameAsync("access.doctor");var senior=await manager.FindByNameAsync("clinical.senior");
        foreach(var p in await db.TreatmentPlans.Include(p=>p.Assessment).ToListAsync()){p.DoctorId=doctor!.Id;p.Assessment.SeniorId=senior!.Id;}
        var users=await db.Users.Where(u=>u.Id==doctor!.Id||u.UserName!.StartsWith("clinical.")).ToListAsync();
        foreach(var u in users)for(var day=0;day<7;day++)db.DoctorSchedules.Add(new(){DoctorId=u.Id,DayOfWeek=day,StartTime=new(0,0),EndTime=new(23,59)});
        db.Rooms.AddRange(new Room{Id=2,Name="Clinical Room 2",Capacity=1,IsActive=true},new Room{Id=3,Name="Clinical Room 3",Capacity=1,IsActive=true});
        await db.SaveChangesAsync();
    }
    public static async Task<string> LoginAsync(HttpClient client,string name)
    {
        var response=await client.PostAsJsonAsync("/api/Auth/Login",new LoginRequest{UserName=name,Password=Password});Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var auth=(await response.Content.ReadFromJsonAsync<AuthResponse>())!;client.DefaultRequestHeaders.Authorization=new("Bearer",auth.Token);return auth.Token;
    }
    public static async Task<int> BookAsync(BookingTestHost host,int patient=1,int room=1,int day=0,decimal deposit=100)
    {
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plan=await db.TreatmentPlans.SingleAsync(p=>p.Assessment.PatientId==patient);
        var receipt=await scope.ServiceProvider.GetRequiredService<BookingService>().CreateAsync(new(){
            IdempotencyKey=Guid.NewGuid().ToString(),PatientId=patient,TreatmentPlanId=plan.Id,ClinicianId=plan.DoctorId,RoomId=room,
            StartsAt=[new DateTimeOffset(2030,2,4,10,0,0,TimeSpan.Zero).AddDays(day)],UnitPrice=100,Deposit=deposit,PaymentMethod=PaymentMethod.Cash});
        return receipt.Appointments[0].Id;
    }
    public static async Task<int> AddCashPatientAsync(BookingTestHost host)
    {
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var original=await db.TreatmentPlans.Include(p=>p.Assessment).FirstAsync();var key=Guid.NewGuid().ToString("N");
        var patient=new Patient{ApplicationUser=new(){Id=key,UserName=key,FullName="Concurrent clinical patient",Gender=Gender.Male},PaymentType="Cash"};
        db.Patients.Add(patient);db.TreatmentPlans.Add(new(){Assessment=new(){Patient=patient,SeniorId=original.Assessment.SeniorId,Diagnosis="Saved",ConditionType="Physio",Priority="Routine",CompletedAt=DateTime.UtcNow},DoctorId=original.DoctorId,NumberOfSessions=3,FrequencyPerWeek=2,TreatmentType="Physio",Status=StatusPlan.Active});await db.SaveChangesAsync();return patient.Id;
    }
    public static async Task ArriveAsync(BookingTestHost host,int id)
    {using var scope=host.Services.CreateScope();await scope.ServiceProvider.GetRequiredService<BookingService>().TransitionAsync(id,AppointmentStatus.InProgress);}
}

public class DoctorWorkspaceHttpTests
{
    [Fact] public async Task Real_Doctor_login_scopes_schedule_clinical_reads_and_blocks_patient_and_other_Doctor()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        await DoctorWorkspaceFixture.SeedAsync(host);var id=await DoctorWorkspaceFixture.BookAsync(host);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/Doctor/schedule?date=2030-02-04")).StatusCode);
        await ReceptionAccessFixture.LoginAsync(client,"Patient");Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"/api/Doctor/appointments/{id}")).StatusCode);
        await DoctorWorkspaceFixture.LoginAsync(client,"clinical.otherdoctor");
        Assert.Empty((await client.GetFromJsonAsync<DoctorScheduleResponse>("/api/Doctor/schedule?date=2030-02-04"))!.Appointments);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"/api/Doctor/appointments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/Assessments/GetAll?patientId=1")).StatusCode);
        await ReceptionAccessFixture.LoginAsync(client,"Doctor");
        Assert.Single((await client.GetFromJsonAsync<DoctorScheduleResponse>("/api/Doctor/schedule?date=2030-02-04"))!.Appointments);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync($"/api/Doctor/appointments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/Assessments/GetAll?patientId=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/Assessments/GetAll")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/Users/GetAll")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/Billing/invoices")).StatusCode);
    }
    [Theory]
    [InlineData("/api/Bookings")]
    [InlineData("/api/Bookings/accounts/course:1/payments")]
    [InlineData("/api/Finance/appointments/1/payments")]
    [InlineData("/api/Finance/insurance/claims")]
    [InlineData("/api/Billing/payments")]
    [InlineData("/api/Assessments/Add")]
    [InlineData("/api/TreatmentPlans/Add")]
    [InlineData("/api/TreatmentPlanSessions/Add")]
    public async Task Doctor_cannot_mutate_finances_prescriptions_or_Senior_assessments(string path)
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});await DoctorWorkspaceFixture.SeedAsync(host);await ReceptionAccessFixture.LoginAsync(client,"Doctor");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path,new{})).StatusCode);
    }
    [Fact] public async Task HTTP_drafts_end_retry_and_Reception_departure_preserve_one_clinical_completion()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});await DoctorWorkspaceFixture.SeedAsync(host);var id=await DoctorWorkspaceFixture.BookAsync(host);
        await ReceptionAccessFixture.LoginAsync(client,"Doctor");
        var notArrived=await client.PostAsJsonAsync($"/api/Doctor/appointments/{id}/start",new{});Assert.Equal(HttpStatusCode.Conflict,notArrived.StatusCode);
        await DoctorWorkspaceFixture.ArriveAsync(host,id);
        var starting=await Task.WhenAll(client.PostAsJsonAsync($"/api/Doctor/appointments/{id}/start",new{}),client.PostAsJsonAsync($"/api/Doctor/appointments/{id}/start",new{}));Assert.All(starting,r=>Assert.Equal(HttpStatusCode.OK,r.StatusCode));
        var active=(await starting[0].Content.ReadFromJsonAsync<DoctorPatientWorkspace>())!;
        var draft=new NotesMutation(active.Notes.Version,"Actual HTTP clinical notes","No change","Tolerated","Saved plan followed");
        var saving=await client.PutAsJsonAsync($"/api/Doctor/appointments/{id}/notes",draft);Assert.Equal(HttpStatusCode.OK,saving.StatusCode);
        var saved=(await client.GetFromJsonAsync<DoctorPatientWorkspace>($"/api/Doctor/appointments/{id}"))!;Assert.Equal(draft.ProgressNotes,saved.Notes.ProgressNotes);
        var final=draft with{ExpectedVersion=saved.Notes.Version};
        var ending=await Task.WhenAll(client.PostAsJsonAsync($"/api/Doctor/appointments/{id}/end",final),client.PostAsJsonAsync($"/api/Doctor/appointments/{id}/end",final));Assert.All(ending,r=>Assert.Equal(HttpStatusCode.OK,r.StatusCode));
        var ended=(await ending[0].Content.ReadFromJsonAsync<DoctorPatientWorkspace>())!;Assert.Null(ended.Visit.DepartedAt);Assert.Equal(1,ended.Plan.Completed);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PutAsJsonAsync($"/api/Doctor/appointments/{id}/notes",draft)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PutAsJsonAsync($"/api/Bookings/appointments/{id}/status",AppointmentStatus.Completed)).StatusCode);
        await ReceptionAccessFixture.LoginAsync(client);
        Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync($"/api/Receptionist/appointments/{id}/status",AppointmentStatus.Completed)).StatusCode);
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();Assert.Equal(1,(await db.TreatmentPlans.SingleAsync(p=>p.Id==ended.Plan.Id)).CompletedSessions);Assert.Single(await db.TreatmentSessionRecords.ToListAsync());
    }
    [Fact] public async Task Two_Seniors_claiming_the_same_free_clinical_review_produce_one_owner_and_no_financial_records()
    {
        using var host=new BookingTestHost();using var doctor=host.CreateClient(new(){BaseAddress=new("https://localhost")});await DoctorWorkspaceFixture.SeedAsync(host);var id=await DoctorWorkspaceFixture.BookAsync(host,deposit:0);
        await ReceptionAccessFixture.LoginAsync(doctor,"Doctor");var response=await doctor.PostAsJsonAsync($"/api/ReassessmentRequests/appointments/{id}",new RequestReassessment("review-http","ProgressConcern","Please review","Pause"));Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var request=(await response.Content.ReadFromJsonAsync<ReassessmentView>())!;
        using var a=host.CreateClient(new(){BaseAddress=new("https://localhost")});using var b=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        await DoctorWorkspaceFixture.LoginAsync(a,"clinical.senior");await DoctorWorkspaceFixture.LoginAsync(b,"clinical.otherSenior");
        Assert.Single((await a.GetFromJsonAsync<List<ReassessmentView>>("/api/ReassessmentRequests"))!);
        var claims=await Task.WhenAll(a.PostAsJsonAsync($"/api/ReassessmentRequests/{request.Id}/start",new StartReassessment(request.Version)),b.PostAsJsonAsync($"/api/ReassessmentRequests/{request.Id}/start",new StartReassessment(request.Version)));
        Assert.Single(claims,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(claims,r=>r.StatusCode==HttpStatusCode.Conflict);
        var owner=claims[0].IsSuccessStatusCode?a:b;var review=(await claims.Single(r=>r.IsSuccessStatusCode).Content.ReadFromJsonAsync<ReassessmentView>())!;
        var completed=await owner.PostAsJsonAsync($"/api/ReassessmentRequests/{request.Id}/complete",new CompleteReassessment(review.Version,"Reviewed during same visit","Continue","","",0,0,"",null));Assert.Equal(HttpStatusCode.OK,completed.StatusCode);
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();Assert.Single(await db.Appointments.ToListAsync());Assert.Single(await db.Invoices.ToListAsync());Assert.Empty(await db.Payments.ToListAsync());Assert.Empty(await db.InsuranceClaims.ToListAsync());Assert.Equal("Completed",(await db.ReassessmentRequests.SingleAsync()).State);
    }
    [Fact] public async Task Simultaneous_actual_starts_cannot_exceed_two_rooms_and_return_structured_conflicts()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});await DoctorWorkspaceFixture.SeedAsync(host);
        int third;
        using(var scope=host.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();var original=await db.TreatmentPlans.Include(p=>p.Assessment).FirstAsync();
            var patient=new Patient{ApplicationUser=new(){Id="clinical-race-third",UserName="clinical.race.third",FullName="Concurrent third patient",Gender=Gender.Male},PaymentType="Cash"};
            db.Patients.Add(patient);db.TreatmentPlans.Add(new(){Assessment=new(){Patient=patient,SeniorId=original.Assessment.SeniorId,Diagnosis="Saved",ConditionType="Physio",Priority="Routine",CompletedAt=DateTime.UtcNow},DoctorId=original.DoctorId,NumberOfSessions=3,FrequencyPerWeek=2,TreatmentType="Physio",Status=StatusPlan.Active});await db.SaveChangesAsync();third=patient.Id;
        }
        var ids=new[]{await DoctorWorkspaceFixture.BookAsync(host),await DoctorWorkspaceFixture.BookAsync(host,2,2,1,20),await DoctorWorkspaceFixture.BookAsync(host,third,3,2)};
        foreach(var id in ids)await DoctorWorkspaceFixture.ArriveAsync(host,id);
        await ReceptionAccessFixture.LoginAsync(client,"Doctor");
        var results=await Task.WhenAll(ids.Select(id=>client.PostAsJsonAsync($"/api/Doctor/appointments/{id}/start",new{})));
        Assert.Equal(2,results.Count(r=>r.StatusCode==HttpStatusCode.OK));
        var denied=Assert.Single(results,r=>r.StatusCode==HttpStatusCode.Conflict);var problem=await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("DoctorCapacity",problem.GetProperty("details").GetProperty("conflicts").EnumerateArray().Select(v=>v.GetString()));
        using var verify=host.Services.CreateScope();var check=verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2,await check.Appointments.CountAsync(a=>a.TreatmentStartedAt!=null&&a.TreatmentEndedAt==null));
    }

    [Fact] public async Task Three_simultaneous_booking_mutations_share_the_same_two_patient_capacity_rule()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});await DoctorWorkspaceFixture.SeedAsync(host);
        var third=await DoctorWorkspaceFixture.AddCashPatientAsync(host);await ReceptionAccessFixture.LoginAsync(client);
        var requests=new List<BookingRequest>();
        using(var scope=host.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach(var (patient,room,deposit) in new[]{(1,1,100m),(2,2,20m),(third,3,100m)}){
                var plan=await db.TreatmentPlans.SingleAsync(p=>p.Assessment.PatientId==patient);
                requests.Add(new(){IdempotencyKey=Guid.NewGuid().ToString(),PatientId=patient,TreatmentPlanId=plan.Id,ClinicianId=plan.DoctorId,RoomId=room,StartsAt=[new(2030,2,4,10,0,0,TimeSpan.Zero)],UnitPrice=100,Deposit=deposit,PaymentMethod=PaymentMethod.Cash});
            }
        }
        var results=await Task.WhenAll(requests.Select(r=>client.PostAsJsonAsync("/api/Bookings",r)));
        Assert.Equal(2,results.Count(r=>r.StatusCode==HttpStatusCode.OK));var denied=Assert.Single(results,r=>r.StatusCode==HttpStatusCode.Conflict);
        var body=await denied.Content.ReadFromJsonAsync<JsonElement>();Assert.Contains("DoctorCapacity",body.GetProperty("details").GetProperty("appointments")[0].GetProperty("conflicts").EnumerateArray().Select(c=>c.GetString()));
        using var verify=host.Services.CreateScope();var check=verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();Assert.Equal(2,await check.Appointments.CountAsync());Assert.Equal(2,await check.BookingBatches.CountAsync());
    }
    [Fact] public async Task Competing_reschedules_cannot_create_a_third_overlap_or_change_saved_money()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});await DoctorWorkspaceFixture.SeedAsync(host);
        var third=await DoctorWorkspaceFixture.AddCashPatientAsync(host);
        await DoctorWorkspaceFixture.BookAsync(host);var a=await DoctorWorkspaceFixture.BookAsync(host,2,2,1,20);var b=await DoctorWorkspaceFixture.BookAsync(host,third,3,2);
        string doctor;string before;
        using(var scope=host.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();doctor=await db.Appointments.Where(x=>x.Id==a).Select(x=>x.DoctorId!).SingleAsync();
            before=JsonSerializer.Serialize(await db.Invoices.OrderBy(i=>i.Id).Select(i=>new{i.Id,i.TotalAmount,i.PatientAmount,i.InsuranceAmount,i.PaidAmount}).ToListAsync());
        }
        await ReceptionAccessFixture.LoginAsync(client);
        var results=await Task.WhenAll(client.PutAsJsonAsync($"/api/Bookings/appointments/{a}/schedule",new RescheduleBookingRequest{StartTime=new(2030,2,4,10,0,0,TimeSpan.Zero),ClinicianId=doctor,RoomId=2}),client.PutAsJsonAsync($"/api/Bookings/appointments/{b}/schedule",new RescheduleBookingRequest{StartTime=new(2030,2,4,10,0,0,TimeSpan.Zero),ClinicianId=doctor,RoomId=3}));
        Assert.Single(results,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(results,r=>r.StatusCode==HttpStatusCode.Conflict);
        using var verify=host.Services.CreateScope();var check=verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(before,JsonSerializer.Serialize(await check.Invoices.OrderBy(i=>i.Id).Select(i=>new{i.Id,i.TotalAmount,i.PatientAmount,i.InsuranceAmount,i.PaidAmount}).ToListAsync()));
        Assert.Equal(2,await check.Appointments.CountAsync(x=>x.StartTime==new DateTime(2030,2,4,10,0,0,DateTimeKind.Utc)));
    }

}
