using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Abstractions;
using PTCenter.Application.Contracts.Appointments;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Application.Contracts.Common;
using PTCenter.Application.Interfaces;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Domain.Errors;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

public class AppointmentService(ApplicationDbContext context, BookingService bookings)
    : IAppointmentService
{
    private readonly ApplicationDbContext _context = context;

    public async Task<Result<PagedResponse<AppointmentResponse>>> GetAllAsync(
        AppointmentQueryRequest request)
    {
        if (request.PageNumber < 1)
            request.PageNumber = 1;

        if (request.PageSize < 1)
            request.PageSize = 10;

        var query = _context.Appointments
            .Include(x => x.Patient)
                .ThenInclude(p => p.ApplicationUser)
            .Include(x => x.Doctor)
            .Include(x => x.Room)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();

            query = query.Where(x =>
                x.Patient.ApplicationUser.FullName.Contains(search) ||
                (x.Patient.ApplicationUser.PhoneNumber != null &&
                 x.Patient.ApplicationUser.PhoneNumber.Contains(search)));
        }

        if (!string.IsNullOrWhiteSpace(request.DoctorId))
            query = query.Where(x => x.DoctorId == request.DoctorId || x.SeniorId == request.DoctorId);

        if (request.RoomId.HasValue)
            query = query.Where(x => x.RoomId == request.RoomId.Value);

        if (request.Date.HasValue)
        {
            var date = request.Date.Value.Date;
            var nextDate = date.AddDays(1);

            query = query.Where(x =>
                x.StartTime >= date && x.StartTime < nextDate);
        }

        if (request.Status.HasValue)
            query = query.Where(x => x.Status == request.Status.Value);

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(
            totalCount / (double)request.PageSize);

        var items = await query
            .OrderBy(x => x.StartTime)
            .Skip((request.PageNumber - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new AppointmentResponse
            {
                Id = x.Id,
            ActualCheckIn = x.ActualCheckIn, ActualCheckOut = x.ActualCheckOut, TreatmentStartedAt = x.TreatmentStartedAt, TreatmentEndedAt = x.TreatmentEndedAt,
                PatientId = x.PatientId,
                AssessmentCompleted = _context.Assessments.Any(a => a.AppointmentId == x.Id && a.CompletedAt != null),
                PatientName = x.Patient.ApplicationUser.FullName,
                DoctorId = x.DoctorId,
                DoctorName = x.Doctor != null ? x.Doctor.FullName : _context.Users.Where(u => u.Id == x.SeniorId).Select(u => u.FullName).FirstOrDefault(),
                SeniorId = x.SeniorId,
                RoomId = x.RoomId,
                RoomName = x.Room != null ? x.Room.Name : null,
                TreatmentPlanSessionId = x.TreatmentPlanSessionId,
                AppointmentType = x.AppointmentType,
                StartTime = x.StartTime,
                EndTime = x.EndTime,
                Status = x.Status,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync();

        return Result.Success(new PagedResponse<AppointmentResponse>
        {
            Items = items,
            PageNumber = request.PageNumber,
            PageSize = request.PageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        });
    }

    public async Task<Result<AppointmentResponse>> GetByIdAsync(int id)
    {
        var appointment = await _context.Appointments
            .Include(x => x.Patient)
                .ThenInclude(p => p.ApplicationUser)
            .Include(x => x.Doctor)
            .Include(x => x.Room)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (appointment is null)
            return Result.Failure<AppointmentResponse>(
                AppointmentErrors.AppointmentNotFound);

        var response = MapResponse(appointment);
        if (response.DoctorName == null && appointment.SeniorId != null) response.DoctorName = await _context.Users.Where(u => u.Id == appointment.SeniorId).Select(u => u.FullName).SingleOrDefaultAsync();
        response.AssessmentCompleted = await _context.Assessments.AnyAsync(a => a.AppointmentId == id && a.CompletedAt != null);
        return Result.Success(response);
    }

    public async Task<Result<AppointmentResponse>> AddAsync(CreateAppointmentRequest request)
    {
        try
        {
            var planId = request.TreatmentPlanSessionId.HasValue
                ? await _context.TreatmentPlanSessions.Where(s => s.Id == request.TreatmentPlanSessionId).Select(s => (int?)s.TreatmentPlanId).SingleOrDefaultAsync() : null;
            var receipt = await bookings.CreateAsync(new BookingRequest {
                IdempotencyKey = request.IdempotencyKey ?? "", PatientId = request.PatientId,
                ClinicianId = request.DoctorId ?? request.SeniorId ?? "", RoomId = request.RoomId ?? 0,
                TreatmentPlanId = planId, StartsAt = [new DateTimeOffset(DateTime.SpecifyKind(request.StartTime, DateTimeKind.Utc))],
                AssessmentCharge = request.AssessmentCharge, UnitPrice = request.UnitPrice
            });
            return await GetByIdAsync(receipt.Appointments[0].Id);
        }
        catch (BookingRuleException e) { return Result.Failure<AppointmentResponse>(new(e.Code, e.Message, e.Status)); }
        catch (Microsoft.Data.SqlClient.SqlException e) when(e.Number is 1205 or 1222 or 51001) { return Result.Failure<AppointmentResponse>(new("Booking.ConcurrentChange","Refresh clinic availability before retrying the original action.",409)); }
        catch (DbUpdateConcurrencyException) { return Result.Failure<AppointmentResponse>(new("Booking.ConcurrentChange","The appointment changed. Refresh before saving.",409)); }
    }

    public async Task<Result<AppointmentResponse>> UpdateAsync(int id, UpdateAppointmentRequest request)
    {
        try
        {
            var current = await _context.Appointments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
            if (current == null) return Result.Failure<AppointmentResponse>(AppointmentErrors.AppointmentNotFound);
            if (current.Status != request.Status)
                return Result.Failure<AppointmentResponse>(new("Booking.SeparateStatusAction", "Use the explicit check-in/check-out action to change status.", 400));
            await bookings.RescheduleAsync(id, new RescheduleBookingRequest {
                StartTime = new DateTimeOffset(DateTime.SpecifyKind(request.StartTime, DateTimeKind.Utc)),
                ClinicianId = request.DoctorId ?? request.SeniorId ?? "", RoomId = request.RoomId ?? 0
            });
            return await GetByIdAsync(id);
        }
        catch (BookingRuleException e) { return Result.Failure<AppointmentResponse>(new(e.Code, e.Message, e.Status)); }
        catch (Microsoft.Data.SqlClient.SqlException e) when(e.Number is 1205 or 1222 or 51001) { return Result.Failure<AppointmentResponse>(new("Booking.ConcurrentChange","Refresh clinic availability before retrying the original action.",409)); }
        catch (DbUpdateConcurrencyException) { return Result.Failure<AppointmentResponse>(new("Booking.ConcurrentChange","The appointment changed. Refresh before saving.",409)); }
    }

    public Task<Result> DeleteAsync(int id) => UpdateStatusAsync(id, nameof(AppointmentStatus.Cancelled));

    public async Task<Result> UpdateStatusAsync(int id, string status)
    {
        if (!Enum.TryParse<AppointmentStatus>(status.Replace(" ", ""), true, out var parsed) || !Enum.IsDefined(parsed))
            return Result.Failure(AppointmentErrors.InvalidStatus);
        try { await bookings.TransitionAsync(id, parsed); return Result.Success(); }
        catch (BookingRuleException e) { return Result.Failure(new(e.Code, e.Message, e.Status)); }
    }

    private async Task<PTCenter.Domain.Errors.Error?> ValidateBookingAsync(
        string? doctorId,
        int? roomId,
        DateTime startTime,
        DateTime endTime,
        int? excludeAppointmentId = null)
    {
        if (!string.IsNullOrWhiteSpace(doctorId))
        {
            var doctorExists = await _context.Users
                .AnyAsync(x => x.Id == doctorId);

            if (!doctorExists)
                return AppointmentErrors.DoctorNotFound;
        }

        if (roomId.HasValue)
        {
            var room = await _context.Rooms
                .FirstOrDefaultAsync(x => x.Id == roomId.Value);

            if (room is null)
                return AppointmentErrors.RoomNotFound;

            if (!room.IsActive)
                return AppointmentErrors.RoomInactive;

            var roomConflict = await _context.Appointments.AnyAsync(x =>
                x.RoomId == roomId.Value &&
                x.Id != excludeAppointmentId &&
                x.Status != AppointmentStatus.Cancelled &&
                x.Status != AppointmentStatus.Completed &&
                x.Status != AppointmentStatus.NoShow &&
                x.StartTime < endTime &&
                x.EndTime > startTime);

            if (roomConflict)
                return AppointmentErrors.RoomConflict;
        }

        if (!string.IsNullOrWhiteSpace(doctorId))
        {
            var doctorConflict = await _context.Appointments.AnyAsync(x =>
                x.DoctorId == doctorId &&
                x.Id != excludeAppointmentId &&
                x.Status != AppointmentStatus.Cancelled &&
                x.Status != AppointmentStatus.Completed &&
                x.Status != AppointmentStatus.NoShow &&
                x.StartTime < endTime &&
                x.EndTime > startTime);

            if (doctorConflict)
                return AppointmentErrors.DoctorConflict;
        }

        return null;
    }

    private static AppointmentResponse MapResponse(Appointment x)
    {
        return new AppointmentResponse
        {
            Id = x.Id,
            ActualCheckIn = x.ActualCheckIn, ActualCheckOut = x.ActualCheckOut, TreatmentStartedAt = x.TreatmentStartedAt, TreatmentEndedAt = x.TreatmentEndedAt,
            PatientId = x.PatientId,
            PatientName = x.Patient.ApplicationUser.FullName,
            DoctorId = x.DoctorId,
            DoctorName = x.Doctor?.FullName,
            SeniorId = x.SeniorId,
            RoomId = x.RoomId,
            RoomName = x.Room?.Name,
            TreatmentPlanSessionId = x.TreatmentPlanSessionId,
            AppointmentType = x.AppointmentType,
            StartTime = x.StartTime,
            EndTime = x.EndTime,
            Status = x.Status,
            CreatedAt = x.CreatedAt
        };
    }
}
