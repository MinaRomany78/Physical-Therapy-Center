namespace PTCenter.Domain.Entities;

public class ReassessmentRequest
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public int TreatmentPlanId { get; set; }
    public int? RelatedAppointmentId { get; set; }
    public string RequestedBy { get; set; } = null!;
    public DateTime RequestedAt { get; set; }
    public string ReasonCategory { get; set; } = null!;
    public string Notes { get; set; } = null!;
    public string Recommendation { get; set; } = "Continue";
    public string State { get; set; } = "Requested";
    public int? ActivePlanId { get; set; }
    public string? ReviewingSeniorId { get; set; }
    public DateTime? ReviewStartedAt { get; set; }
    public int? CompletedAssessmentId { get; set; }
    public int? RevisedPlanId { get; set; }
    public string? CancellationReason { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public long Version { get; set; }
    public string OperationKey { get; set; } = null!;
    public string? CompletionHash { get; set; }
    public bool ReviewRequired { get; set; }
    public string? Decision { get; set; }
}
