using Mapster;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Abstractions;
using PTCenter.Application.Contracts.InsuranceCompany;
using PTCenter.Application.Interfaces;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Errors;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

public class InsuranceCompanyService(ApplicationDbContext context) : IInsuranceCompanyService
{
    private readonly ApplicationDbContext _context = context;


    public async Task<Result<InsuranceCompanyResponse>> AddAsync(CreateInsuranceCompanyRequest request)
    {
        await using var tx = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(_context);
        request.Name = request.Name?.Trim() ?? "";
        if (request.Name.Length == 0) return Result.Failure<InsuranceCompanyResponse>(new Error("InsuranceCompany.Required", "Company name is required.", 400));
        var companyExists = await _context.InsuranceCompanies
            .AnyAsync(x => x.Name.ToUpper() == request.Name.ToUpper());

        if (companyExists)
            return Result.Failure<InsuranceCompanyResponse>(
                InsuranceCompanyErrors.InsuranceCompanyAlreadyExists);


        var company = request.Adapt<InsuranceCompany>();

        await _context.InsuranceCompanies.AddAsync(company);

        await _context.SaveChangesAsync();
        await tx.CommitAsync();
        var response = company.Adapt<InsuranceCompanyResponse>();

        return Result.Success(response);
    }


    public async Task<Result<List<InsuranceCompanyResponse>>> GetAllAsync()
    {
        var companies = await _context.InsuranceCompanies
            .AsNoTracking()
            .ToListAsync();


        var response = companies.Adapt<List<InsuranceCompanyResponse>>();

        return Result.Success(response);
    }


    public async Task<Result<InsuranceCompanyResponse>> GetByIdAsync(int id)
    {
        var company = await _context.InsuranceCompanies
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);


        if (company is null)
            return Result.Failure<InsuranceCompanyResponse>(
                InsuranceCompanyErrors.InsuranceCompanyNotFound);


        var response = company.Adapt<InsuranceCompanyResponse>();

        return Result.Success(response);
    }


    public async Task<Result<InsuranceCompanyResponse>> UpdateAsync(
        int id,
        UpdateInsuranceCompanyRequest request)
    {
        await using var tx = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(_context);
        request.Name = request.Name?.Trim() ?? "";
        if (request.Name.Length == 0) return Result.Failure<InsuranceCompanyResponse>(new Error("InsuranceCompany.Required", "Company name is required.", 400));
        var company = await _context.InsuranceCompanies
            .FirstOrDefaultAsync(x => x.Id == id);


        if (company is null)
            return Result.Failure<InsuranceCompanyResponse>(
                InsuranceCompanyErrors.InsuranceCompanyNotFound);


        if (request.Version != null && request.Version != (company.UpdatedAt?.ToString("O") ?? "new"))
            return Result.Failure<InsuranceCompanyResponse>(new Error("InsuranceCompany.Conflict", "Company settings changed. Reload before saving.", 409));
        var nameExists = await _context.InsuranceCompanies
            .AnyAsync(x => x.Name.ToUpper() == request.Name.ToUpper() && x.Id != id);


        if (nameExists)
            return Result.Failure<InsuranceCompanyResponse>(
                InsuranceCompanyErrors.InsuranceCompanyAlreadyExists);



        company.Name = request.Name;
        company.PortalUrl = request.PortalUrl;
        company.IsActive = request.IsActive;
        company.Hotline = request.Hotline;
        await _context.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetByIdAsync(id);
    }


    public async Task<Result> DeleteAsync(int id)
    {
        var company = await _context.InsuranceCompanies
            .FirstOrDefaultAsync(x => x.Id == id);


        if (company is null)
            return Result.Failure(
                InsuranceCompanyErrors.InsuranceCompanyNotFound);


        if (await _context.Patients.IgnoreQueryFilters().AnyAsync(p => p.InsuranceCompanyId == id) ||
            await _context.TreatmentCourseAgreements.IgnoreQueryFilters().AnyAsync(a => a.InsuranceCompanyId == id) ||
            await _context.InsuranceClaims.IgnoreQueryFilters().AnyAsync(c => c.InsuranceCompanyId == id) ||
            await _context.InsuranceChequeReceipts.IgnoreQueryFilters().AnyAsync(c => c.InsuranceCompanyId == id))
            return Result.Failure(new Error("InsuranceCompany.LinkedHistory", "This company has linked history. Deactivate it instead; existing agreements, claims and receipts are retained.", 409));
        company.IsActive = false; // Retain even an unused company as configuration history.


        await _context.SaveChangesAsync();


        return Result.Success();
    }
}
