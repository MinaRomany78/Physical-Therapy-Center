
using System.Security.Cryptography;
using Mapster;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PTCenter.Application.Contracts.Common;
using PTCenter.Application.Contracts.Patients;
using PTCenter.Application.Interfaces;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Errors;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

public class PatientService(
    ApplicationDbContext context,
    UserManager<ApplicationUser> userManager,
    ILogger<PatientService> logger,
    IPatientUsernameGenerator usernames)
    : IPatientService
{
    private readonly ApplicationDbContext _context = context;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly ILogger<PatientService> _logger = logger;

    // ========================= ADD PATIENT =========================

    public async Task<Result<PatientResponse>> AddAsync(CreatePatientRequest request)
    {
        if (!ValidBirthDate(request.BirthDate))
            return Result.Failure<PatientResponse>(new("Patient.InvalidBirthDate", "Enter a valid birth date that is not in the future.", 400));
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        var operation = request.IdempotencyKey;
        if (operation != null && (string.IsNullOrWhiteSpace(operation) || operation.Length > 100))
            return Result.Failure<PatientResponse>(new("Patient.RegistrationKey", "Invalid registration operation key.", 400));
        var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new {
            Name=request.FullName.Trim(), Phone=request.PhoneNumber.Trim(), request.Gender, request.BirthDate, Email=email,
            request.PaymentType, request.EmergencyContact, request.InsuranceCompanyId, request.InsuranceDetails
        }))));
        for (var attempt=0; attempt<5; attempt++)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await ClinicMutationLock.AcquireAsync(_context);
            if (operation != null)
            {
                var replay = await _context.Patients.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(p=>p.RegistrationOperationKey==operation);
                if (replay != null)
                {
                    if (replay.IsDeleted || replay.RegistrationRequestHash != fingerprint)
                        return Result.Failure<PatientResponse>(new("Patient.RegistrationConflict", "This operation belongs to a different or deleted registration. Review the saved patient before retrying.", 409));
                    return Result.Success((await GetPatientResponseAsync(replay.Id))!);
                }
            }
            if (request.InsuranceCompanyId is int company && !await _context.InsuranceCompanies.AnyAsync(c=>c.Id==company && c.IsActive))
                return Result.Failure<PatientResponse>(PatientErrors.InsuranceCompanyNotFound);
            if (email != null && await _userManager.FindByEmailAsync(email) != null)
                return Result.Failure<PatientResponse>(PatientErrors.PatientAlreadyExists);
            var userName = usernames.Next();
            if (await _userManager.FindByNameAsync(userName) != null) continue;
            var user = new ApplicationUser { UserName=userName, Email=email, PhoneNumber=request.PhoneNumber.Trim(),
                FullName=request.FullName.Trim(), Gender=request.Gender, BirthDate=request.BirthDate, EmailConfirmed=false };
            try
            {
                var created = await _userManager.CreateAsync(user, GeneratePassword());
                if (!created.Succeeded)
                {
                    _context.Entry(user).State = EntityState.Detached;
                    if (created.Errors.Any(e=>e.Code=="DuplicateUserName")) continue;
                    LogIdentityErrors("Create patient", userName, created.Errors);
                    return Result.Failure<PatientResponse>(PatientErrors.RegistrationFailed);
                }
                var role = await _userManager.AddToRoleAsync(user,"Patient");
                if (!role.Succeeded) return Result.Failure<PatientResponse>(PatientErrors.FailedToAssignRole);
                var patient = new Patient { ApplicationUser=user, PaymentType=request.PaymentType, EmergencyContact=request.EmergencyContact,
                    InsuranceCompanyId=request.InsuranceCompanyId, InsuranceDetails=request.InsuranceDetails?.Adapt<InsuranceDetails>(),
                    RegistrationOperationKey=operation, RegistrationRequestHash=operation == null ? null : fingerprint };
                _context.Patients.Add(patient); await _context.SaveChangesAsync();
                // Load the response inside the transaction; a read failure must not encourage a second registration.
                var response = (await GetPatientResponseAsync(patient.Id))!;
                await transaction.CommitAsync(); return Result.Success(response);
            }
            catch (DbUpdateException error) when (UniqueCollision(error))
            {
                await transaction.RollbackAsync(); _context.ChangeTracker.Clear();
                // Identity username uniqueness and the registration key are database-enforced.
                // A concurrent winning operation is recovered at the top of the next attempt.
            }
        }
        return Result.Failure<PatientResponse>(new("Patient.RegistrationConflict", "Registration changed concurrently. Retry the original operation or check the patient list.", 409));
    }

    private static bool UniqueCollision(DbUpdateException error) =>
        error.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 } ||
        error.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true;

    private static bool ValidBirthDate(DateTime birthDate) => birthDate != default &&
        DateOnly.FromDateTime(birthDate) <= DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ClinicCalendar.Zone).DateTime);

    // ========================= GET ALL PATIENTS =========================

    public async Task<Result<PagedResponse<PatientResponse>>> GetAllAsync(
        PatientQueryRequest request)
    {
        request.PageNumber = Math.Max(1, request.PageNumber);
        request.PageSize = Math.Clamp(request.PageSize, 1, 100);

        var query = _context.Patients
            .Include(x => x.ApplicationUser)
            .Include(x => x.InsuranceCompany)
            .Include(x => x.InsuranceDetails)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(x =>
                x.ApplicationUser.FullName.Contains(search) ||
                (x.ApplicationUser.PhoneNumber != null &&
                 x.ApplicationUser.PhoneNumber.Contains(search)) ||
                (x.ApplicationUser.UserName != null &&
                 x.ApplicationUser.UserName.Contains(search)));
        }

        if (!string.IsNullOrWhiteSpace(request.PaymentType))
        {
            query = query.Where(
                x => x.PaymentType == request.PaymentType);
        }

        if (request.InsuranceCompanyId.HasValue)
        {
            query = query.Where(
                x => x.InsuranceCompanyId ==
                     request.InsuranceCompanyId.Value);
        }

        var totalCount = await query.CountAsync();

        var totalPages = (int)Math.Ceiling(
            totalCount / (double)request.PageSize);

        var patients = await query
            .OrderBy(x => x.ApplicationUser.FullName).ThenBy(x => x.Id)
            .Skip((request.PageNumber - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var items = patients
            .Select(patient => MapToResponse(patient))
            .ToList();

        var response = new PagedResponse<PatientResponse>
        {
            Items = items,
            PageNumber = request.PageNumber,
            PageSize = request.PageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
        return Result.Success(response);
    }

    // ========================= GET PATIENT BY ID =========================

    public async Task<Result<PatientResponse>> GetByIdAsync(int id)
    {
        var patient = await _context.Patients
            .Include(x => x.ApplicationUser)
            .Include(x => x.InsuranceCompany)
            .Include(x => x.InsuranceDetails)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (patient is null)
        {
            return Result.Failure<PatientResponse>(
                PatientErrors.PatientNotFound);
        }

        return Result.Success(MapToResponse(patient));
    }

    // ========================= UPDATE PATIENT =========================

    public async Task<Result<PatientResponse>> GetByRegistrationKeyAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
            return Result.Failure<PatientResponse>(new("Patient.RegistrationKey", "Invalid registration operation key.", 400));
        await using var transaction=await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(_context);
        var id=await _context.Patients.Where(p=>p.RegistrationOperationKey==key).Select(p=>(int?)p.Id).SingleOrDefaultAsync();
        if(id==null)return Result.Failure<PatientResponse>(PatientErrors.PatientNotFound);
        var patient=(await GetPatientResponseAsync(id.Value))!;
        await transaction.CommitAsync();
        return Result.Success(patient);
    }

    public async Task<Result<PatientResponse>> UpdateAsync(
        int id,
        UpdatePatientRequest request)
    {
        if (request.BirthDate is DateTime birthDate && !ValidBirthDate(birthDate))
            return Result.Failure<PatientResponse>(new("Patient.InvalidBirthDate", "Enter a valid birth date that is not in the future.", 400));
        var patient = await _context.Patients
            .Include(x => x.InsuranceDetails)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (patient is null)
        {
            return Result.Failure<PatientResponse>(
                PatientErrors.PatientNotFound);
        }

        if (request.InsuranceCompanyId.HasValue)
        {
            var companyExists = await _context.InsuranceCompanies
                .AnyAsync(
                    x => x.Id == request.InsuranceCompanyId.Value && (x.IsActive || patient.InsuranceCompanyId == x.Id));

            if (!companyExists)
            {
                return Result.Failure<PatientResponse>(
                    PatientErrors.InsuranceCompanyNotFound);
            }
        }

        var user = await _userManager.FindByIdAsync(
            patient.ApplicationUserId);

        if (user is null)
        {
            return Result.Failure<PatientResponse>(
                PatientErrors.PatientNotFound);
        }

        var userName = request.UserName?.Trim() ?? user.UserName!;
        var email = request.Email?.Trim() ?? user.Email;

        var existingUserName =
            await _userManager.FindByNameAsync(userName);

        if (existingUserName is not null &&
            existingUserName.Id != user.Id)
        {
            return Result.Failure<PatientResponse>(
                PatientErrors.PatientAlreadyExists);
        }

        var existingEmail = string.IsNullOrEmpty(email) ? null :
            await _userManager.FindByEmailAsync(email);

        if (existingEmail is not null &&
            existingEmail.Id != user.Id)
        {
            return Result.Failure<PatientResponse>(
                PatientErrors.PatientAlreadyExists);
        }

        await using var transaction =
            await _context.Database.BeginTransactionAsync();

        try
        {
            // Update patient-specific fields.
            if (request.PaymentTypeProvided) patient.PaymentType = request.PaymentType;
            if (request.EmergencyContactProvided) patient.EmergencyContact = request.EmergencyContact;
            if (request.InsuranceCompanyIdProvided) patient.InsuranceCompanyId = request.InsuranceCompanyId;

            // Uses InsuranceDetailsResponse as requested.
            if (request.InsuranceDetails is not null)
            {
                patient.InsuranceDetails ??= new InsuranceDetails();

                patient.InsuranceDetails.Status =
                    request.InsuranceDetails.Status;

                patient.InsuranceDetails.CopayPercentage =
                    request.InsuranceDetails.CopayPercentage;

                patient.InsuranceDetails.ApprovedSessions =
                    request.InsuranceDetails.ApprovedSessions;

                patient.InsuranceDetails.MemberId =
                    request.InsuranceDetails.MemberId;

                patient.InsuranceDetails.Employer =
                    request.InsuranceDetails.Employer;
            }
            else if (request.InsuranceDetailsProvided)
            {
                patient.InsuranceDetails = null;
            }

            // Update the linked Identity user.
            user.UserName = userName;
            user.Email = email;
            if (request.PhoneNumber is not null) user.PhoneNumber = request.PhoneNumber.Trim();
            if (request.FullName is not null) user.FullName = request.FullName.Trim();
            if (request.BirthDate.HasValue) user.BirthDate = request.BirthDate.Value;
            if (request.Gender.HasValue) user.Gender = request.Gender.Value;

            var identityResult = await _userManager.UpdateAsync(user);
            if (!identityResult.Succeeded)
            {
                LogIdentityErrors(
                    "Update patient user",
                    userName,
                    identityResult.Errors);

                await transaction.RollbackAsync();

                return Result.Failure<PatientResponse>(
                    PatientErrors.RegistrationFailed);
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            var updatedPatient = await GetByIdAsync(id);

            return updatedPatient;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();

            _logger.LogError(
                ex,
                "Unexpected error while updating patient {PatientId}.",
                id);

            throw;
        }
    }

    // ========================= DELETE PATIENT =========================

    public async Task<Result> DeleteAsync(int id)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(_context);
        var patient = await _context.Patients
            .FirstOrDefaultAsync(x => x.Id == id);

        if (patient is null)
        {
            return Result.Failure(
                PatientErrors.PatientNotFound);
        }

        // A soft deletion also hides related rows through patient query filters.
        // Keep any patient with clinical, financial or document history visible.
        if (await _context.Appointments.IgnoreQueryFilters().AnyAsync(a => a.PatientId == id) ||
            await _context.Assessments.IgnoreQueryFilters().AnyAsync(a => a.PatientId == id) ||
            await _context.Invoices.IgnoreQueryFilters().AnyAsync(i => i.PatientId == id) ||
            await _context.InsuranceDocuments.IgnoreQueryFilters().AnyAsync(d => d.PatientId == id))
            return Result.Failure(new("Patient.LinkedHistory", "This patient has linked history and cannot be deleted.", 409));

        try
        {
            _context.Patients.Remove(patient);
            await _context.SaveChangesAsync();

            // Patients use soft deletion. Retain the Identity principal required by
            // historical records; reception profile deletion must not delete staff accounts.
            await transaction.CommitAsync();
            return Result.Success();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();

            _logger.LogError(
                ex,
                "Unexpected error while deleting patient {PatientId}.",
                id);

            throw;
        }
    }

    // ========================= MAPPING =========================

    private async Task<PatientResponse?> GetPatientResponseAsync(int id)
    {
        var patient = await _context.Patients
            .Include(x => x.ApplicationUser)
            .Include(x => x.InsuranceCompany)
            .Include(x => x.InsuranceDetails)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        return patient is null ? null : MapToResponse(patient);
    }

    private static PatientResponse MapToResponse(Patient patient)
    {
        var user = patient.ApplicationUser;

        return new PatientResponse
        {
            Id = patient.Id,
            ApplicationUserId = patient.ApplicationUserId,
            FullName = user.FullName,
            Email = user.Email ?? string.Empty,
            PhoneNumber = user.PhoneNumber ?? string.Empty,
            Gender = user.Gender,
            BirthDate = user.BirthDate,
            PaymentType = patient.PaymentType,
            EmergencyContact = patient.EmergencyContact,
            InsuranceCompanyId = patient.InsuranceCompanyId,
            InsuranceCompanyName = patient.InsuranceCompany?.Name,

            // Map the entity to InsuranceDetailsResponse.
            InsuranceDetails = patient.InsuranceDetails is null
                ? null
                : patient.InsuranceDetails
                    .Adapt<InsuranceDetailsResponse>(),

            CreatedAt = patient.CreatedAt
        };
    }

    // ========================= IDENTITY LOGGING =========================

    private void LogIdentityErrors(
        string operation,
        string userName,
        IEnumerable<IdentityError> errors)
    {
        foreach (var error in errors)
        {
            _logger.LogWarning(
                "Identity operation {Operation} failed for {UserName}. " +
                "Code: {Code}. Description: {Description}",
                operation,
                userName,
                error.Code,
                error.Description);
        }
    }

    // ========================= PASSWORD GENERATION =========================

    private static string GeneratePassword(int length = 12)
    {
        if (length < 4)
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "Password length must be at least 4.");

        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string digits = "0123456789";
        const string special = "!@#$%&*";

        var allCharacters = upper + lower + digits + special;

        var password = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            special[RandomNumberGenerator.GetInt32(special.Length)]
        };

        while (password.Count < length)
        {
            password.Add(
                allCharacters[
                    RandomNumberGenerator.GetInt32(allCharacters.Length)]);
        }

        // Cryptographically secure shuffle.
        for (var i = password.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (password[i], password[j]) = (password[j], password[i]);
        }

        return new string(password.ToArray());
    }
}
