using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Abstractions;
using PTCenter.Application.Contracts.Rooms;
using PTCenter.Application.Interfaces;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Domain.Errors;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

public class RoomService(ApplicationDbContext context) : IRoomService
{
    private readonly ApplicationDbContext _context = context;

    public async Task<Result<List<RoomResponse>>> GetAvailableAsync(RoomAvailabilityRequest request)
    {
        if (request.StartsAt.Count is < 1 or > 200 || request.StartsAt.Any(start => start > DateTimeOffset.MaxValue.AddHours(-1)))
            return Result.Failure<List<RoomResponse>>(new("Room.InvalidTimes", "Select between 1 and 200 valid appointment times.", 400));
        var intervals = request.StartsAt.Select(start => (Start: start.UtcDateTime, End: start.UtcDateTime.AddHours(1))).ToArray();
        var earliest = intervals.Min(slot => slot.Start);
        var latest = intervals.Max(slot => slot.End);
        var rooms = await _context.Rooms.AsNoTracking().Where(room => room.IsActive && room.Capacity > 0).OrderBy(room => room.Id).ToListAsync();
        var reservations = await _context.Appointments.AsNoTracking()
            .Where(a => a.RoomId != null && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.InProgress) &&
                (a.Status == AppointmentStatus.InProgress && a.ActualCheckOut == null || a.StartTime < latest && a.EndTime > earliest))
            .ToListAsync();
        var available = rooms.Where(room => intervals.All(slot => ClinicalCapacity.Peak(slot.Start,slot.End,reservations.Where(a=>a.RoomId==room.Id)) < room.Capacity));
        return Result.Success(available.Select(room => new RoomResponse { Id = room.Id, Name = room.Name, Capacity = room.Capacity,
            IsActive = room.IsActive, Status = StatusRoom.Available, CurrentLoad = 0 }).ToList());
    }

    public async Task<Result<List<RoomResponse>>> GetAllAsync()
    {
        var rooms = await _context.Rooms
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync();

        var responses = new List<RoomResponse>();

        foreach (var room in rooms)
            responses.Add(await MapRoomAsync(room));

        return Result.Success(responses);
    }

    public async Task<Result<RoomResponse>> GetByIdAsync(int id)
    {
        var room = await _context.Rooms
            .FirstOrDefaultAsync(x => x.Id == id);

        if (room is null)
            return Result.Failure<RoomResponse>(RoomErrors.RoomNotFound);

        return Result.Success(await MapRoomAsync(room));
    }

    public async Task<Result<RoomResponse>> CreateAsync(CreateRoomRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Result.Failure<RoomResponse>(RoomErrors.InvalidName);

        if (request.Capacity <= 0)
            return Result.Failure<RoomResponse>(RoomErrors.InvalidCapacity);

        var room = new Room
        {
            Name = request.Name.Trim(),
            Capacity = request.Capacity,
            IsActive = request.IsActive
        };

        await _context.Rooms.AddAsync(room);
        await _context.SaveChangesAsync();

        return Result.Success(await MapRoomAsync(room));
    }

    public async Task<Result<RoomResponse>> UpdateAsync(
        int id,
        UpdateRoomRequest request)
    {
        await using var transaction=await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(_context);
        var room = await _context.Rooms
            .FirstOrDefaultAsync(x => x.Id == id);

        if (room is null)
            return Result.Failure<RoomResponse>(RoomErrors.RoomNotFound);

        if (string.IsNullOrWhiteSpace(request.Name))
            return Result.Failure<RoomResponse>(RoomErrors.InvalidName);

        if (request.Capacity <= 0)
            return Result.Failure<RoomResponse>(RoomErrors.InvalidCapacity);

        var reservations=await _context.Appointments.AsNoTracking().Where(a=>a.RoomId==id && a.Status!=AppointmentStatus.Cancelled && a.Status!=AppointmentStatus.NoShow && a.Status!=AppointmentStatus.Completed && (a.EndTime>DateTime.UtcNow || a.ActualCheckIn!=null && a.ActualCheckOut==null)).ToListAsync();
        if(reservations.Count>0 && (ClinicalCapacity.Peak(reservations.Min(a=>a.StartTime),reservations.Max(a=>a.EndTime),reservations)>request.Capacity || reservations.Count(a=>ClinicalCapacity.Occupied(a))>request.Capacity))
            return Result.Failure<RoomResponse>(new("Room.CapacityReserved","Existing reservations or checked-in patients exceed this capacity. Review the appointments before reducing it.",409));

        if (!request.IsActive && room.IsActive)
        {
            var hasFutureAppointments = await _context.Appointments.AnyAsync(x =>
                x.RoomId == id &&
                (x.EndTime > DateTime.UtcNow || x.ActualCheckIn!=null && x.ActualCheckOut==null) &&
                x.Status != AppointmentStatus.Cancelled &&
                x.Status != AppointmentStatus.Completed &&
                x.Status != AppointmentStatus.NoShow);

            if (hasFutureAppointments)
                return Result.Failure<RoomResponse>(
                    RoomErrors.RoomHasFutureAppointments);
        }

        room.Name = request.Name.Trim();
        room.Capacity = request.Capacity;
        room.IsActive = request.IsActive;

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();

        return Result.Success(await MapRoomAsync(room));
    }

    public async Task<Result> UpdateMaintenanceAsync(
        int id,
        bool maintenance)
    {
        await using var transaction=await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(_context);
        var room = await _context.Rooms
            .FirstOrDefaultAsync(x => x.Id == id);

        if (room is null)
            return Result.Failure(RoomErrors.RoomNotFound);

        if (maintenance)
        {
            var hasFutureAppointments = await _context.Appointments.AnyAsync(x =>
                x.RoomId == id &&
                (x.EndTime > DateTime.UtcNow || x.ActualCheckIn!=null && x.ActualCheckOut==null) &&
                x.Status != AppointmentStatus.Cancelled &&
                x.Status != AppointmentStatus.Completed &&
                x.Status != AppointmentStatus.NoShow);

            if (hasFutureAppointments)
                return Result.Failure(RoomErrors.RoomHasFutureAppointments);
        }

        room.IsActive = !maintenance;

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();

        return Result.Success();
    }

    public async Task<Result> DeleteAsync(int id)
    {
        var room = await _context.Rooms
            .FirstOrDefaultAsync(x => x.Id == id);

        if (room is null)
            return Result.Failure(RoomErrors.RoomNotFound);

        var hasAppointments = await _context.Appointments
            .AnyAsync(x => x.RoomId == id);

        if (hasAppointments)
            return Result.Failure(RoomErrors.RoomHasAppointments);

        _context.Rooms.Remove(room);
        await _context.SaveChangesAsync();

        return Result.Success();
    }

    private async Task<RoomResponse> MapRoomAsync(Room room)
    {
        var now = DateTime.UtcNow;

        var currentAppointments = await _context.Appointments
            .AsNoTracking()
            .Where(x =>
                x.RoomId == room.Id && x.ActualCheckIn != null && x.ActualCheckOut == null &&
                x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow)
            .Select(x => new
            {
                x.DoctorId,
                DoctorName = x.Doctor != null ? x.Doctor.FullName : _context.Users.Where(u=>u.Id==x.SeniorId).Select(u=>u.FullName).FirstOrDefault(),
                Occupant = new RoomOccupant(x.Id,x.Patient.ApplicationUser.FullName,x.Doctor != null ? x.Doctor.FullName : _context.Users.Where(u=>u.Id==x.SeniorId).Select(u=>u.FullName).FirstOrDefault(),
                    x.TreatmentEndedAt != null ? "TreatmentFinished" : x.TreatmentStartedAt != null ? "Treating" : "Arrived")
            })
            .ToListAsync();

        var currentLoad = currentAppointments.Count;

        var status = !room.IsActive
            ? StatusRoom.Maintenance
            : currentLoad > 0
                ? StatusRoom.Occupied
                : StatusRoom.Available;

        var doctor = currentAppointments.FirstOrDefault();

        return new RoomResponse
        {
            Id = room.Id,
            Name = room.Name,
            Capacity = room.Capacity,
            IsActive = room.IsActive,
            Status = status,
            CurrentLoad = currentLoad,
            DoctorId = doctor?.DoctorId,
            DoctorName = doctor?.DoctorName,
            Occupants = currentAppointments.Select(x=>x.Occupant).ToList()
        };
    }
}
