using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTCenter.Api.Extensions;
using PTCenter.Application.Contracts.Assessments;
using PTCenter.Application.Interfaces;

namespace PTCenter.Api.Controllers;

[ApiController]
[Route("api/[controller]/[action]")]
[Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.StaffRead)]
public class AssessmentsController(
    IAssessmentService assessmentService)
    : ControllerBase
{
    private readonly IAssessmentService _assessmentService =
        assessmentService;

    [HttpPost]
    [Authorize(Roles = "Senior,Ceo,Admin,Owner")]
    [ProducesResponseType(
        typeof(AssessmentResponse),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Add(
        CreateAssessmentRequest request)
    {
        var result =
            await _assessmentService.AddAsync(request);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }

    [HttpGet]
    [ProducesResponseType(
        typeof(List<AssessmentResponse>),
        StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? patientId)
    {
        var result =
            await _assessmentService.GetAllAsync(patientId);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(
        typeof(AssessmentResponse),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var result =
            await _assessmentService.GetByIdAsync(id);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Senior,Ceo,Admin,Owner")]
    [ProducesResponseType(
        typeof(AssessmentResponse),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        int id,
        UpdateAssessmentRequest request)
    {
        var result =
            await _assessmentService.UpdateAsync(id, request);

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
            await _assessmentService.DeleteAsync(id);

        return result.IsSuccess
            ? Ok()
            : result.ToProblem();
    }
}
