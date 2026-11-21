using System.ComponentModel.DataAnnotations;

namespace PTCenter.Application.Contracts.Billing;

public class InsuranceFinanceQuery : BillingQuery
{
    [Range(0,int.MaxValue)] public int? CompanyId { get; set; }
    [RegularExpression("^[0-9]{4}-(0[1-9]|1[0-2])$")] public string? ServiceMonth { get; set; }
    [MaxLength(100)] public string? CompanySearch { get; set; }
    [MaxLength(150)] public string? ClaimReference { get; set; }
    [RegularExpression("^(Unsubmitted|Unpaid|Partial|Settled|Outstanding)$")] public string? PaymentState { get; set; }
}
public record InsurerAmounts(int Sessions,decimal Earned,decimal Submitted,decimal Receipts,decimal Remaining,int ReceiptCount);
public record InsuranceCompanyAccount(int CompanyId,string Name,InsurerAmounts Amounts,int UndatedInvoices);
public record InsuranceCompanyPage(IReadOnlyList<InsuranceCompanyAccount> Items,int MatchingCompanies,int MatchingInvoices,int Page,int PageSize,InsurerAmounts Totals,int UnavailableCompanyInvoices,int UndatedInvoices);
public record InsuranceMonth(string Month,InsurerAmounts Amounts);
public record InsurancePatientAccount(int PatientId,string Name,string? Phone,InsurerAmounts Amounts,string State);
public record InsuranceMonthAccount(int CompanyId,string Company,string ServiceMonth,IReadOnlyList<InsuranceMonth> Months,
    IReadOnlyList<InsurancePatientAccount> Patients,int MatchingPatients,int MatchingInvoices,int Page,int PageSize,InsurerAmounts Totals,int UnavailableEvidenceInvoices);
public record InsuranceSession(BillingInvoice Invoice,int? ClaimId,int? SubmissionBatchId,string? ClaimReference,DateTime? SubmittedAt,decimal SubmittedAmount,string ClaimState,bool CanSubmitClaim);
public record InsuranceSessionPage(IReadOnlyList<InsuranceSession> Items,int MatchingInvoices,int Page,int PageSize,InsurerAmounts Totals);
public record MonthlyClaimRequest([Required,MaxLength(90)] string IdempotencyKey,[Range(1,int.MaxValue)] int CompanyId,
    [Required,RegularExpression("^[0-9]{4}-(0[1-9]|1[0-2])$")] string ServiceMonth,
    [Required,MinLength(1),MaxLength(1000)] IReadOnlyList<int> InvoiceIds,[Required,MaxLength(150)] string Reference);
public record MonthlyClaimReceipt(int Id,int CompanyId,string ServiceMonth,string Reference,DateTime RecordedAt,string RecordedBy,IReadOnlyList<int> ClaimIds,decimal Amount);
public class ChequeRequest
{
    [Required,MaxLength(90)] public string IdempotencyKey { get; set; } = "";
    [Range(1,int.MaxValue)] public int CompanyId { get; set; }
    [Required,MinLength(1),MaxLength(24)] public string[] ServiceMonths { get; set; } = [];
    [MaxLength(1000)] public int[] ClaimIds { get; set; } = [];
    [Required,MaxLength(100)] public string ChequeNumber { get; set; } = "";
    [Required,MaxLength(150)] public string BankName { get; set; } = "";
    [Range(typeof(decimal),"0.01","999999999")] public decimal Amount { get; set; }
    public DateOnly ReceivedOn { get; set; }
    public DateOnly? ChequeDate { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    [MaxLength(64)] public string? ExpectedAllocationHash { get; set; }
}
public record ChequeAllocation(int PaymentId,int ClaimId,int InvoiceId,string InvoiceReference,int PatientId,string PatientName,
    string ServiceMonth,DateTime ServiceDate,string ClaimReference,decimal Before,decimal Amount,decimal After);
public record ChequePreview(int CompanyId,string Company,IReadOnlyList<string> ServiceMonths,decimal Amount,decimal Before,decimal After,
    string Hash,IReadOnlyList<ChequeAllocation> Allocations);
public record ChequeReceipt(int Id,string IdempotencyKey,int CompanyId,string Company,string ChequeNumber,string BankName,decimal Amount,
    DateOnly ReceivedOn,DateOnly? ChequeDate,string? Notes,string RecordedBy,DateTime RecordedAt,IReadOnlyList<ChequeAllocation> Allocations);
public record InsurerLegacyReceipt(int PaymentId,int InvoiceId,string InvoiceReference,int? ClaimId,string? Reference,decimal Amount,DateTime ReceivedAt,string Method);
public record InsuranceReceiptHistory(IReadOnlyList<ChequeReceipt> Items,int MatchingReceipts,int Page,int PageSize,decimal ActualReceiptTotal,
    decimal AllocatedToScope,IReadOnlyList<InsurerLegacyReceipt> LegacyReceipts,decimal LegacyReceiptTotal);
