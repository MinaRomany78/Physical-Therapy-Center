using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Domain.Entities;

public class Appointment : BaseEntity
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public string? DoctorId { get; set; }
    public string? SeniorId { get; set; }
    public int? RoomId { get; set; }
    public int? TreatmentPlanSessionId { get; set; }
    public string AppointmentType { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public AppointmentStatus Status { get; set; }
    public int? BookingBatchId { get; set; }
    public int? BookingSequence { get; set; }
    public bool InsuranceFunded { get; set; }
    public DateTime? ActualCheckIn { get; set; }
    public DateTime? ActualCheckOut { get; set; }
    public DateTime? TreatmentStartedAt { get; set; }
    public DateTime? TreatmentEndedAt { get; set; }
    public string? TreatmentStartedBy { get; set; }
    public string? TreatmentEndedBy { get; set; }

    // أضف هذه الـ Navigation Properties لكي يعمل الـ Include في الـ Service بنجاح:
    public virtual Patient Patient { get; set; } = null!;
    public virtual ApplicationUser? Doctor { get; set; }
    public virtual Room? Room { get; set; }
    public virtual TreatmentPlanSession? TreatmentPlanSession { get; set; }
}
