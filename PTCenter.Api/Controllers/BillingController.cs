using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Abstractions;
using PTCenter.Application.Contracts.Billing;
using PTCenter.Application.Interfaces;

namespace PTCenter.Api.Controllers;

[ApiController]
[Authorize(Roles = "Receptionist,Senior,Doctor,Ceo,Admin,Owner")]
[Route("api/[controller]")]
public class BillingController(IBillingService billing) : ControllerBase
{
    private async Task<IActionResult> Execute<T>(Func<Task<Result<T>>> action)
    {
        try {
            var result=await action();
            return result.IsFailure ? StatusCode(result.Error.StatusCode ?? 400,result.Error) : Ok(result.Value);
        }
        catch(DbUpdateException) { return Conflict(new {code="Billing.ConcurrentChange",description="Financial records changed. Check the original payment history before retrying with the same key."}); }
        catch(Microsoft.Data.SqlClient.SqlException error) when(error.Number is 1205 or 1222)
        { return Conflict(new {code="Billing.ConcurrentChange",description="Financial records changed concurrently. Check the original payment history before retrying with the same key."}); }
    }
    [HttpGet("invoices")]
    public Task<IActionResult> GetAllInvoices() => Execute(billing.GetAllInvoicesAsync);
    [HttpGet("invoices/{id:int}")]
    public Task<IActionResult> GetInvoiceById(int id) => Execute(()=>billing.GetInvoiceByIdAsync(id));
    [HttpPost("invoices")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Operations)]
    public Task<IActionResult> CreateInvoice(CreateInvoiceRequest request) => Execute(()=>billing.CreateInvoiceAsync(request));
    [HttpPatch("invoices/{id:int}/status")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Operations)]
    public Task<IActionResult> UpdateInvoiceStatus(int id,UpdateInvoiceStatusRequest request) => Execute(()=>billing.UpdateInvoiceStatusAsync(id,request));
    [HttpDelete("invoices/{id:int}")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Operations)]
    public Task<IActionResult> DeleteInvoice(int id) => Execute(async()=>{
        var result=await billing.DeleteInvoiceAsync(id);
        return result.IsFailure ? Result.Failure<object>(result.Error) : Result.Success<object>(new{message="Invoice deleted successfully."});
    });
    [HttpPost("payments")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Operations)]
    public Task<IActionResult> AddPayment(CreatePaymentRequest request) => Execute(()=>billing.AddPaymentAsync(request));
    [HttpGet("invoices/{invoiceId:int}/payments")]
    public Task<IActionResult> GetPaymentsByInvoiceId(int invoiceId) => Execute(()=>billing.GetPaymentsByInvoiceIdAsync(invoiceId));
}
