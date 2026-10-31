namespace PTCenter.Application.Contracts.Doctor;

public record DoctorVisit(int AppointmentId, int PatientId, string PatientName, string? Phone, string Gender,
    int? TreatmentPlanId, int? SessionId, int? SessionNumber, string? PlanSummary, string? DoctorId, string? DoctorName,
    int? RoomId, string? RoomName, DateTime PlannedStart, DateTime PlannedEnd, DateTime? ArrivedAt, DateTime? DepartedAt,
    DateTime? TreatmentStartedAt, DateTime? TreatmentEndedAt, string State);
public record DoctorScheduleResponse(DateOnly Date, List<DoctorVisit> Appointments, List<DoctorVisit> ActiveTreatments);
public record ClinicalPlan(int Id, string Diagnosis, string ConditionType, string? AssessmentNotes, DateTime? AssessedAt,
    string SeniorName, string TreatmentType, string? Exercises, int Prescribed, int Booked, int Completed, int FrequencyPerWeek,
    string AssignedDoctor, bool StaffReviewRequired);
public record ClinicalNotes(long Version, string ProgressNotes, string ConditionChanges, string ResponseToTreatment,
    string TreatmentPerformed, string? AuthorName, DateTime? SavedAt, DateTime? FinalizedAt);
public record PreviousClinicalSession(int SessionNumber, DateTime? PlannedAt, DateTime? TreatmentStartedAt,
    DateTime? TreatmentEndedAt, string? AuthorName, DateTime? RecordedAt, string? ProgressNotes, string? ConditionChanges,
    string? ResponseToTreatment, string? TreatmentPerformed);
public record ClinicalDocument(int Id, string FileName, DateTime UploadedAt);
public record DoctorPatientWorkspace(DoctorVisit Visit, DateTime? BirthDate, ClinicalPlan Plan, ClinicalNotes Notes,
    List<PreviousClinicalSession> PreviousSessions, List<ClinicalDocument> Documents, List<ReassessmentView> Reassessments);
public record NotesMutation(long ExpectedVersion, string ProgressNotes, string ConditionChanges, string ResponseToTreatment, string TreatmentPerformed);
public record RequestReassessment(string OperationKey, string ReasonCategory, string Notes, string Recommendation);
public record ReassessmentView(int Id, int PatientId, string PatientName, int TreatmentPlanId, int? RelatedAppointmentId,
    string RequestedBy, DateTime RequestedAt, string ReasonCategory, string Notes, string Recommendation, string State,
    DateTime? ReviewStartedAt, string? SeniorId, string? SeniorName, int? CompletedAssessmentId, int? RevisedPlanId,
    string? CancellationReason, long Version, string Diagnosis, string? Exercises, List<PreviousClinicalSession> PreviousSessions, int PrescribedSessions, int CompletedSessions,
    string TreatmentType, int FrequencyPerWeek, string DoctorId, string DoctorName, string? ReviewNotes, string? Decision, DateTime? ResolvedAt, string? ResolvedBy);
public record StartReassessment(long ExpectedVersion);
public record CancelReassessment(long ExpectedVersion, string Reason);
public record CompleteReassessment(long ExpectedVersion, string ProgressNotes, string Decision, string Diagnosis,
    string DoctorId, int NumberOfSessions, int FrequencyPerWeek, string TreatmentType, string? Exercises);
