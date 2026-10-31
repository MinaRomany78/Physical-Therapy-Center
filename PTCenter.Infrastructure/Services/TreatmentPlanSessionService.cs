using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Abstractions;
using PTCenter.Application.Contracts.TreatmentPlans;
using PTCenter.Application.Contracts.TreatmentPlanSession;
using PTCenter.Application.Interfaces;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Domain.Errors;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

public class TreatmentPlanSessionService(
    ApplicationDbContext context)
    : ITreatmentPlanSessionService
{
    private readonly ApplicationDbContext _context = context;

    public async Task<Result<TreatmentPlanSessionResponse>> AddAsync(
        CreateTreatmentPlanSessionRequest request)
    {
        if (request.PaymentStatus != PaymentStatus.Pending || request.Status != SessionStatus.Pending || request.CheckIn != null || request.CheckOut != null || request.SessionDate != null)
            return Result.Failure<TreatmentPlanSessionResponse>(new("Booking.ManagedSession", "Create an unbooked session here. Scheduling, actual attendance and payment coverage are recorded through the booking workflow.", 400));
        if (request.SessionNumber <= 0)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.InvalidSessionNumber);
        }

        var treatmentPlan = await _context.TreatmentPlans
            .FirstOrDefaultAsync(x => x.Id == request.TreatmentPlanId);

        if (treatmentPlan is null)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.TreatmentPlanNotFound);
        }

        if (request.SessionNumber > treatmentPlan.NumberOfSessions)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.SessionNumberOutOfRange);
        }

        var sessionExists = await _context.TreatmentPlanSessions
            .AnyAsync(x =>
                x.TreatmentPlanId == request.TreatmentPlanId &&
                x.SessionNumber == request.SessionNumber);

        if (sessionExists)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.SessionAlreadyExists);
        }

        if (request.CheckIn.HasValue &&
            request.CheckOut.HasValue &&
            request.CheckOut < request.CheckIn)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.InvalidCheckInOut);
        }

        var session = new TreatmentPlanSession
        {
            TreatmentPlanId = request.TreatmentPlanId,
            SessionNumber = request.SessionNumber,
            SessionDate = request.SessionDate,
            CheckIn = request.CheckIn,
            CheckOut = request.CheckOut,
            PaymentStatus = request.PaymentStatus,
            Status = request.Status,
            Notes = request.Notes
        };

        await _context.TreatmentPlanSessions.AddAsync(session);

        await _context.SaveChangesAsync();

        await UpdateCompletedSessionsAsync(request.TreatmentPlanId);

        return await GetByIdAsync(session.Id);
    }

    public async Task<Result<List<TreatmentPlanSessionResponse>>> GetAllAsync(
        int treatmentPlanId)
    {
        var treatmentPlanExists = await _context.TreatmentPlans
            .AnyAsync(x => x.Id == treatmentPlanId);

        if (!treatmentPlanExists)
        {
            return Result.Failure<List<TreatmentPlanSessionResponse>>(
                TreatmentPlanSessionErrors.TreatmentPlanNotFound);
        }

        var sessions = await _context.TreatmentPlanSessions
            .AsNoTracking()
            .Where(x => x.TreatmentPlanId == treatmentPlanId)
            .OrderBy(x => x.SessionNumber)
            .Select(x => new TreatmentPlanSessionResponse
            {
                Id = x.Id,
                TreatmentPlanId = x.TreatmentPlanId,
                SessionNumber = x.SessionNumber,
                SessionDate = x.SessionDate,
                CheckIn = x.CheckIn,
                CheckOut = x.CheckOut,
                PaymentStatus = x.PaymentStatus,
                Status = x.Status,
                Notes = x.Notes
            })
            .ToListAsync();

        return Result.Success(sessions);
    }

    public async Task<Result<TreatmentPlanSessionResponse>> GetByIdAsync(
        int id)
    {
        var session = await _context.TreatmentPlanSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (session is null)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.SessionNotFound);
        }

        return Result.Success(MapToResponse(session));
    }

    public async Task<Result<TreatmentPlanSessionResponse>> UpdateAsync(
        int id,
        UpdateTreatmentPlanSessionRequest request)
    {
        if (request.SessionNumber <= 0)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.InvalidSessionNumber);
        }

        var session = await _context.TreatmentPlanSessions
            .FirstOrDefaultAsync(x => x.Id == id);

        if (session is null)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.SessionNotFound);
        }

        if (session.Status == SessionStatus.Completed && request.Notes != session.Notes)
            return Result.Failure<TreatmentPlanSessionResponse>(new("Doctor.Finalized", "Finalized treatment notes are read-only; no correction history is implemented.", 409));

        var treatmentPlan = await _context.TreatmentPlans
            .FirstOrDefaultAsync(x => x.Id == session.TreatmentPlanId);

        if (treatmentPlan is null)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.TreatmentPlanNotFound);
        }

        if (request.SessionNumber > treatmentPlan.NumberOfSessions)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.SessionNumberOutOfRange);
        }

        var sessionExists = await _context.TreatmentPlanSessions
            .AnyAsync(x =>
                x.Id != id &&
                x.TreatmentPlanId == session.TreatmentPlanId &&
                x.SessionNumber == request.SessionNumber);

        if (sessionExists)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.SessionAlreadyExists);
        }

        if (request.CheckIn.HasValue &&
            request.CheckOut.HasValue &&
            request.CheckOut < request.CheckIn)
        {
            return Result.Failure<TreatmentPlanSessionResponse>(
                TreatmentPlanSessionErrors.InvalidCheckInOut);
        }

        if (request.PaymentStatus != session.PaymentStatus)
            return Result.Failure<TreatmentPlanSessionResponse>(new("Booking.ManagedPaymentStatus", "Payment coverage is calculated from saved payments, never from a session status flag.", 400));
        if (request.CheckIn != session.CheckIn || request.CheckOut != session.CheckOut || request.Status != session.Status || request.SessionDate != session.SessionDate)
            return Result.Failure<TreatmentPlanSessionResponse>(new("Booking.ManagedAttendance", "Use the appointment reschedule and explicit check-in/check-out actions. Session notes remain editable here.", 400));
        if (request.SessionNumber != session.SessionNumber && await _context.Appointments.AnyAsync(a => a.TreatmentPlanSessionId == id))
            return Result.Failure<TreatmentPlanSessionResponse>(new("Booking.ManagedSession", "A booked session allocation cannot be renumbered.", 409));
        session.SessionNumber = request.SessionNumber;
        session.SessionDate = request.SessionDate;
        session.CheckIn = request.CheckIn;
        session.CheckOut = request.CheckOut;
        session.PaymentStatus = request.PaymentStatus;
        session.Status = request.Status;
        session.Notes = request.Notes;

        await _context.SaveChangesAsync();

        await UpdateCompletedSessionsAsync(session.TreatmentPlanId);

        return await GetByIdAsync(id);
    }

    public async Task<Result> DeleteAsync(int id)
    {
        var session = await _context.TreatmentPlanSessions
            .FirstOrDefaultAsync(x => x.Id == id);

        if (session is null)
        {
            return Result.Failure(
                TreatmentPlanSessionErrors.SessionNotFound);
        }

        var treatmentPlanId = session.TreatmentPlanId;
        if (await _context.Appointments.AnyAsync(a => a.TreatmentPlanSessionId == id))
            return Result.Failure(new("Booking.ManagedSession", "A session linked to a booking cannot be deleted. Reschedule its appointment instead.", 409));

        try
        {
            _context.TreatmentPlanSessions.Remove(session);

            await _context.SaveChangesAsync();

            await UpdateCompletedSessionsAsync(treatmentPlanId);

            return Result.Success();
        }
        catch
        {
            return Result.Failure(
                TreatmentPlanSessionErrors.DeleteFailed);
        }
    }

    private async Task UpdateCompletedSessionsAsync(
        int treatmentPlanId)
    {
        var completedSessions = await _context.TreatmentPlanSessions
            .CountAsync(x =>
                x.TreatmentPlanId == treatmentPlanId &&
                x.Status == SessionStatus.Completed);

        var treatmentPlan = await _context.TreatmentPlans
            .FirstOrDefaultAsync(x => x.Id == treatmentPlanId);

        if (treatmentPlan is null)
            return;

        treatmentPlan.CompletedSessions = completedSessions;

        await _context.SaveChangesAsync();
    }

    private static TreatmentPlanSessionResponse MapToResponse(
        TreatmentPlanSession session)
    {
        return new TreatmentPlanSessionResponse
        {
            Id = session.Id,
            TreatmentPlanId = session.TreatmentPlanId,
            SessionNumber = session.SessionNumber,
            SessionDate = session.SessionDate,
            CheckIn = session.CheckIn,
            CheckOut = session.CheckOut,
            PaymentStatus = session.PaymentStatus,
            Status = session.Status,
            Notes = session.Notes
        };
    }
}
