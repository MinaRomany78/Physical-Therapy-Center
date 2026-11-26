namespace PTCenter.Application.Contracts.Bookings;

public record ProfileSession(int Id, int SessionNumber, int? AppointmentId, string? Version, string? ClinicianId, string DoctorName,
    int? RoomId, string? RoomName, DateTime? StartTime, DateTime? EndTime, DateTime? ActualCheckIn, DateTime? ActualCheckOut,
    string Status, string PatientCoverage, decimal? PatientLiability, decimal? PatientPaid, bool CanReschedule)
{
    public DateTime? ReceptionCheckIn { get; init; }
    public DateTime? ReceptionCheckOut { get; init; }
    public DateTime? ClinicalStart { get; init; }
    public DateTime? ClinicalEnd { get; init; }
}
public record ProfilePayment(int Id,int InvoiceId,string InvoiceNumber,decimal Amount,string Payer,string Method,DateTime Date,string? Reference);
public record ProfileActivity(int Id,int AppointmentId,DateTime ChangedAt,string ActorName,DateTime PreviousStart,DateTime PreviousEnd,
    string PreviousRoomName,DateTime NewStart,DateTime NewEnd,string NewRoomName,string? Reason) {
    public string? Action { get; init; }
    public string? PreviousClinicianName { get; init; }
    public string? ClinicianName { get; init; }
    public string? ContactOutcome { get; init; }
    public string? ConsentMethod { get; init; }
    public DateTime? ConsentRecordedAt { get; init; }
    public string? ConsentReason { get; init; }
}
public record ProfileCourse(int Id,string TreatmentType,string Status,string DoctorName,int PrescribedSessions,int CompletedSessions,
    decimal PatientAmount,decimal PatientPaid,decimal PatientBalance,decimal InsurerAmount,decimal InsurerPaid,decimal InsurerBalance,
    List<ProfileSession> Sessions,List<ProfilePayment> Payments,List<ProfileActivity> Activity);
public record ProfileAssessment(int Id,DateTime Date,string Diagnosis,string SeniorName,DateTime? CompletedAt);
public record PatientProfileData(int PatientId,List<ProfileCourse> Courses,List<ProfileAssessment> Assessments)
{
    public List<ProfilePayment> AssessmentPayments { get; init; } = [];
    public List<ProfileActivity> AppointmentActivity { get; init; } = [];
}
public record RescheduleSlot(DateTime StartTime,DateTime EndTime,int RoomId,string RoomName,int RemainingCapacity);
public record RescheduleAvailability(int AppointmentId,string Version,DateTime CurrentStart,DateTime CurrentEnd,int? CurrentRoomId,
    string ClinicianId,string ClinicianName,DateOnly Date,List<RescheduleSlot> Slots,List<string> Notices);
public class RescheduleSessionRequest
{
    public string OperationKey { get; set; } = "";
    public string ExpectedVersion { get; set; } = "";
    public DateTimeOffset StartTime { get; set; }
    public int RoomId { get; set; }
    public string? Reason { get; set; }
}
public record RescheduleSessionReceipt(string OperationKey,int AppointmentId,int PatientId,int? TreatmentPlanId,
    DateTime StartTime,DateTime EndTime,int RoomId,string RoomName,DateTime ChangedAt);
