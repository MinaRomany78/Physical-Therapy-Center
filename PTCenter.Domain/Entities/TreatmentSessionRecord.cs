namespace PTCenter.Domain.Entities;

// New records only. Legacy SessionNotes are retained and read as historical notes.
public class TreatmentSessionRecord
{
    public int AppointmentId { get; set; }
    public string ProgressNotes { get; set; } = "";
    public string ConditionChanges { get; set; } = "";
    public string ResponseToTreatment { get; set; } = "";
    public string TreatmentPerformed { get; set; } = "";
    public long Version { get; set; }
    public string AuthorId { get; set; } = null!;
    public DateTime SavedAt { get; set; }
    public DateTime? FinalizedAt { get; set; }
}
