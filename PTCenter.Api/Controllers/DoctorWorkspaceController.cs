using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Doctor;
using PTCenter.Infrastructure.Persistence;
using PTCenter.Infrastructure.Services;

namespace PTCenter.Api.Controllers;

public abstract class ClinicalController : ControllerBase
{
    protected string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    protected bool Supervisor => User.IsInRole("Ceo") || User.IsInRole("Admin") || User.IsInRole("Owner");
    protected async Task<IActionResult> Execute<T>(Func<Task<T>> action)
    {
        try { return Ok(await action()); }
        catch (ClinicalRuleException e) { return Problem(statusCode: e.Status, title: e.Code, detail: e.Message, extensions: e.Details == null ? null : new Dictionary<string, object?> { ["details"] = e.Details }); }
        catch (BookingRuleException e) { return Problem(statusCode: e.Status, title: e.Code, detail: e.Message); }
        catch (DbUpdateConcurrencyException) { return Problem(statusCode: 409, title: "Doctor.ConcurrentEdit", detail: "The record changed. Your edits were not applied. Reload its current version before retrying."); }
        catch (DbUpdateException) { return Problem(statusCode: 409, title: "Doctor.ConcurrentChange", detail: "The operation could not be confirmed. Refresh the record and retry the original operation."); }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 1205 or 1222 or 51001) { return Problem(statusCode: 409, title: "Doctor.ConcurrentChange", detail: "A concurrent action changed clinic availability. Refresh before retrying."); }
    }
}

[ApiController, Route("api/Doctor"), Authorize(Roles = "Doctor,Ceo,Admin,Owner")]
public class DoctorWorkspaceController(DoctorWorkspaceService service, ApplicationDbContext db, IWebHostEnvironment environment) : ClinicalController
{
    [HttpGet("schedule")]
    public Task<IActionResult> Schedule([FromQuery] DateOnly? date, [FromQuery] string? doctorId) => Execute(() => service.ScheduleAsync(date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ClinicCalendar.Zone).DateTime), Actor, Supervisor, doctorId));
    [HttpGet("appointments/{id:int}")]
    public Task<IActionResult> Workspace(int id) => Execute(() => service.GetAsync(id, Actor, Supervisor));
    [HttpPost("appointments/{id:int}/start")]
    public Task<IActionResult> Start(int id) => Execute(() => service.StartAsync(id, Actor, Supervisor));
    [HttpPut("appointments/{id:int}/notes")]
    public Task<IActionResult> Draft(int id, NotesMutation request) => Execute(() => service.SaveAsync(id, request, Actor, Supervisor, false));
    [HttpPost("appointments/{id:int}/end")]
    public Task<IActionResult> End(int id, NotesMutation request) => Execute(() => service.SaveAsync(id, request, Actor, Supervisor, true));
    [HttpGet("appointments/{id:int}/documents/{documentId:int}")]
    public async Task<IActionResult> Download(int id, int documentId)
    {
        try
        {
            var a = await service.AssignedAsync(id, Actor, Supervisor);
            var document = await db.InsuranceDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == documentId && d.PatientId == a.PatientId);
            if (document == null) return NotFound();
            var root = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
            var allowed = Path.GetFullPath(Path.Combine(root, "uploads", "insurance-documents")) + Path.DirectorySeparatorChar;
            var file = Path.GetFullPath(Path.Combine(root, document.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!file.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(file)) return NotFound();
            return PhysicalFile(file, "application/octet-stream", Path.GetFileName(document.FileName));
        }
        catch (BookingRuleException e) { return Problem(statusCode: e.Status, title: e.Code, detail: e.Message); }
    }
}
