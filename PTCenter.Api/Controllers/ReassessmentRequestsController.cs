using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTCenter.Application.Contracts.Doctor;
using PTCenter.Infrastructure.Services;

namespace PTCenter.Api.Controllers;

[ApiController, Route("api/ReassessmentRequests"), Authorize(Roles = "Doctor,Senior,Receptionist,Ceo,Admin,Owner")]
public class ReassessmentRequestsController(ReassessmentService service) : ClinicalController
{
    [HttpGet]
    public Task<IActionResult> List() => Execute(() => service.ListAsync(Actor, Supervisor || User.IsInRole("Receptionist"), User.IsInRole("Senior")));
    [HttpGet("{id:int}")]
    public Task<IActionResult> Get(int id) => Execute(async () => (await service.ListAsync(Actor, Supervisor || User.IsInRole("Receptionist"), User.IsInRole("Senior"), requestId:id, includeHistory:true)).SingleOrDefault(r => r.Id == id)
        ?? throw new ClinicalRuleException("Reassessment.AccessDenied", "This review is not available to your account.", 403));
    [HttpPost("appointments/{id:int}")]
    [Authorize(Roles = "Doctor,Ceo,Admin,Owner")]
    public Task<IActionResult> CreateRequest(int id, RequestReassessment request) => Execute(() => service.RequestAsync(id, request, Actor, Supervisor));
    [HttpPost("{id:int}/start")]
    [Authorize(Roles = "Senior,Ceo,Admin,Owner")]
    public Task<IActionResult> Start(int id, StartReassessment request) => Execute(() => service.StartAsync(id, request, Actor, Supervisor));
    [HttpPost("{id:int}/complete")]
    [Authorize(Roles = "Senior,Ceo,Admin,Owner")]
    public Task<IActionResult> Complete(int id, CompleteReassessment request) => Execute(() => service.CompleteAsync(id, request, Actor, Supervisor));
    [HttpPost("{id:int}/cancel")]
    public Task<IActionResult> Cancel(int id, CancelReassessment request) => Execute(() => service.CancelAsync(id, request, Actor, Supervisor || User.IsInRole("Receptionist")));
}
