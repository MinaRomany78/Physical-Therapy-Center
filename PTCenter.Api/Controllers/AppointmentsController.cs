using Microsoft.AspNetCore.Mvc;
using PTCenter.Application.Contracts.Appointments;
using PTCenter.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace PTCenter.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = "Receptionist,Senior,Doctor,Ceo,Admin,Owner")]
public class AppointmentsController(
    IAppointmentService appointmentService) : ControllerBase
{
    private readonly IAppointmentService _appointmentService =
        appointmentService;

    [HttpGet("GetAll")]
    public async Task<IActionResult> GetAll(
        [FromQuery] AppointmentQueryRequest request)
    {
        var result = await _appointmentService.GetAllAsync(request);

        return result.IsSuccess
            ? Ok(result.Value)
            : StatusCode(
                result.Error.StatusCode ?? 400,
                result.Error);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var result = await _appointmentService.GetByIdAsync(id);

        return result.IsSuccess
            ? Ok(result.Value)
            : StatusCode(
                result.Error.StatusCode ?? 400,
                result.Error);
    }

    [HttpPost]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Operations)]
    public async Task<IActionResult> Create(
        [FromBody] CreateAppointmentRequest request)
    {
        var result = await _appointmentService.AddAsync(request);

        return result.IsSuccess
            ? Ok(result.Value)
            : StatusCode(
                result.Error.StatusCode ?? 400,
                result.Error);
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Operations)]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpdateAppointmentRequest request)
    {
        var result = await _appointmentService.UpdateAsync(id, request);

        return result.IsSuccess
            ? Ok(result.Value)
            : StatusCode(
                result.Error.StatusCode ?? 400,
                result.Error);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Operations)]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _appointmentService.DeleteAsync(id);

        return result.IsSuccess
            ? NoContent()
            : StatusCode(
                result.Error.StatusCode ?? 400,
                result.Error);
    }

    [HttpPatch("{id:int}/status")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Operations)]
    public async Task<IActionResult> UpdateStatus(
        int id,
        [FromBody] string status)
    {
        var result = await _appointmentService.UpdateStatusAsync(id, status);

        return result.IsSuccess
            ? NoContent()
            : StatusCode(
                result.Error.StatusCode ?? 400,
                result.Error);
    }
}
