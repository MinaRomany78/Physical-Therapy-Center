using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Common;
using PTCenter.Application.Contracts.Receptionist;
using PTCenter.Application.Interfaces;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

public class ReceptionistService(ApplicationDbContext context, BookingService bookings, TimeProvider? clock = null) : IReceptionistService
{
    private readonly ApplicationDbContext _context = context;

    public async Task<Result<ReceptionistDashboardStatsResponse>> GetDashboardStatsAsync()
    {
        var (dayStart, dayEnd) = ClinicCalendar.UtcDay((clock ?? TimeProvider.System).GetUtcNow());

        var todaysSessionsQuery = _context.Appointments
            .Where(x => x.StartTime >= dayStart && x.StartTime < dayEnd);

        var todaysSessionsCount = await todaysSessionsQuery.CountAsync();

        var completedSessionsCount = await todaysSessionsQuery
            .Where(x => x.Status == AppointmentStatus.Completed)
            .CountAsync();

        var pendingPaymentsCount = await _context.Invoices
            .Where(x => x.Status == InvoiceStatus.Pending)
            .CountAsync();

        var invoicesToday = await _context.Invoices
            .Where(x => x.CreatedAt >= dayStart && x.CreatedAt < dayEnd)
            .ToListAsync();

        var totalExpected = invoicesToday.Sum(x => x.TotalAmount);
        var receipts = await _context.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.Paid && p.PaymentDate >= dayStart && p.PaymentDate < dayEnd)
            .Select(p => new { p.Amount, p.PayerType, p.PaymentMethod }).ToListAsync();
        var totalCollected = receipts.Sum(p => p.Amount);
        var insurerCollected = receipts.Where(p => p.PayerType is "Insurer" or "Insurance" || p.PaymentMethod == PaymentMethod.Insurance).Sum(p => p.Amount);

        double collectionRate = totalExpected > 0 ? (double)(totalCollected / totalExpected) * 100 : 0;

        var response = new ReceptionistDashboardStatsResponse
        {
            TodaysSessions = todaysSessionsCount,
            CompletedSessions = completedSessionsCount,
            PendingPayments = pendingPaymentsCount,
            TotalExpectedToday = totalExpected,
            TotalCollectedToday = totalCollected,
            PatientCollectedToday = totalCollected - insurerCollected,
            InsurerCollectedToday = insurerCollected,
            CollectionRate = Math.Round(collectionRate, 2)
        };

        return Result.Success(response);
    }

    public async Task<Result<PagedResponse<TodaySessionResponse>>> GetTodaySessionsAsync(TodaySessionsQueryRequest request)
    {
        if (request.PageNumber < 1) request.PageNumber = 1;
        if (request.PageSize < 1) request.PageSize = 10;

        var (dayStart, dayEnd) = ClinicCalendar.UtcDay((clock ?? TimeProvider.System).GetUtcNow());

        var query = _context.Appointments
            .Include(x => x.Patient)
                .ThenInclude(p => p.ApplicationUser)
            .Include(x => x.Doctor)
            .Include(x => x.Room)
            .Include(x => x.TreatmentPlanSession)
            .Where(x => x.StartTime >= dayStart && x.StartTime < dayEnd)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(x => x.Patient.ApplicationUser.FullName.Contains(search) ||
                                     x.Patient.ApplicationUser.PhoneNumber!.Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(request.DoctorId))
        {
            query = query.Where(x => x.DoctorId == request.DoctorId);
        }

        if (request.RoomId.HasValue)
        {
            query = query.Where(x => x.RoomId == request.RoomId.Value);
        }

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);

        var sessions = await query
            .OrderBy(x => x.StartTime)
            .Skip((request.PageNumber - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new TodaySessionResponse
            {
                Id = x.Id,
                PatientId = x.PatientId, DoctorId = x.DoctorId, SeniorId = x.SeniorId, RoomId = x.RoomId,
                AppointmentType = x.AppointmentType, StartTime = x.StartTime, EndTime = x.EndTime,
                ActualCheckIn = x.ActualCheckIn, ActualCheckOut = x.ActualCheckOut, TreatmentStartedAt = x.TreatmentStartedAt, TreatmentEndedAt = x.TreatmentEndedAt,
                Time = x.StartTime.ToString("hh:mm tt"),
                PatientName = x.Patient.ApplicationUser.FullName,
                SessionNumber = x.TreatmentPlanSession != null ? $"Session {x.TreatmentPlanSession.SessionNumber}" : null,
                DoctorName = x.Doctor != null ? x.Doctor.FullName : _context.Users.Where(u => u.Id == x.SeniorId).Select(u => u.FullName).FirstOrDefault() ?? "Not Assigned",
                RoomName = x.Room != null ? x.Room.Name : "No Room",
                Status = x.Status.ToString()
            })
            .ToListAsync();

        var pagedResponse = new PagedResponse<TodaySessionResponse>
        {
            Items = sessions,
            PageNumber = request.PageNumber,
            PageSize = request.PageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };

        return Result.Success(pagedResponse);
    }

  

    public async Task<Result> UpdateAppointmentStatusAsync(int appointmentId, AppointmentStatus newStatus)
    {
        try { await bookings.TransitionAsync(appointmentId, newStatus); return Result.Success(); }
        catch (BookingRuleException e) { return Result.Failure(new(e.Code, e.Message, e.Status)); }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 1205 or 1222 or 51001) { return Result.Failure(new("Booking.ConcurrentChange", "The session changed concurrently. Refresh before starting again.", 409)); }
    }

    public Task<Result> CancelAppointmentAsAbsentAsync(int appointmentId) => UpdateAppointmentStatusAsync(appointmentId, AppointmentStatus.Cancelled);
}
