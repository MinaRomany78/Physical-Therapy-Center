using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PTCenter.Api.Extensions;
using PTCenter.Application.Contracts.InsuranceCompany;
using PTCenter.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Ceo;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Api.Controllers;

[ApiController]
[Route("api/[controller]/[action]")]
[Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.StaffRead)]
public class InsuranceCompaniesController(
    IInsuranceCompanyService insuranceCompanyService) : ControllerBase
{
    private readonly IInsuranceCompanyService _insuranceCompanyService = insuranceCompanyService;

    [HttpGet]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Administration)]
    public async Task<IActionResult> Search([FromQuery] CompanyQuery q, [FromServices] ApplicationDbContext db, CancellationToken ct)
    {
        var rows = db.InsuranceCompanies.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Search)) rows = rows.Where(x => x.Name.Contains(q.Search.Trim()));
        if (q.Active != null) rows = rows.Where(x => x.IsActive == q.Active);
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderBy(x => x.Name).ThenBy(x => x.Id).Skip((q.Page-1)*q.PageSize).Take(q.PageSize)
            .Select(x => new { x.Id, x.Name, x.PortalUrl, x.Hotline, x.IsActive, x.UpdatedAt, Version = x.UpdatedAt == null ? "new" : x.UpdatedAt.Value.ToString("O"),
                LinkedPatients = db.Patients.IgnoreQueryFilters().Count(p => p.InsuranceCompanyId == x.Id) }).ToListAsync(ct);
        return Ok(new { items, total, q.Page, q.PageSize });
    }


    [HttpPost]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Administration)]
    [ProducesResponseType(typeof(InsuranceCompanyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Add(CreateInsuranceCompanyRequest request)
    {
        var result = await _insuranceCompanyService.AddAsync(request);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }


    [HttpGet]
    [ProducesResponseType(typeof(List<InsuranceCompanyResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll()
    {
        var result = await _insuranceCompanyService.GetAllAsync();

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }


    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(InsuranceCompanyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var result = await _insuranceCompanyService.GetByIdAsync(id);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }


    [HttpPut("{id:int}")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Administration)]
    [ProducesResponseType(typeof(InsuranceCompanyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        int id,
        UpdateInsuranceCompanyRequest request)
    {
        var result = await _insuranceCompanyService.UpdateAsync(id, request);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.ToProblem();
    }


    [HttpDelete("{id:int}")]
    [Authorize(Roles = PTCenter.Api.Authorization.ReceptionPermissions.Administration)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _insuranceCompanyService.DeleteAsync(id);

        return result.IsSuccess
            ? Ok()
            : result.ToProblem();
    }
}
