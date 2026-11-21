using System.ComponentModel.DataAnnotations;
using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Application.Contracts.Billing;

/// <summary>Shared invoice-scope contract for operational billing, expansion and finance exports.</summary>
public class BillingQuery
{
    [MaxLength(100)] public string? Search { get; set; }
    [RegularExpression("^(SelfPay|Insurance)$")] public string? Funding { get; set; }
    [MaxLength(30)] public int[] CompanyIds { get; set; } = [];
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    [RegularExpression("^(Invoice|Service|Collection)$")] public string DateBasis { get; set; } = "Invoice";
    [RegularExpression("^(Unpaid|Partial|Settled|NonCollectible)$")] public string? PatientStatus { get; set; }
    [EnumDataType(typeof(InvoiceType))] public InvoiceType? Type { get; set; }
    [RegularExpression("^(PerSession|Package|Unknown)$")] public string? PricingMode { get; set; }
    [EnumDataType(typeof(InvoiceStatus))] public InvoiceStatus? Status { get; set; }
    [EnumDataType(typeof(AppointmentStatus))] public AppointmentStatus? AppointmentStatus { get; set; }
    [EnumDataType(typeof(SessionStatus))] public SessionStatus? SessionStatus { get; set; }
    [Range(1, int.MaxValue)] public int? PlanId { get; set; }
    [MaxLength(450)] public string? ClinicianId { get; set; }
    [EnumDataType(typeof(PaymentMethod))] public PaymentMethod? Method { get; set; }
    [RegularExpression("^(Unsubmitted|Submitted|Partial|Settled|Outstanding)$")] public string? ClaimStatus { get; set; }
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
}

public record BillingAmounts(decimal Agreed, decimal PatientLiability, decimal PatientReceipts,
    decimal PatientRemaining, decimal CollectedInPeriod, decimal InsurerPlanned, decimal InsurerEarned,
    decimal ClaimsSubmitted, decimal InsurerReceipts, decimal InsurerEarnedOutstanding, int ReceiptCount);
public record BillingPatient(int PatientId, string Name, string? Phone, string Funding,
    string[] Companies, int InvoiceCount, BillingAmounts Amounts, string PatientStatus, bool FullySettled);
public record BillingPage<T>(IReadOnlyList<T> Items, int MatchingPatients, int MatchingInvoices,
    int Page, int PageSize, BillingAmounts Totals, string DateBasis, string Timezone = "Africa/Cairo");
public record BillingInvoice(int Id, string Reference, int PatientId, string PatientName, DateTime CreatedAt,
    DateTime? DeliveredAt, string Funding, int? CompanyId, string? Company, string PricingMode, int? PlanId,
    int? AgreementId, int? SessionId, string? ClinicianId, string? ClinicianName, InvoiceType Type,
    InvoiceStatus Status, AppointmentStatus? AppointmentStatus, SessionStatus? SessionStatus,
    BillingAmounts Amounts, string PatientStatus, bool FullySettled, string? CollectionBlockedReason)
{
    public decimal PatientShare { get; init; }
    public decimal InsurerShare { get; init; }
}
public record BillingOption(string Id, string Name);
public record BillingOptions(IReadOnlyList<BillingOption> Companies, IReadOnlyList<BillingOption> Plans,
    IReadOnlyList<BillingOption> Clinicians);
