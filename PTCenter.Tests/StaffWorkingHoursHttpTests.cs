using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Application.Contracts.Users;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;
using Xunit;

namespace PTCenter.Tests;

public class StaffWorkingHoursHttpTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CreateUserRequest Staff() => new() { UserName = "schedule.senior", FullName = "Schedule Senior", Password = "LocalTest@1234",
        Gender = Gender.Male, BirthDate = new(1990, 1, 1), Roles = ["Senior"], WorkingHours = [new(5, new(19, 0), new(21, 0))] };
    private static async Task<HttpClient> Client(BookingTestHost host)
    {
        var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await host.TokenAsync("Ceo"));
        return client;
    }

    [Fact] public async Task Ceo_creates_staff_with_hours_edits_days_and_ranges_and_booking_uses_the_saved_schedule()
    {
        using var host = new BookingTestHost(); using var client = await Client(host);
        var request = Staff();
        var created = await client.PostAsJsonAsync("/api/Users/Add", request);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var user = (await created.Content.ReadFromJsonAsync<UserResponse>(Json))!;
        Assert.Equal(request.WorkingHours, user.WorkingHours);
        int patientId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Users.Add(new ApplicationUser { Id = "schedule-patient", UserName = "schedule.patient", FullName = "Test patient", Gender = Gender.Male });
            var patient = new Patient { ApplicationUserId = "schedule-patient", PaymentType = "Cash" }; db.Patients.Add(patient);
            db.Rooms.Add(new Room { Id = 1, Name = "Test room", IsActive = true, Capacity = 1 });
            await db.SaveChangesAsync(); patientId = patient.Id;
        }
        var booking = new BookingRequest { PatientId = patientId, ClinicianId = user.Id, RoomId = 1, AssessmentCharge = "Free",
            StartsAt = [new DateTimeOffset(2030, 2, 8, 17, 0, 0, TimeSpan.Zero)] }; // Friday 19:00 Cairo.
        Assert.True((await (await client.PostAsJsonAsync("/api/Bookings/preview", booking)).Content.ReadFromJsonAsync<BookingPreview>())!.CanSave);
        var update = new UpdateUserRequest { UserName = request.UserName, FullName = "Updated Senior", Gender = request.Gender, BirthDate = request.BirthDate,
            Roles = request.Roles, WorkingHours = [new(1, new(8, 0), new(12, 0)), new(5, new(8, 0), new(9, 0))] };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/Users/Update?userId={user.Id}", update)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/Users/Update?userId={user.Id}", update)).StatusCode);
        var directory = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/Users/GetAll");
        var saved = directory.GetProperty("value").EnumerateArray().Single(item => item.GetProperty("id").GetString() == user.Id);
        Assert.Equal(2, saved.GetProperty("workingHours").GetArrayLength());
        var preview = (await (await client.PostAsJsonAsync("/api/Bookings/preview", booking)).Content.ReadFromJsonAsync<BookingPreview>())!;
        Assert.Contains("OutsideWorkingHours", preview.Appointments[0].Conflicts);
        using var verify = host.Services.CreateScope();
        var savedDb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2, await savedDb.DoctorSchedules.CountAsync(s => s.DoctorId == user.Id));
        Assert.Single(await savedDb.DoctorSchedules.IgnoreQueryFilters().Where(s => s.DoctorId == user.Id && s.IsDeleted).ToListAsync());
    }

    [Fact] public async Task Invalid_or_overlapping_hours_do_not_create_or_partially_update_a_user()
    {
        using var host = new BookingTestHost(); using var client = await Client(host);
        var request = Staff();
        request.WorkingHours = [new(5, new(18, 0), new(20, 0)), new(5, new(19, 0), new(21, 0))];
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/Users/Add", request)).StatusCode);
        using (var check = host.Services.CreateScope()) Assert.False(await check.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.AnyAsync(u => u.UserName == request.UserName));
        request = Staff(); var user = (await (await client.PostAsJsonAsync("/api/Users/Add", request)).Content.ReadFromJsonAsync<UserResponse>(Json))!;
        var invalid = new UpdateUserRequest { UserName = "changed.senior", FullName = "Should not save", Roles = ["Senior"],
            WorkingHours = [new(7, new(20, 0), new(19, 0))] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/Users/Update?userId={user.Id}", invalid)).StatusCode);
        using var verify = host.Services.CreateScope(); var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(request.FullName, (await db.Users.FindAsync(user.Id))!.FullName);
        Assert.Single(await db.DoctorSchedules.Where(s => s.DoctorId == user.Id).ToListAsync());
    }

    [Fact] public async Task Omitted_hours_are_preserved_and_explicit_empty_hours_clear_the_schedule()
    {
        using var host = new BookingTestHost(); using var client = await Client(host);
        var request = Staff(); var user = (await (await client.PostAsJsonAsync("/api/Users/Add", request)).Content.ReadFromJsonAsync<UserResponse>(Json))!;
        var update = new UpdateUserRequest { UserName = request.UserName, FullName = request.FullName, Roles = request.Roles, Gender = request.Gender, BirthDate = request.BirthDate };
        var preserved = await client.PostAsJsonAsync($"/api/Users/Update?userId={user.Id}", update);
        Assert.Single((await preserved.Content.ReadFromJsonAsync<UserResponse>(Json))!.WorkingHours);
        update.WorkingHours = [];
        var cleared = await client.PostAsJsonAsync($"/api/Users/Update?userId={user.Id}", update);
        Assert.Empty((await cleared.Content.ReadFromJsonAsync<UserResponse>(Json))!.WorkingHours);
    }

    [Fact] public async Task Receptionist_cannot_manage_users_and_booking_has_no_working_hours_actions()
    {
        using var host = new BookingTestHost(); using var client = await Client(host);
        client.DefaultRequestHeaders.Authorization = new("Bearer", await host.TokenAsync("Receptionist"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/Users/Add", Staff())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/Bookings/clinicians/senior/working-hours", new { })).StatusCode);
    }
}
