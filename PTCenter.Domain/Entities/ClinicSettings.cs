namespace PTCenter.Domain.Entities;

/// <summary>Singleton operational identity. Timezone/currency are fixed business rules.</summary>
public class ClinicSettings
{
    public int Id { get; set; } = 1;
    public string Name { get; set; } = "Physical Therapy Center";
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Email { get; set; }
    public int Version { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
