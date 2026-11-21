using Mapster;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Abstractions;
using PTCenter.Application.Contracts.Authentication;
using PTCenter.Application.Contracts.Users;
using PTCenter.Application.Interfaces;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Errors;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

public class UserService(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ApplicationDbContext context) : IUserService
{
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly RoleManager<IdentityRole> _roleManager = roleManager;
    private readonly ApplicationDbContext _context = context;

    private static bool ValidWorkingHours(List<StaffWorkingHours>? hours, List<string> roles)
    {
        if (hours == null || hours.Count == 0) return true;
        if (hours.Count > 28 || !roles.Any(role => role != "Patient") ||
            hours.Any(window => window.DayOfWeek is < 0 or > 6 || window.StartTime >= window.EndTime)) return false;
        return hours.GroupBy(window => window.DayOfWeek).All(group => {
            var ordered = group.OrderBy(window => window.StartTime).ToArray();
            return Enumerable.Range(1, ordered.Length - 1).All(index => ordered[index - 1].EndTime <= ordered[index].StartTime);
        });
    }

    private async Task SaveWorkingHoursAsync(string userId, List<StaffWorkingHours>? hours)
    {
        if (hours == null) return; // Older callers that omit this field preserve the saved schedule.
        var existing = await _context.DoctorSchedules.Where(window => window.DoctorId == userId).ToListAsync();
        _context.DoctorSchedules.RemoveRange(existing.Where(window => !hours.Any(h => h.DayOfWeek == window.DayOfWeek && h.StartTime == window.StartTime && h.EndTime == window.EndTime)));
        foreach (var hour in hours.Where(hour => !existing.Any(window => window.DayOfWeek == hour.DayOfWeek && window.StartTime == hour.StartTime && window.EndTime == hour.EndTime)))
            _context.DoctorSchedules.Add(new DoctorSchedule { DoctorId = userId, DayOfWeek = hour.DayOfWeek, StartTime = hour.StartTime, EndTime = hour.EndTime });
        await _context.SaveChangesAsync();
    }

    private Task<List<StaffWorkingHours>> WorkingHoursAsync(string userId) => _context.DoctorSchedules.AsNoTracking()
        .Where(window => window.DoctorId == userId).OrderBy(window => window.DayOfWeek).ThenBy(window => window.StartTime)
        .Select(window => new StaffWorkingHours(window.DayOfWeek, window.StartTime, window.EndTime)).ToListAsync();

    public async Task<Result<UserProfileResponse>> GetProfileAsync(string userId)
    {
        if (await _userManager.FindByIdAsync(userId) is not { } user)
            return Result.Failure<UserProfileResponse>(UserErrors.UserNotFound);

        var roles = await _userManager.GetRolesAsync(user);

        var userMapped = user.Adapt<UserProfileResponse>();
        userMapped.Roles = roles;

        return Result.Success(userMapped);
    }

    public async Task<Result<List<UserResponse>>> GetAllAsync()
    {
        var users = await _userManager.Users.ToListAsync();
        var schedules = (await _context.DoctorSchedules.AsNoTracking().ToListAsync()).ToLookup(window => window.DoctorId);

        var userResponses = new List<UserResponse>();

        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);

            var userResponse = user.Adapt<UserResponse>();
            userResponse.Roles = roles;
            userResponse.WorkingHours = schedules[user.Id].OrderBy(window => window.DayOfWeek).ThenBy(window => window.StartTime)
                .Select(window => new StaffWorkingHours(window.DayOfWeek, window.StartTime, window.EndTime)).ToList();

            userResponses.Add(userResponse);
        }

        return Result.Success(userResponses);
    }

    public async Task<Result> ChangePasswordAsync(
        string userId,
        ChangePasswordRequest request)
    {
        if (await _userManager.FindByIdAsync(userId) is not { } user)
            return Result.Failure(UserErrors.UserNotFound);

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);

        var result = await _userManager.ResetPasswordAsync(
            user,
            token,
            request.NewPassword);

        if (result.Succeeded)
            return Result.Success();

        var error = result.Errors.First();

        return Result.Failure(
            new Error(
                error.Code,
                error.Description,
                StatusCodes.Status400BadRequest));
    }

    public async Task<Result<UserResponse>> AddAsync(CreateUserRequest request)
    {
        request.UserName = request.UserName?.Trim() ?? ""; request.FullName = request.FullName?.Trim() ?? "";
        if (request.BirthDate == DateTime.MinValue || request.BirthDate.Date > TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ClinicCalendar.Zone).Date)
            return Result.Failure<UserResponse>(new Error("Staff.InvalidBirthDate", "Enter a valid birth date that is not in the future.", 400));
        if (request.UserName.Length == 0 || request.FullName.Length == 0 || request.Roles == null || request.Roles.Count == 0)
            return Result.Failure<UserResponse>(new Error("Staff.Required", "Name, username and role are required.", 400));
        if (!ValidWorkingHours(request.WorkingHours, request.Roles)) return Result.Failure<UserResponse>(UserErrors.InvalidWorkingHours);
        // Check duplicate username
        if (await _context.Users.AnyAsync(x => x.UserName == request.UserName))
        {
            return Result.Failure<UserResponse>(
                UserErrors.DuplicatedUserName);
        }

        // Validate roles
        var allowedRoles = await _roleManager.Roles
            .Select(role => role.Name)
            .Where(role => role != null)
            .ToListAsync();

        if (request.Roles.Except(allowedRoles).Any())
        {
            return Result.Failure<UserResponse>(
                UserErrors.InvalidRoles);
        }

        await using var transaction = await _context.Database.BeginTransactionAsync();
        // Create user explicitly
        // This fixes the FullName NULL problem
        var user = new ApplicationUser
        {
            UserName = request.UserName,
            FullName = request.FullName,
            Gender = request.Gender,
            BirthDate = request.BirthDate

        };
        user.EmailConfirmed = true;
        // Create Identity user
        var result = await _userManager.CreateAsync(
            user,
            request.Password);

        if (!result.Succeeded)
        {
            var error = result.Errors.First();

            return Result.Failure<UserResponse>(
                new Error(
                    error.Code,
                    error.Description,
                    StatusCodes.Status400BadRequest));
        }

        // Add roles
        var roleResult = await _userManager.AddToRolesAsync(
            user,
            request.Roles);

        if (!roleResult.Succeeded)
        {
            var error = roleResult.Errors.First();

            return Result.Failure<UserResponse>(
                new Error(
                    error.Code,
                    error.Description,
                    StatusCodes.Status400BadRequest));
        }
       

        // Map response
        var response = user.Adapt<UserResponse>();

        response.Roles = request.Roles;
        await SaveWorkingHoursAsync(user.Id, request.WorkingHours);
        response.WorkingHours = await WorkingHoursAsync(user.Id);
        await transaction.CommitAsync();
        return Result.Success(response);
    }
    public async Task<Result<UserResponse>> UpdateAsync(string userId, UpdateUserRequest request)
    {
        request.UserName = request.UserName?.Trim() ?? ""; request.FullName = request.FullName?.Trim() ?? "";
        if (request.BirthDate == DateTime.MinValue || request.BirthDate.Date > TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ClinicCalendar.Zone).Date)
            return Result.Failure<UserResponse>(new Error("Staff.InvalidBirthDate", "Enter a valid birth date that is not in the future.", 400));
        if (request.UserName.Length == 0 || request.FullName.Length == 0 || request.Roles == null || request.Roles.Count == 0)
            return Result.Failure<UserResponse>(new Error("Staff.Required", "Name, username and role are required.", 400));
        if (!ValidWorkingHours(request.WorkingHours, request.Roles)) return Result.Failure<UserResponse>(UserErrors.InvalidWorkingHours);
        // Find user
        if (await _userManager.FindByIdAsync(userId) is not { } user)
        { return Result.Failure<UserResponse>(UserErrors.UserNotFound); }
        // Check duplicate username
        var duplicateUserName = await _context.Users.AnyAsync(x => x.UserName == request.UserName && x.Id != userId);
        if (request.Version != null && request.Version != user.ConcurrencyStamp)
            return Result.Failure<UserResponse>(new Error("Ceo.StaffConflict", "Staff details changed. Reload before saving.", 409));
        if (duplicateUserName) { return Result.Failure<UserResponse>(UserErrors.DuplicatedUserName); }
        // Validate roles
        var allowedRoles = await _roleManager.Roles.Select(role => role.Name).Where(role => role != null)
            .ToListAsync();
        if (request.Roles.Except(allowedRoles).Any())
        { return Result.Failure<UserResponse>(UserErrors.InvalidRoles); }
        await using var transaction = await _context.Database.BeginTransactionAsync();
        // Update user properties
        await ClinicMutationLock.AcquireAsync(_context);
        try { await StaffSafety.AssertFutureWorkAsync(_context, userId, DateTime.UtcNow, request.Roles, request.WorkingHours); }
        catch (BookingRuleException e) { return Result.Failure<UserResponse>(new Error(e.Code, e.Message, e.Status)); }
        user.UserName = request.UserName;
        user.FullName = request.FullName;
        user.Gender = request.Gender;
        user.BirthDate = request.BirthDate;
        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            var error = updateResult.Errors.First();
            return Result.Failure<UserResponse>(new Error(error.Code, error.Description, StatusCodes.Status400BadRequest));
        }
        // Get current roles
        var currentRoles = await _userManager.GetRolesAsync(user);
        // Remove old roles
        var removeRolesResult = await _userManager.RemoveFromRolesAsync(user, currentRoles);
        if (!removeRolesResult.Succeeded)
        {
            var error = removeRolesResult.Errors.First();
            return Result.Failure<UserResponse>(new Error(error.Code, error.Description, StatusCodes.Status400BadRequest));
        }
        var addRolesResult = await _userManager.AddToRolesAsync(user, request.Roles);
        if (!addRolesResult.Succeeded)
        {
            var error = addRolesResult.Errors.First();
            return Result.Failure<UserResponse>(new Error(error.Code, error.Description, StatusCodes.Status400BadRequest));
        }
        var response = user.Adapt<UserResponse>();
        response.Roles = request.Roles;
        await SaveWorkingHoursAsync(user.Id, request.WorkingHours);
        response.WorkingHours = await WorkingHoursAsync(user.Id);
        await transaction.CommitAsync();
        return Result.Success(response);
    }
    public async Task<Result> DeleteAsync(string userId)
    {
        if (await _userManager.FindByIdAsync(userId) is not { } user)
        { return Result.Failure(UserErrors.UserNotFound); }
        // Legacy clients must use the explicit, reviewed deactivation endpoint. Never delete Identity authors.
        return Result.Failure(new Error("Staff.DeactivationRequired", "Staff identities are retained. Use the staff deactivation action after reviewing future appointments.", 409));
    }
}

