using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Application.Contracts.Bookings;

public class BookingRequest
{
    public string IdempotencyKey { get; set; } = "";
    public int PatientId { get; set; }
    public string ClinicianId { get; set; } = "";
    public int RoomId { get; set; }
    public int? TreatmentPlanId { get; set; }
    public List<DateTimeOffset> StartsAt { get; set; } = [];
    public string? AssessmentCharge { get; set; }
    public string PricingMode { get; set; } = "PerSession";
    public decimal? UnitPrice { get; set; }
    public decimal? PackagePrice { get; set; }
    public decimal Discount { get; set; }
    public string DiscountType { get; set; } = "None";
    public decimal DiscountValue { get; set; }
    public decimal Deposit { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }
    public List<InstallmentRequest> Installments { get; set; } = [];
}

public record InstallmentRequest(DateOnly DueDate, decimal Amount);
public record CollectBookingPaymentRequest(string IdempotencyKey, decimal Amount, PaymentMethod? Method);
public record BookingAccount(string Key, int PatientId, string PatientName, string PricingMode, decimal TotalAmount, decimal Discount,
    decimal PatientAmount, decimal InsuranceAmount, decimal PaidAmount, decimal RemainingAmount, List<int> InvoiceIds, List<InstallmentRequest> Installments);
public record BookingAssessment(int Id, DateTime AssessmentDate, DateTime? CompletedAt, string Diagnosis);
public record BookingPatientOption(int Id, string FullName, string PhoneNumber, string Gender);
public record BookingPlan(int Id, string DoctorId, string DoctorName, string TreatmentType,
    int PlannedSessions, int BookedSessions, int CompletedSessions, int RemainingSessions)
{
    public CourseFinancialSummary? Financial { get; set; }
    public bool StaffReviewRequired { get; set; }
    public int? FrequencyPerWeek { get; set; }
    public List<DateTime> BookedStartsAt { get; set; } = [];
}
public record BookingContext(int PatientId, string PatientName, string Gender, string? PaymentType,
    bool HasCompletedAssessment, bool IsInsurance, bool InsuranceApproved, int? RemainingApprovedSessions,
    decimal? CopayPercentage, string? InsuranceCompanyName, List<BookingAssessment> Assessments, List<BookingPlan> Plans)
{
    public int? ApprovedSessions { get; init; }
}
public record BookingClinician(string Id, string Name, string Gender, string Role, bool Preferred, int CurrentLoad);
public record BookingSlot(DateTimeOffset StartTime, DateTimeOffset EndTime, List<string> Conflicts);
public record BookingPreview(List<BookingSlot> Appointments, List<string> Errors, decimal TotalAmount,
    decimal PatientAmount, decimal InsuranceAmount, decimal Deposit, decimal RemainingAmount, string? WaiverReason)
{
    public CourseFinancialSummary? Financial { get; init; }
    public decimal DiscountAmount { get; init; }
    public bool CanSave => Errors.Count == 0 && Appointments.Count > 0 && Appointments.All(x => x.Conflicts.Count == 0);
}
public record BookedAppointment(int Id, int? TreatmentPlanSessionId, DateTimeOffset StartTime, DateTimeOffset EndTime)
{
    public string? ClinicianId { get; init; }
    public string? ClinicianName { get; init; }
    public int? RoomId { get; init; }
    public string? RoomName { get; init; }
    public int? TreatmentPlanId { get; init; }
    public int? SessionNumber { get; init; }
    public int? CourseAgreementId { get; init; }
    public List<int> InvoiceIds { get; init; } = [];
}
public record BookingReceipt(string IdempotencyKey, int PatientId, string Outcome, List<BookedAppointment> Appointments,
    List<int> InvoiceIds, decimal TotalAmount, decimal PatientAmount, decimal InsuranceAmount, decimal PaidAmount,
    decimal RemainingAmount, string? WaiverReason, List<InstallmentRequest> Installments);
public class RescheduleBookingRequest
{
    public DateTimeOffset StartTime { get; set; }
    public string ClinicianId { get; set; } = "";
    public int RoomId { get; set; }
}
public class CompleteAssessmentRequest
{
    public int AppointmentId { get; set; }
    public string Diagnosis { get; set; } = "";
    public int NumberOfSessions { get; set; }
    public int FrequencyPerWeek { get; set; }
    public string DoctorId { get; set; } = "";
    public string TreatmentType { get; set; } = "PhysicalTherapy";
    public string? Exercises { get; set; }
}
