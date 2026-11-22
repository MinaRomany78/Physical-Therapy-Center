using System.ComponentModel.DataAnnotations;
using PTCenter.Application.Contracts.Users;

namespace PTCenter.Application.Contracts.Ceo;

public class CeoQuery
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    [Range(1, int.MaxValue)] public int? CompanyId { get; set; }
    [Range(1, int.MaxValue)] public int? PatientId { get; set; }
    [MaxLength(100)] public string? Search { get; set; }
    [MaxLength(450)] public string? ClinicianId { get; set; }
    [RegularExpression("^(SelfPay|Insurance)$")] public string? Funding { get; set; }
    [RegularExpression("^(Cash|Visa|EWallet|BankTransfer|Online|Cheque)$")] public string? Method { get; set; }
    [RegularExpression("^(Pending|InProgress|Completed|Cancelled|NoShow)$")] public string? Status { get; set; }
    [Range(1, 100000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
}
public class ReportRow
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Patient { get; set; }
    public int? PatientId { get; set; }
    public int? CompanyId { get; set; }
    public string? Company { get; set; }
    public string? Clinician { get; set; }
    public string? ClinicianId { get; set; }
    public DateTime? Date { get; set; }
    public decimal Amount { get; set; }
    public decimal Collected { get; set; }
    public decimal Remaining { get; set; }
    public string? Status { get; set; }
    public string? Method { get; set; }
    public DateTime? ScheduledEnd { get; set; }
    public DateTime? ArrivedAt { get; set; }
    public DateTime? DepartedAt { get; set; }
    public DateTime? ClinicalStart { get; set; }
    public DateTime? ClinicalEnd { get; set; }
    public int? DurationSeconds { get; set; }
    public int VisitCount { get; set; }
    public int CompletedCount { get; set; }
    public int MissingClinicalTimestamps { get; set; }
}
public record ReportTotals(decimal Amount, decimal Collected, decimal Remaining, int Rows, int RecordedDurationSeconds = 0, int MissingClinicalTimestamps = 0, int VisitCount = 0);
public record CeoReport(string Kind, string DateBasis, string Scope, DateTime GeneratedAt,
    DateOnly? From, DateOnly? To, int Page, int PageSize, int Total, ReportTotals Totals,
    IReadOnlyList<ReportRow> Items, string Currency = "EGP", string Timezone = "Africa/Cairo");
public record CollectionDay(string Day, decimal Patient, decimal Insurer);
public record CeoOverview(DateTime GeneratedAt, DateOnly From, DateOnly To, decimal PatientCollections,
    decimal InsurerCollections, decimal PatientOutstanding, decimal EarnedInsurerOutstanding,
    decimal PlannedInsurerLiability, int Scheduled, int ClinicallyCompleted, int Cancelled, int NoShow,
    int Occupied, int ActiveRoomCapacity, int AvailableRoomSlots, int UnsubmittedServices, int OutstandingClaims,
    int UnresolvedAbsenceWork, IReadOnlyList<CollectionDay> Collections);
public class SettingsRequest
{
    [Required, StringLength(200, MinimumLength = 2)] public string Name { get; set; } = "";
    [MaxLength(50)] public string? Phone { get; set; }
    [MaxLength(500)] public string? Address { get; set; }
    [EmailAddress, MaxLength(200)] public string? Email { get; set; }
    [Range(0, int.MaxValue)] public int Version { get; set; }
}
public record ClinicIdentity(string Name, string? Phone, string? Address, string? Email, int Version,
    DateTime? UpdatedAt, string? UpdatedBy, string Timezone = "Africa/Cairo", string Currency = "EGP");
public class StaffQuery
{
    [MaxLength(100)] public string? Search { get; set; }
    [RegularExpression("^(Admin|Owner|Doctor|Senior|Receptionist|Ceo)$")] public string? Role { get; set; }
    public bool? Active { get; set; }
    [Range(1, 100000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
}
public record StaffRow(string Id, string FullName, string UserName, string Gender, DateTime BirthDate,
    bool IsActive, string[] Roles, IReadOnlyList<StaffWorkingHours> WorkingHours, int FutureAppointments, string Version);
public record StaffPage(IReadOnlyList<StaffRow> Items, int Total, int Page, int PageSize);
public record StaffStatusRequest(bool IsActive, [Required] string Version);
