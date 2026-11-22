using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PTCenter.Application.Contracts.Ceo;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;
using PTCenter.Infrastructure.Services;
using Xunit;

namespace PTCenter.Tests;

public class CeoWorkspaceTests
{
    [Theory, InlineData("Admin"), InlineData("Owner")]
    public async Task Existing_administrative_read_scope_remains_available(string role)
    {
        using var host=new BookingTestHost();await ReceptionAccessFixture.SeedAsync(host);using var c=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        c.DefaultRequestHeaders.Authorization=new("Bearer",await host.TokenAsync(role));
        Assert.Equal(HttpStatusCode.OK,(await c.GetAsync("/api/ceo/staff")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await c.GetAsync("/api/ceo/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await c.GetAsync("/api/ceo/reports/patient-outstanding")).StatusCode);
    }
    [Fact] public async Task Overview_uses_posted_Cairo_receipts_and_delivered_liability_instead_of_invoice_face_amounts()
    {
        using var host=new BookingTestHost();await InsuranceMonthFixture.SeedAsync(host);using var c=host.CreateClient(new(){BaseAddress=new("https://localhost")});await ReceptionAccessFixture.LoginAsync(c,"Ceo");
        var overview=await Read<CeoOverview>(await c.GetAsync("/api/ceo/overview?from=2026-10-01&to=2026-10-31"));
        Assert.Equal(200,overview.PatientCollections);Assert.Equal(0,overview.InsurerCollections);Assert.Equal(800,overview.PlannedInsurerLiability);Assert.Equal(720,overview.EarnedInsurerOutstanding);
        Assert.Equal(200,overview.Collections.Sum(x=>x.Patient));Assert.Equal(200,overview.Collections.Single(x=>x.Day=="2026-10-01").Patient);
        Assert.Equal(0,overview.PatientOutstanding);Assert.Equal(31,overview.Collections.Count);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }
    [Theory, InlineData(-1), InlineData(101), InlineData(0.001)]
    public async Task Invalid_copay_cannot_create_a_patient_or_rewrite_approval_terms(double copay)
    {
        using var host=new BookingTestHost();await ReceptionAccessFixture.SeedAsync(host);using var c=host.CreateClient(new(){BaseAddress=new("https://localhost")});await ReceptionAccessFixture.LoginAsync(c);
        var r=await c.PutAsJsonAsync("/api/Patients/Update/2",new{insuranceDetails=new{status="Approved",approvedSessions=3,copayPercentage=copay}});
        Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();Assert.Equal(20,(await db.Patients.Include(p=>p.InsuranceDetails).SingleAsync(p=>p.Id==2)).InsuranceDetails!.CopayPercentage);
    }
    [Fact] public async Task Settings_persist_identity_and_actor_reject_stale_edits_and_do_not_change_bookings()
    {
        using var host = new BookingTestHost(); await ReceptionAccessFixture.SeedAsync(host); using var c = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        await ReceptionAccessFixture.LoginAsync(c,"Receptionist"); Assert.Equal(HttpStatusCode.Forbidden,(await c.GetAsync("/api/ceo/settings")).StatusCode);
        Assert.Equal("Physical Therapy Center", (await Read<ClinicIdentity>(await c.GetAsync("/api/clinic-identity"))).Name);
        await ReceptionAccessFixture.LoginAsync(c,"Ceo");
        var first = await Read<ClinicIdentity>(await c.GetAsync("/api/ceo/settings"));
        var request = new SettingsRequest { Name="Test Clinic", Phone="01011112222", Address="Test street", Email="clinic@example.invalid", Version=first.Version };
        var saved = await Read<ClinicIdentity>(await c.PutAsJsonAsync("/api/ceo/settings",request)); Assert.Equal(first.Version+1,saved.Version); Assert.NotNull(saved.UpdatedBy); Assert.NotNull(saved.UpdatedAt);
        Assert.Equal(HttpStatusCode.Conflict,(await c.PutAsJsonAsync("/api/ceo/settings",request)).StatusCode);
        Assert.Equal("Test Clinic",(await Read<ClinicIdentity>(await c.GetAsync("/api/clinic-identity"))).Name);
        Assert.Equal(HttpStatusCode.BadRequest,(await c.PutAsJsonAsync("/api/ceo/settings",new {name=" ",version=saved.Version})).StatusCode);
    }
    [Fact] public async Task Staff_paging_excludes_patients_and_deactivation_preserves_identity_and_invalidates_existing_token()
    {
        using var host = new BookingTestHost(); await ReceptionAccessFixture.SeedAsync(host); using var c = host.CreateClient(new() {BaseAddress=new("https://localhost")});
        var token = await ReceptionAccessFixture.LoginAsync(c,"Doctor");
        await ReceptionAccessFixture.LoginAsync(c,"Ceo"); var all = await Read<StaffPage>(await c.GetAsync("/api/ceo/staff?pageSize=1")); Assert.Single(all.Items); Assert.True(all.Total>1);
        var page = await Read<StaffPage>(await c.GetAsync("/api/ceo/staff?role=Doctor&search=Local")); var doctor = Assert.Single(page.Items); Assert.DoesNotContain("Patient",doctor.Roles);
        Assert.Equal(HttpStatusCode.OK,(await c.PutAsJsonAsync($"/api/ceo/staff/{doctor.Id}/status",new StaffStatusRequest(false,doctor.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await c.PutAsJsonAsync($"/api/ceo/staff/{doctor.Id}/status",new StaffStatusRequest(true,doctor.Version))).StatusCode);
        using var old = host.CreateClient(new(){BaseAddress=new("https://localhost")}); old.DefaultRequestHeaders.Authorization=new("Bearer",token);
        Assert.Equal(HttpStatusCode.Unauthorized,(await old.GetAsync("/api/clinic-identity")).StatusCode);
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();Assert.NotNull(await db.Users.FindAsync(doctor.Id));Assert.False((await db.Users.FindAsync(doctor.Id))!.IsActive);
    }
    [Fact] public async Task Future_appointments_block_deactivation_and_hours_or_role_changes_without_reassignment()
    {
        using var host=new BookingTestHost();await ReceptionAccessFixture.SeedAsync(host);
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now=DateTime.UtcNow;db.Appointments.Add(new(){PatientId=1,DoctorId="access-doctor",AppointmentType="Session",StartTime=now.AddDays(3),EndTime=now.AddDays(3).AddHours(1),Status=AppointmentStatus.Pending});await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BookingRuleException>(()=>StaffSafety.AssertFutureWorkAsync(db,"access-doctor",now,null,null,true));
        await Assert.ThrowsAsync<BookingRuleException>(()=>StaffSafety.AssertFutureWorkAsync(db,"access-doctor",now,["Receptionist"],null));
        await Assert.ThrowsAsync<BookingRuleException>(()=>StaffSafety.AssertFutureWorkAsync(db,"access-doctor",now,["Doctor"],[new(0,new(0,0),new(1,0))]));
        Assert.Equal("access-doctor",(await db.Appointments.SingleAsync()).DoctorId);
    }
    [Fact] public async Task Linked_company_cannot_be_deleted_deactivation_retains_agreements_and_blocks_new_patient_selection()
    {
        using var host=new BookingTestHost();await ReceptionAccessFixture.SeedAsync(host);using var c=host.CreateClient(new(){BaseAddress=new("https://localhost")});await ReceptionAccessFixture.LoginAsync(c,"Ceo");
        Assert.Equal(HttpStatusCode.Conflict,(await c.DeleteAsync("/api/InsuranceCompanies/Delete/1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await c.PutAsJsonAsync("/api/InsuranceCompanies/Update/1",new{name="Reception Test Insurer",isActive=false})).StatusCode);
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();Assert.NotNull(await db.InsuranceCompanies.FindAsync(1));Assert.Equal(1,(await db.Patients.FindAsync(2))!.InsuranceCompanyId);
        Assert.Equal(HttpStatusCode.Conflict,(await c.DeleteAsync("/api/Users/Delete?userId=access-doctor")).StatusCode);
    }
    [Fact] public async Task Finance_reports_use_allocations_and_current_balances_exclude_waived_and_preserve_historical_company()
    {
        using var host=new BookingTestHost();await InsuranceMonthFixture.SeedAsync(host);using var c=host.CreateClient(new(){BaseAddress=new("https://localhost")});await ReceptionAccessFixture.LoginAsync(c,"Ceo");
        var patient = await Read<CeoReport>(await c.GetAsync("/api/ceo/reports/patient-collections?from=2026-10-01&to=2026-10-31&pageSize=1"));
        Assert.Equal(200,patient.Totals.Collected);Assert.Single(patient.Items);Assert.Equal(10,patient.Total);
        var second=await Read<CeoReport>(await c.GetAsync("/api/ceo/reports/patient-collections?from=2026-10-01&to=2026-10-31&pageSize=1&page=2"));Assert.Equal(patient.Totals,second.Totals);Assert.NotEqual(patient.Items[0].Id,second.Items[0].Id);
        var historical=await Read<CeoReport>(await c.GetAsync("/api/ceo/reports/insurer-receivables?companyId=1"));Assert.Equal(560,historical.Totals.Remaining);Assert.All(historical.Items,x=>Assert.Equal(1,x.CompanyId));
        Assert.Equal(HttpStatusCode.BadRequest,(await c.GetAsync("/api/ceo/reports/patient-outstanding?from=2026-10-01")).StatusCode);
        var csv=await c.GetStringAsync("/api/ceo/reports/patient-collections/export?from=2026-10-01&to=2026-10-31&pageSize=1");Assert.Contains("Currency",csv);Assert.Contains("allocation:",csv);Assert.Contains("\"200.0",csv);
        await ReceptionAccessFixture.LoginAsync(c);Assert.Equal(HttpStatusCode.Forbidden,(await c.GetAsync("/api/ceo/reports/patient-collections/export")).StatusCode);
    }
}
