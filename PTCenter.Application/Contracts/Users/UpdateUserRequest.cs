
using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Application.Contracts.Users;
using System.ComponentModel.DataAnnotations;

public class UpdateUserRequest
{
    public string? Version { get; set; }
    [Required, MaxLength(256)] public string UserName { get; set; } = null!;
    [Required, MaxLength(200)] public string FullName { get; set; } = null!;
    [EnumDataType(typeof(Gender))] public Gender Gender { get; set; }
    public DateTime BirthDate { get; set; }
    [Required, MinLength(1)] public List<string> Roles { get; set; } = [];
    public List<StaffWorkingHours>? WorkingHours { get; set; }
}

