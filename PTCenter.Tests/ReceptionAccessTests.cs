using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PTCenter.Application.Contracts.Authentication;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;
using Xunit;

namespace PTCenter.Tests;

// Real Identity users, production Login/AuthService/JwtProvider and signed JWT validation.
// BookingTestHost replaces SQL Server with a unique temporary SQLite database.
public static class ReceptionAccessFixture
{
    public const string UserName = "reception.access.local";
    public const string Password = "LocalOnly-Reception-42!";
    public static async Task SeedAsync(BookingTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (db.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite") throw new InvalidOperationException("Isolated SQLite required.");
        await db.Database.EnsureCreatedAsync();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var role in new[] { "Receptionist", "Patient", "Doctor", "Ceo" })
        {
            var user = new ApplicationUser { UserName = role == "Receptionist" ? UserName : $"access.{role.ToLowerInvariant()}",
                FullName = $"Local {role}", Email = $"{role.ToLowerInvariant()}@example.invalid", EmailConfirmed = true, Gender = Gender.Male };
            Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
            Assert.True((await manager.AddToRoleAsync(user, role)).Succeeded);
        }
        db.Users.AddRange(new ApplicationUser { Id="access-doctor", UserName="schedule.doctor", FullName="Reception Doctor", Gender=Gender.Male },
            new ApplicationUser { Id="access-senior", UserName="schedule.senior", FullName="Reception Senior", Gender=Gender.Female },
            new ApplicationUser { Id="access-locked", UserName="schedule.locked", FullName="Locked clinician", Gender=Gender.Male, LockoutEnd=DateTimeOffset.UtcNow.AddDays(1) },
            new ApplicationUser { Id="access-selfpay", UserName="reception.selfpay", FullName="Reception Self-pay Patient", Gender=Gender.Male, PhoneNumber="01000000001" },
            new ApplicationUser { Id="access-insured", UserName="reception.insured", FullName="Reception Insured Patient", Gender=Gender.Male, PhoneNumber="01000000002" });
        var roles = await db.Roles.ToDictionaryAsync(r=>r.Name!,r=>r.Id);
        db.UserRoles.AddRange(new(){UserId="access-doctor",RoleId=roles["Doctor"]}, new(){UserId="access-senior",RoleId=roles["Senior"]},
            new(){UserId="access-senior",RoleId=roles["Doctor"]}, new(){UserId="access-locked",RoleId=roles["Doctor"]});
        var company=new InsuranceCompany{Name="Reception Test Insurer", IsActive=true, PortalUrl="https://example.invalid/portal"};
        db.InsuranceCompanies.Add(company); await db.SaveChangesAsync();
        var cash=new Patient{ApplicationUserId="access-selfpay",PaymentType="Cash"};
        var insured=new Patient{ApplicationUserId="access-insured",PaymentType="Insurance",InsuranceCompanyId=company.Id,
            InsuranceDetails=new(){Status=InsuranceStatus.Approved,ApprovedSessions=3,CopayPercentage=20}};
        db.Patients.AddRange(cash,insured); db.Rooms.Add(new(){Id=1,Name="Reception Room",Capacity=1,IsActive=true});
        for(var day=0;day<7;day++) db.DoctorSchedules.Add(new(){DoctorId="access-doctor",DayOfWeek=day,StartTime=new(8,0),EndTime=new(22,0)});
        db.DoctorAbsences.Add(new(){DoctorId="access-senior",AbsentDate=new(2031,1,1),Reason="Private absence reason must not be returned"});
        await db.SaveChangesAsync();
        foreach(var patient in new[]{cash,insured}) db.TreatmentPlans.Add(new(){Assessment=new(){PatientId=patient.Id,SeniorId="access-senior",Diagnosis="Local only",ConditionType="Physio",Priority="Routine",CompletedAt=DateTime.UtcNow},DoctorId="access-doctor",NumberOfSessions=3,FrequencyPerWeek=2,TreatmentType="Physio",Status=StatusPlan.Active});
        await db.SaveChangesAsync();
    }
    public static async Task<string> LoginAsync(HttpClient client, string role="Receptionist")
    {
        var response=await client.PostAsJsonAsync("/api/Auth/Login",new LoginRequest{UserName=role=="Receptionist"?UserName:$"access.{role.ToLowerInvariant()}",Password=Password});
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var auth=(await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        var jwt=new JwtSecurityTokenHandler().ReadJwtToken(auth.Token);
        Assert.Equal(new[]{role},jwt.Claims.Where(c=>c.Type=="roles").Select(c=>c.Value).ToArray());
        client.DefaultRequestHeaders.Authorization=new("Bearer",auth.Token);
        return auth.Token;
    }
}

public class ReceptionAccessTests
{
    public static TheoryData<string> ReadEndpoints => new() {
        "/api/Receptionist/dashboard-stats", "/api/Receptionist/today-sessions?PageNumber=1&PageSize=100", "/api/Receptionist/clinicians",
        "/api/Patients/GetAll?PageNumber=1&PageSize=100", "/api/Patients/GetById/1", "/api/Rooms", "/api/Rooms/1",
        "/api/InsuranceCompanies/GetAll", "/api/InsuranceCompanies/GetById/1", "/api/Appointments/GetAll?PageNumber=1&PageSize=100",
        "/api/Bookings/patients", "/api/Bookings/patients/1", "/api/Bookings/clinicians/workload", "/api/Bookings/patients/1/clinicians",
        "/api/Bookings/accounts", "/api/Billing/invoices",
        "/api/Assessments/GetAll?patientId=1", "/api/Assessments/GetById/1", "/api/TreatmentPlans/GetAll?patientId=1",
        "/api/TreatmentPlans/GetById/1", "/api/TreatmentPlanSessions/GetAll?treatmentPlanId=1", "/api/InsuranceDocuments?patientId=2"
    };
    [Theory, MemberData(nameof(ReadEndpoints))]
    public async Task Every_reception_read_accepts_a_real_Receptionist_login_and_blocks_nonstaff(string url)
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        await ReceptionAccessFixture.SeedAsync(host);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(url)).StatusCode);
        await ReceptionAccessFixture.LoginAsync(client,"Patient");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(url)).StatusCode);
        await ReceptionAccessFixture.LoginAsync(client);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(url)).StatusCode);
    }

    [Fact] public async Task Directory_exposes_only_clinicians_and_safe_availability_without_staff_account_fields()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        await ReceptionAccessFixture.SeedAsync(host);await ReceptionAccessFixture.LoginAsync(client);
        var list=await client.GetFromJsonAsync<JsonElement>("/api/Receptionist/clinicians");
        Assert.DoesNotContain(list.EnumerateArray(),u=>u.GetProperty("id").GetString()=="access-locked");
        var doctor=list.EnumerateArray().Single(u=>u.GetProperty("id").GetString()=="access-doctor");
        Assert.Equal(7,doctor.GetProperty("workingHours").GetArrayLength());
        var senior=list.EnumerateArray().Single(u=>u.GetProperty("id").GetString()=="access-senior");
        Assert.Equal(2,senior.GetProperty("roles").GetArrayLength());Assert.Equal("Female",senior.GetProperty("gender").GetString());
        Assert.Equal("2031-01-01",senior.GetProperty("absenceDates")[0].GetString());
        foreach(var row in list.EnumerateArray()) Assert.Equal(new[]{"absenceDates","currentLoad","gender","id","name","roles","workingHours"},row.EnumerateObject().Select(p=>p.Name).Order().ToArray());
    }

    public static TheoryData<string,string> RestrictedActions => new() {
        {"GET","/api/Users/GetAll"}, {"GET","/api/Users/GetProfile"}, {"POST","/api/Users/Add"}, {"POST","/api/Users/Update?userId=access-doctor"},
        {"DELETE","/api/Users/Delete?userId=access-doctor"}, {"PUT","/api/Users/ChangePassword?userId=access-doctor"},
        {"POST","/api/InsuranceCompanies/Add"}, {"PUT","/api/InsuranceCompanies/Update/1"}, {"DELETE","/api/InsuranceCompanies/Delete/1"},
        {"POST","/api/Assessments/Add"}, {"PUT","/api/Assessments/Update/1"}, {"DELETE","/api/Assessments/Delete/1"},
        {"POST","/api/TreatmentPlans/Add"}, {"PUT","/api/TreatmentPlans/Update/1"}, {"DELETE","/api/TreatmentPlans/Delete/1"},
        {"POST","/api/TreatmentPlanSessions/Add"}, {"PUT","/api/TreatmentPlanSessions/Update/1"}, {"DELETE","/api/TreatmentPlanSessions/Delete/1"},
        {"POST","/api/Bookings/assessments/complete"}, {"POST","/api/Bookings/assessments/1/complete"}
    };
    [Theory, MemberData(nameof(RestrictedActions))]
    public async Task Receptionist_cannot_administer_accounts_insurers_or_author_clinical_records(string method,string url)
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        await ReceptionAccessFixture.SeedAsync(host);await ReceptionAccessFixture.LoginAsync(client);
        using var request=new HttpRequestMessage(new(method),url){Content=JsonContent.Create(new{})};
        Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(request)).StatusCode);
    }

    [Fact] public async Task Receptionist_can_manage_rooms_and_patients_but_Doctor_cannot_change_clerical_records()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        await ReceptionAccessFixture.SeedAsync(host);await ReceptionAccessFixture.LoginAsync(client,"Doctor");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/Rooms",new{name="Extra",capacity=1,isActive=true})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.DeleteAsync("/api/Patients/Delete/1")).StatusCode);
        await ReceptionAccessFixture.LoginAsync(client);
        var room=await client.PostAsJsonAsync("/api/Rooms",new{name="Extra",capacity=1,isActive=true});Assert.Equal(HttpStatusCode.OK,room.StatusCode);
        var roomId=(await room.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync($"/api/Rooms/{roomId}",new{name="Updated",capacity=2,isActive=true})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await client.PatchAsJsonAsync($"/api/Rooms/{roomId}/maintenance",new{maintenance=true})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await client.DeleteAsync($"/api/Rooms/{roomId}")).StatusCode);
        var payload=new{userName="new.reception.patient",fullName="New Reception Patient",gender=0,birthDate="1990-01-01",phoneNumber="01012345678",paymentType="Cash"};
        var saved=await client.PostAsJsonAsync("/api/Patients/Add",payload);Assert.Equal(HttpStatusCode.OK,saved.StatusCode);
        var patientId=(await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync($"/api/Patients/Update/{patientId}",new{payload.userName,fullName="Updated Patient",payload.gender,payload.birthDate,payload.phoneNumber,payload.paymentType,email="updated@example.invalid"})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.DeleteAsync($"/api/Patients/Delete/{patientId}")).StatusCode);
        using var verify=host.Services.CreateScope();var db=verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var deleted=await db.Patients.IgnoreQueryFilters().SingleAsync(p=>p.Id==patientId);
        Assert.True(deleted.IsDeleted);Assert.True(await db.Users.AnyAsync(u=>u.Id==deleted.ApplicationUserId));
    }

    [Fact] public async Task Partial_patient_edit_preserves_unsupplied_identity_insurance_and_funding_fields()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        await ReceptionAccessFixture.SeedAsync(host);await ReceptionAccessFixture.LoginAsync(client);
        var edit=await client.PutAsJsonAsync("/api/Patients/Update/2",new{fullName="Edited insured patient"});Assert.Equal(HttpStatusCode.OK,edit.StatusCode);
        var patient=await edit.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("Edited insured patient",patient.GetProperty("fullName").GetString());
        Assert.Equal("Insurance",patient.GetProperty("paymentType").GetString());Assert.Equal(1,patient.GetProperty("insuranceCompanyId").GetInt32());
        Assert.Equal(3,patient.GetProperty("insuranceDetails").GetProperty("approvedSessions").GetInt32());
        Assert.Equal(20,patient.GetProperty("insuranceDetails").GetProperty("copayPercentage").GetDecimal());
        Assert.Equal("01000000002",patient.GetProperty("phoneNumber").GetString());
        Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync("/api/Patients/Update/2",new{emergencyContact="01011112222"})).StatusCode);
        var clear=await client.PutAsJsonAsync("/api/Patients/Update/2",new{emergencyContact=(string?)null});
        Assert.Equal(JsonValueKind.Null,(await clear.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("emergencyContact").ValueKind);
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("reception.insured",(await db.Users.FindAsync("access-insured"))!.UserName);
    }

    [Fact] public async Task CEO_retains_staff_and_insurer_administration_while_an_unrecognized_role_is_rejected()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        await ReceptionAccessFixture.SeedAsync(host);
        client.DefaultRequestHeaders.Authorization=new("Bearer",BookingTestHost.Token("Unrecognized"));
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/Receptionist/clinicians")).StatusCode);
        await ReceptionAccessFixture.LoginAsync(client,"Ceo");Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/Users/GetAll")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/api/InsuranceCompanies/Add",new{name="CEO managed insurer",isActive=true})).StatusCode);
    }

    public static TheoryData<string,string> ReceptionWrites => new() {
        {"POST","/api/Rooms/availability"},{"POST","/api/Rooms"},{"PUT","/api/Rooms/1"},{"PATCH","/api/Rooms/1/maintenance"},{"DELETE","/api/Rooms/1"},
        {"POST","/api/Patients/Add"},{"PUT","/api/Patients/Update/1"},{"DELETE","/api/Patients/Delete/1"},
        {"POST","/api/Bookings/preview"},{"POST","/api/Bookings"},{"POST","/api/Bookings/accounts/test/payments"},{"PUT","/api/Bookings/appointments/1/schedule"},{"PUT","/api/Bookings/appointments/1/status"},
        {"PUT","/api/Receptionist/appointments/1/status"},{"PUT","/api/Receptionist/appointments/1/cancel"},
        {"POST","/api/Appointments"},{"PUT","/api/Appointments/1"},{"PATCH","/api/Appointments/1/status"},{"DELETE","/api/Appointments/1"},
        {"POST","/api/Billing/payments"},{"POST","/api/Finance/appointments/1/payments"},{"POST","/api/Finance/insurance/claims"},{"POST","/api/Finance/insurance/claims/1/settlements"},
        {"POST","/api/InsuranceDocuments/Add"},{"PUT","/api/InsuranceDocuments/1"},{"DELETE","/api/InsuranceDocuments/1"}
    };
    [Theory,MemberData(nameof(ReceptionWrites))]
    public async Task Reception_mutations_reject_anonymous_and_Patient_role_requests_before_model_binding(string method,string url)
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        using var anonymous=new HttpRequestMessage(new(method),url){Content=JsonContent.Create(new{})};
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.SendAsync(anonymous)).StatusCode);
        client.DefaultRequestHeaders.Authorization=new("Bearer",BookingTestHost.Token("Patient"));
        using var patient=new HttpRequestMessage(new(method),url){Content=JsonContent.Create(new{})};
        Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(patient)).StatusCode);
    }

    [Fact] public async Task Patient_scoped_documents_can_be_uploaded_read_downloaded_updated_and_deleted_only_by_staff()
    {
        using var host=new BookingTestHost();using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        await ReceptionAccessFixture.SeedAsync(host);
        var directory=Path.Combine(Path.GetTempPath(),$"ptcenter-reception-documents-{Guid.NewGuid():N}");Directory.CreateDirectory(directory);
        using(var scope=host.Services.CreateScope())scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>().WebRootPath=directory;
        try {
            await ReceptionAccessFixture.LoginAsync(client);
            Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/InsuranceDocuments")).StatusCode);
            using var form=new MultipartFormDataContent();form.Add(new StringContent("2"),"PatientId");form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("insurance test")),"Files","approval.txt");
            var upload=await client.PostAsync("/api/InsuranceDocuments/Add",form);Assert.Equal(HttpStatusCode.OK,upload.StatusCode);
            var id=(await upload.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetInt32();
            Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/InsuranceDocuments?patientId=1")).EnumerateArray());
            Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/InsuranceDocuments?patientId=2")).EnumerateArray());
            Assert.Equal("insurance test",await client.GetStringAsync($"/api/InsuranceDocuments/{id}/file"));
            client.DefaultRequestHeaders.Authorization=null;Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync($"/api/InsuranceDocuments/{id}/file")).StatusCode);
            await ReceptionAccessFixture.LoginAsync(client,"Patient");Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"/api/InsuranceDocuments/{id}/file")).StatusCode);
            await ReceptionAccessFixture.LoginAsync(client);
            using var update=new MultipartFormDataContent();update.Add(new ByteArrayContent([1,2,3]),"File","updated.txt");
            Assert.Equal(HttpStatusCode.OK,(await client.PutAsync($"/api/InsuranceDocuments/{id}",update)).StatusCode);
            Assert.Equal(HttpStatusCode.OK,(await client.DeleteAsync($"/api/InsuranceDocuments/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"/api/InsuranceDocuments/{id}/file")).StatusCode);
        } finally {
            // This directory was created above with a unique name and never points at application data.
            if(Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(),"ptcenter-reception-documents-")),StringComparison.OrdinalIgnoreCase))Directory.Delete(directory,true);
        }
    }
}
