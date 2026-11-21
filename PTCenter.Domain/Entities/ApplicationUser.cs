using Microsoft.AspNetCore.Identity;
using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Domain.Entities;

public class ApplicationUser : IdentityUser
{
    public bool IsActive { get; set; } = true;
    public string? StatusChangedBy { get; set; }
    public DateTime? StatusChangedAt { get; set; }
    public string FullName { get; set; } = null!;
    public Gender Gender { get; set; } = Gender.Male;
    public DateTime BirthDate { get; set; }
    public ICollection<Patient> Patients { get; set; } = new List<Patient>();
}
