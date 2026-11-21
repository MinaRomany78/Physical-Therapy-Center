using System.ComponentModel.DataAnnotations;
namespace PTCenter.Application.Contracts.Ceo;
public class CompanyQuery
{
    [MaxLength(100)] public string? Search { get; set; }
    public bool? Active { get; set; }
    [Range(1,100000)] public int Page { get; set; } = 1;
    [Range(1,100)] public int PageSize { get; set; } = 20;
}
