using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Infrastructure.Persistence;
using PTCenter.Infrastructure.Services;

namespace PTCenter.Api.Controllers;

[ApiController]
[Route("api/Finance")]
[Authorize(Roles = "Receptionist,Senior,Doctor,Ceo,Admin,Owner")]
public class CourseFinanceController(ApplicationDbContext db) : ControllerBase
{
    private readonly CourseFinanceService finance = new(db);
    private async Task<IActionResult> Execute<T>(Func<Task<T>> action)
    {
        try { return Ok(await action()); }
        catch (BookingRuleException e) { return Problem(statusCode: e.Status, title: e.Code, detail: e.Message); }
        catch (DbUpdateException) { return Problem(statusCode: 409, title: "Booking.ConcurrentChange", detail: "Financial records changed. Reconcile the same operation before retrying."); }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 1205 or 1222 or 51001) { return Problem(statusCode: 409, title: "Booking.ConcurrentChange", detail: "Financial records changed concurrently. Retry the same operation key."); }
    }
    [HttpGet("appointments/{id:int}/payment-gate")]
    public Task<IActionResult> Gate(int id) => Execute(() => finance.GateAsync(id));
    [HttpPost("appointments/{id:int}/payments")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Operations)]
    public Task<IActionResult> Collect(int id, CollectBookingPaymentRequest request) => Execute(() => finance.CollectForAppointmentAsync(id, request));
    [HttpGet("insurance/accounts")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Administration)]
    public Task<IActionResult> InsuranceAccounts() => Execute(() => finance.InsuranceAccountsAsync());
    [HttpPost("insurance/claims")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Administration)]
    public Task<IActionResult> Claim(SubmitInsuranceClaimRequest request) => Execute(() => finance.SubmitClaimAsync(request, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ""));
    [HttpPost("insurance/claims/{id:int}/settlements")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Administration)]
    public Task<IActionResult> Settle(int id, SettleInsuranceClaimRequest request) => Execute(async () => new { paymentId = await finance.SettleClaimAsync(id, request) });
}
