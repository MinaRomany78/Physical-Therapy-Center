using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Ceo;
using PTCenter.Infrastructure.Persistence;
using PTCenter.Infrastructure.Services;

namespace PTCenter.Api.Controllers;

[ApiController, Route("api/ceo"), Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Administration)]
public class CeoWorkspaceController(ApplicationDbContext db, TimeProvider clock, ILogger<CeoWorkspaceController> logger) : ControllerBase
{
    private CeoReadService Read => new(db, clock);
    private CeoManagementService Management => new(db, clock);
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private async Task<IActionResult> Run<T>(Func<Task<T>> action)
    {
        try { return Ok(await action()); }
        catch (BookingRuleException e) { return StatusCode(e.Status, new { title = e.Code, detail = e.Message, e.Details }); }
        catch (DbUpdateException) { return Conflict(new { title = "Ceo.ConcurrentChange", detail = "Data changed. Reload and review before saving." }); }
        catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e,"CEO workspace operation failed"); return Problem(statusCode:500,title:"Ceo.Unavailable",detail:"CEO data is unavailable. Retry without changing the saved scope."); }
    }
    [HttpGet("overview")] public Task<IActionResult> Overview([FromQuery] CeoQuery q, CancellationToken ct) => Run(() => Read.OverviewAsync(q, ct));
    [HttpGet("report-options")]
    public async Task<IActionResult> ReportOptions(CancellationToken ct) => Ok(new {
        companies = await db.InsuranceCompanies.IgnoreQueryFilters().AsNoTracking().OrderBy(c => c.Name).Take(200).Select(c => new { c.Id, c.Name }).ToListAsync(ct),
        clinicians = await db.Users.AsNoTracking().Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && db.Roles.Any(r => r.Id == ur.RoleId && (r.Name == "Doctor" || r.Name == "Senior"))))
            .OrderBy(u => u.FullName).Take(200).Select(u => new { u.Id, Name = u.FullName }).ToListAsync(ct)
    });
    [HttpGet("reports/{kind}")] public Task<IActionResult> Report(string kind, [FromQuery] CeoQuery q, CancellationToken ct) => Run(() => Read.ReportAsync(kind, q, ct));
    [HttpGet("reports/{kind}/export")]
    public async Task<IActionResult> Export(string kind, [FromQuery] CeoQuery q, CancellationToken ct)
    {
        try
        {
            var r = await Read.ReportAsync(kind, q, ct, true); var identity = await Management.IdentityAsync(ct); var csv = new FinancialCsv();
            csv.Line("Clinic", identity.Name, identity.Phone, identity.Address, identity.Email);
            csv.Line("Report", r.Kind, "Date basis", r.DateBasis, "From", r.From, "To", r.To);
            csv.Line("Generated UTC", r.GeneratedAt.ToString("O"), "Timezone", r.Timezone, "Currency", r.Currency);
            csv.Line("Filters", System.Text.Json.JsonSerializer.Serialize(q));
            csv.Line("Matching rows", r.Total, "Amount", r.Totals.Amount, "Collected", r.Totals.Collected, "Remaining", r.Totals.Remaining,
                "Recorded clinical seconds", r.Totals.RecordedDurationSeconds, "Missing clinical timestamps", r.Totals.MissingClinicalTimestamps);
            csv.Line("ID", "Reference / operation", "Patient", "Patient ID", "Company ID", "Historical company", "Clinician", "Clinician ID", "Date UTC", "Amount", "Collected", "Remaining", "Status", "Method", "Scheduled end UTC", "Arrival UTC", "Departure UTC", "Clinical start UTC", "Clinical end UTC", "Recorded seconds", "Visits", "Clinically completed", "Missing clinical timestamps");
            foreach (var x in r.Items) csv.Line(x.Id, x.Label, x.Patient, x.PatientId, x.CompanyId, x.Company, x.Clinician, x.ClinicianId, x.Date?.ToString("O"), x.Amount, x.Collected, x.Remaining, x.Status, x.Method,
                x.ScheduledEnd?.ToString("O"), x.ArrivedAt?.ToString("O"), x.DepartedAt?.ToString("O"), x.ClinicalStart?.ToString("O"), x.ClinicalEnd?.ToString("O"), x.DurationSeconds, x.VisitCount, x.CompletedCount, x.MissingClinicalTimestamps);
            return File(csv.Bytes(), "text/csv; charset=utf-8", $"ceo-{kind}-{r.GeneratedAt:yyyyMMdd-HHmmss}.csv");
        }
        catch (BookingRuleException e) { return Problem(statusCode: e.Status, title: e.Code, detail: e.Message); }
        catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e,"CEO export failed"); return Problem(statusCode:500,title:"Ceo.Unavailable",detail:"Report export is unavailable. Retry the saved scope."); }
    }
    [HttpGet("settings")] public Task<IActionResult> Settings(CancellationToken ct) => Run(() => Management.IdentityAsync(ct));
    [HttpPut("settings")] public Task<IActionResult> Settings(SettingsRequest r, CancellationToken ct) => Run(() => Management.SaveSettingsAsync(r, Actor, ct));
    [HttpGet("staff")] public Task<IActionResult> Staff([FromQuery] StaffQuery q, CancellationToken ct) => Run(() => Management.StaffAsync(q, ct));
    [HttpPut("staff/{id}/status")] public Task<IActionResult> StaffStatus(string id, StaffStatusRequest r, CancellationToken ct) => Run(async () => { await Management.SetStaffStatusAsync(id, r, Actor, ct); return new { saved = true }; });
}

[ApiController, Route("api/clinic-identity"), Authorize]
public class ClinicIdentityController(ApplicationDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet] public async Task<ClinicIdentity> Identity(CancellationToken ct) => await new CeoManagementService(db, clock).IdentityAsync(ct);
}
