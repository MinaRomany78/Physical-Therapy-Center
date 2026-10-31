using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTCenter.Api.Extensions;
using PTCenter.Application.Contracts.TreatmentPlans;
using PTCenter.Application.Interfaces;

namespace PTCenter.Api.Controllers;

[ApiController]
[Route("api/[controller]/[action]")]
[Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.StaffRead)]
public class TreatmentPlansController(
    ITreatmentPlanService treatmentPlanService) : ControllerBase
{
    private readonly ITreatmentPlanService _treatmentPlanService =
        treatmentPlanService;

    [HttpPost]
    [Authorize(Roles = "Senior,Ceo,Admin,Owner")]
    [ProducesResponseType(
        typeof(TreatmentPlanResponse),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Add(
        CreateTreatmentPlanRequest request)
    {
        var result = await _treatmentPlanService.AddAsync(request);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }

    [HttpGet]
    [ProducesResponseType(
        typeof(List<TreatmentPlanResponse>),
        StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll([FromQuery] int? patientId)
    {
        var result = await _treatmentPlanService.GetAllAsync(patientId);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(
        typeof(TreatmentPlanResponse),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var result = await _treatmentPlanService.GetByIdAsync(id);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Senior,Ceo,Admin,Owner")]
    [ProducesResponseType(
        typeof(TreatmentPlanResponse),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        int id,
        UpdateTreatmentPlanRequest request)
    {
        var result = await _treatmentPlanService.UpdateAsync(id, request);

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
        var result = await _treatmentPlanService.DeleteAsync(id);

        return result.IsSuccess
            ? Ok()
            : result.ToProblem();
    }
}
