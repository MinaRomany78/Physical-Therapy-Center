using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTCenter.Api.Extensions;
using PTCenter.Application.Contracts.TreatmentPlans;
using PTCenter.Application.Contracts.TreatmentPlanSession;
using PTCenter.Application.Interfaces;

namespace PTCenter.Api.Controllers;

[ApiController]
[Route("api/[controller]/[action]")]
[Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.StaffRead)]
public class TreatmentPlanSessionsController(
    ITreatmentPlanSessionService treatmentPlanSessionService)
    : ControllerBase
{
    private readonly ITreatmentPlanSessionService _treatmentPlanSessionService =
        treatmentPlanSessionService;

    [HttpPost]
    [Authorize(Roles = "Senior,Ceo,Admin,Owner")]
    [ProducesResponseType(
        typeof(TreatmentPlanSessionResponse),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Add(
        CreateTreatmentPlanSessionRequest request)
    {
        var result = await _treatmentPlanSessionService.AddAsync(request);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }

    [HttpGet]
    [ProducesResponseType(
        typeof(List<TreatmentPlanSessionResponse>),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int treatmentPlanId)
    {
        var result =
            await _treatmentPlanSessionService.GetAllAsync(treatmentPlanId);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(
        typeof(TreatmentPlanSessionResponse),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var result =
            await _treatmentPlanSessionService.GetByIdAsync(id);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Senior,Ceo,Admin,Owner")]
    [ProducesResponseType(
        typeof(TreatmentPlanSessionResponse),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        int id,
        UpdateTreatmentPlanSessionRequest request)
    {
        var result =
            await _treatmentPlanSessionService.UpdateAsync(id, request);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Senior,Ceo,Admin,Owner")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var result =
            await _treatmentPlanSessionService.DeleteAsync(id);

        return result.IsSuccess
            ? Ok()
            : result.ToProblem();
    }
}
