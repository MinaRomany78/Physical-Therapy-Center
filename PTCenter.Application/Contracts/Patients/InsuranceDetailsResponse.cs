using PTCenter.Domain.Entities;

namespace PTCenter.Application.Contracts.Patients;
using System.ComponentModel.DataAnnotations;

public class InsuranceDetailsResponse : IValidatableObject
{
    [EnumDataType(typeof(InsuranceStatus))] public InsuranceStatus? Status { get; set; }
    [Range(typeof(decimal), "0", "100")] public decimal? CopayPercentage { get; set; }
    [Range(0, int.MaxValue)] public int? ApprovedSessions { get; set; }
    public string? MemberId { get; set; }
    public string? Employer { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (CopayPercentage is decimal value && decimal.Round(value,2) != value)
            yield return new ValidationResult("Use at most two decimal places for the copay percentage.",[nameof(CopayPercentage)]);
    }
}
