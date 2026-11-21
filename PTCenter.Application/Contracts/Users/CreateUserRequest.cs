using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Application.Contracts.Users;
using System.ComponentModel.DataAnnotations;

public class CreateUserRequest
{
    [Required, MaxLength(256)] public string UserName { get; set; } = default!;
    [Required, StringLength(128, MinimumLength=8)] public string Password { get; set; } = default!;
    [Required, MaxLength(200)] public string FullName { get; set; } = default!;
    [EnumDataType(typeof(Gender))] public Gender Gender { get; set; }
    public DateTime BirthDate { get; set; }
    [Required, MinLength(1)] public List<string> Roles { get; set; } = [];
    public List<StaffWorkingHours>? WorkingHours { get; set; }
}
