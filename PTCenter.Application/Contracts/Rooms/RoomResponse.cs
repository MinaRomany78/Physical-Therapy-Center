namespace PTCenter.Application.Contracts.Rooms;
 public enum StatusRoom
{
    Available,
    Occupied,
    Maintenance
}
public class RoomResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public int Capacity { get; set; }
    public bool IsActive { get; set; }
    public StatusRoom Status { get; set; } 
    public string? DoctorId { get; set; }
    public string? DoctorName { get; set; }
    public int CurrentLoad { get; set; }
    public List<RoomOccupant> Occupants { get; set; } = [];
}
public record RoomOccupant(int AppointmentId, string PatientName, string? ClinicianName, string State);
