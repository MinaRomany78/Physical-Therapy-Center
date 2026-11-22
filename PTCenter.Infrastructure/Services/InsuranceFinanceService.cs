using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Billing;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

/// <summary>Company/service-month views and atomic cheque receipts over the existing claim/payment ledger.</summary>
public sealed class InsuranceFinanceService(ApplicationDbContext db,TimeProvider? clock=null)
{
    private readonly TimeProvider time=clock ?? TimeProvider.System;
    private readonly BillingQueryService billing=new(db);
    private readonly CourseFinanceService finance=new(db);
    private static BookingRuleException Rule(string code,string message,int status=400,object? details=null)=>new("Insurance."+code,message,status,details);
    public static DateOnly Month(string value)
    {
        if(!DateOnly.TryParseExact(value+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day) || day.Year<1900 || day.Year>=9999)
            throw Rule("InvalidMonth","Select a service month in YYYY-MM format.");
        return day;
    }
    public static string MonthOf(DateTime utc)=>TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc,DateTimeKind.Utc),ClinicCalendar.Zone).ToString("yyyy-MM",CultureInfo.InvariantCulture);
    private DateOnly Today=>DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(),ClinicCalendar.Zone).DateTime);
    private static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static void Validate(InsuranceFinanceQuery q)
    {
        var errors=new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if(!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(q,new(q),errors,true) || q.From>q.To || q.From==DateOnly.MinValue || q.To is {} end && (end==DateOnly.MinValue || end==DateOnly.MaxValue))throw Rule("InvalidFilter","Select valid insurance filters and an ordered receipt-date range.");
        if(q.ServiceMonth!=null)Month(q.ServiceMonth);
        if(q.DateBasis!="Invoice" || q.Funding!=null || q.CompanyIds.Length>0 || q.PatientStatus!=null || q.Method!=null)
            throw Rule("InvalidFilter","Use company/service month for accounts and receipt dates for receipt history.");
    }
    private async Task<List<InsuranceSession>> RowsAsync(InsuranceFinanceQuery q,CancellationToken ct)
    {
        Validate(q);
        var filters=new BillingQuery{Search=q.Search,CompanyIds=q.CompanyId>0?[q.CompanyId.Value]:[],Type=q.Type,Status=q.Status,
            PricingMode=q.PricingMode,PlanId=q.PlanId,AppointmentStatus=q.AppointmentStatus,SessionStatus=q.SessionStatus,ClinicianId=q.ClinicianId,ClaimStatus=q.ClaimStatus};
        var invoices=(await billing.MatchingInvoicesAsync(filters,true,ct)).Where(i=>i.InsurerShare>0 && i.Status is not(InvoiceStatus.Waived or InvoiceStatus.Cancelled))
            .Where(i=>q.CompanyId==null || (i.CompanyId ?? 0)==q.CompanyId).ToList();
        var ids=invoices.Select(i=>i.Id).ToArray();
        var claims=await db.InsuranceClaims.AsNoTracking().Where(c=>ids.Contains(c.InvoiceId)).ToDictionaryAsync(c=>c.InvoiceId,ct);
        var eligible=(await db.Invoices.AsNoTracking().Where(i=>ids.Contains(i.Id) && i.DeliveredAt!=null && i.TreatmentPlanSessionId!=null &&
            db.TreatmentCourseAgreements.Any(a=>a.Id==i.CourseAgreementId && a.InsuranceCompanyId!=null && a.ApprovalReference!=null)).Select(i=>i.Id).ToListAsync(ct)).ToHashSet();
        return invoices.Select(i=>{
            var claim=claims.GetValueOrDefault(i.Id);
            var state=claim==null?"Unsubmitted":i.Amounts.InsurerEarnedOutstanding==0?"Settled":i.Amounts.InsurerReceipts>0?"Partial":"Submitted";
            return new InsuranceSession(i,claim?.Id,claim?.SubmissionBatchId,claim?.SubmissionReference,claim?.SubmittedAt,claim?.Amount ?? 0,state,claim==null && eligible.Contains(i.Id));
        }).Where(i=>string.IsNullOrWhiteSpace(q.ClaimReference) || i.ClaimReference?.Contains(q.ClaimReference.Trim(),StringComparison.OrdinalIgnoreCase)==true).ToList();
    }
    private static bool Delivered(InsuranceSession row)=>row.Invoice.DeliveredAt!=null && row.Invoice.Amounts.InsurerEarned>0;
    private static IEnumerable<InsuranceSession> InMonth(IEnumerable<InsuranceSession> rows,string month)
    {
        var day=Month(month);var start=ClinicCalendar.DayStart(day);var end=ClinicCalendar.DayStart(day.AddMonths(1));
        return rows.Where(r=>Delivered(r) && r.Invoice.DeliveredAt>=start && r.Invoice.DeliveredAt<end);
    }
    private static InsurerAmounts Sum(IEnumerable<InsuranceSession> source)
    {
        var rows=source.ToList();return new(rows.Count,rows.Sum(r=>r.Invoice.Amounts.InsurerEarned),rows.Sum(r=>r.SubmittedAmount),
            rows.Sum(r=>r.Invoice.Amounts.InsurerReceipts),rows.Sum(r=>r.Invoice.Amounts.InsurerEarnedOutstanding),0);
    }
    private static string State(InsurerAmounts amounts)=>amounts.Remaining==0?"Settled":amounts.Receipts>0?"Partial":amounts.Submitted==0?"Unsubmitted":"Unpaid";
    private async Task<InsurerAmounts> TotalsAsync(IReadOnlyList<InsuranceSession> rows,CancellationToken ct)
    {
        var ids=rows.Select(r=>r.Invoice.Id).ToArray();
        var receipts=await db.Payments.AsNoTracking().Where(p=>ids.Contains(p.InvoiceId) && p.Status==PaymentStatus.Paid &&
            (p.PaymentMethod==PaymentMethod.Insurance || p.PayerType=="Insurer" || p.PayerType=="Insurance"))
            .Select(p=>new{p.Id,p.InsuranceChequeReceiptId}).ToListAsync(ct);
        return Sum(rows) with {ReceiptCount=receipts.Select(p=>p.InsuranceChequeReceiptId is {} id?"cheque:"+id:"legacy:"+p.Id).Distinct().Count()};
    }
    private static int Skip(InsuranceFinanceQuery q)=>(int)Math.Min((long)(q.Page-1)*q.PageSize,int.MaxValue);
    public async Task<InsuranceCompanyPage> CompaniesAsync(InsuranceFinanceQuery q,CancellationToken ct=default)
    {
        var all=await RowsAsync(q,ct);var earned=all.Where(Delivered).ToList();
        if(q.ServiceMonth!=null)earned=InMonth(earned,q.ServiceMonth).ToList();
        var companies=await db.InsuranceCompanies.AsNoTracking().Select(c=>new{c.Id,c.Name}).ToListAsync(ct);
        var rows=companies.Select(c=>new InsuranceCompanyAccount(c.Id,c.Name,Sum(earned.Where(r=>r.Invoice.CompanyId==c.Id)),all.Count(r=>r.Invoice.CompanyId==c.Id && r.Invoice.AgreementId==null && r.Invoice.DeliveredAt==null))).ToList();
        if(all.Any(r=>r.Invoice.CompanyId==null))rows.Add(new(0,"Historical company unavailable",Sum(earned.Where(r=>r.Invoice.CompanyId==null)),all.Count(r=>r.Invoice.CompanyId==null && r.Invoice.DeliveredAt==null)));
        rows=rows.Where(c=>q.CompanyId==null || c.CompanyId==q.CompanyId)
            .Where(c=>string.IsNullOrWhiteSpace(q.CompanySearch) || c.Name.Contains(q.CompanySearch.Trim(),StringComparison.OrdinalIgnoreCase))
            .Where(c=>string.IsNullOrWhiteSpace(q.Search) && string.IsNullOrWhiteSpace(q.ClaimReference) || c.Amounts.Sessions>0)
            .Where(c=>q.PaymentState==null || (q.PaymentState=="Outstanding" ? c.Amounts.Remaining>0 : State(c.Amounts)==q.PaymentState)).OrderByDescending(c=>c.Amounts.Remaining).ThenBy(c=>c.Name,StringComparer.OrdinalIgnoreCase).ThenBy(c=>c.CompanyId).ToList();
        var matching=rows.Select(c=>c.CompanyId).ToHashSet();var selected=earned.Where(r=>matching.Contains(r.Invoice.CompanyId ?? 0)).ToList();
        return new(rows.Skip(Skip(q)).Take(q.PageSize).ToList(),rows.Count,selected.Count,q.Page,q.PageSize,await TotalsAsync(selected,ct),all.Count(r=>r.Invoice.CompanyId==null),all.Count(r=>r.Invoice.AgreementId==null && r.Invoice.DeliveredAt==null));
    }
    public async Task<InsuranceMonthAccount> AccountAsync(int companyId,InsuranceFinanceQuery q,CancellationToken ct=default)
    {
        q.CompanyId=companyId;var all=await RowsAsync(q,ct);
        var name=companyId==0?"Historical company unavailable":await db.InsuranceCompanies.Where(c=>c.Id==companyId).Select(c=>c.Name).SingleOrDefaultAsync(ct) ?? throw Rule("CompanyNotFound","Insurance company was not found.",404);
        var months=all.Where(Delivered).GroupBy(r=>MonthOf(r.Invoice.DeliveredAt!.Value)).Select(g=>new InsuranceMonth(g.Key,Sum(g))).OrderByDescending(m=>m.Month,StringComparer.Ordinal).ToList();
        var month=q.ServiceMonth ?? months.FirstOrDefault(m=>m.Amounts.Remaining>0)?.Month ?? months.FirstOrDefault()?.Month ?? Today.ToString("yyyy-MM",CultureInfo.InvariantCulture);
        var rows=InMonth(all,month).ToList();
        var patients=rows.GroupBy(r=>r.Invoice.PatientId).Select(g=>new InsurancePatientAccount(g.Key,g.First().Invoice.PatientName,null,Sum(g),State(Sum(g))))
            .Where(p=>q.PaymentState==null || (q.PaymentState=="Outstanding" ? p.Amounts.Remaining>0 : p.State==q.PaymentState)).OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).ThenBy(p=>p.PatientId).ToList();
        var patientIds=patients.Select(p=>p.PatientId).ToArray();var phones=await db.Patients.AsNoTracking().Where(p=>patientIds.Contains(p.Id)).Select(p=>new{p.Id,p.ApplicationUser.PhoneNumber}).ToDictionaryAsync(p=>p.Id,p=>p.PhoneNumber,ct);
        patients=patients.Select(p=>p with {Phone=phones.GetValueOrDefault(p.PatientId)}).ToList();rows=rows.Where(r=>patientIds.Contains(r.Invoice.PatientId)).ToList();
        return new(companyId,name,month,months,patients.Skip(Skip(q)).Take(q.PageSize).ToList(),patients.Count,rows.Count,q.Page,q.PageSize,await TotalsAsync(rows,ct),all.Count(r=>r.Invoice.AgreementId==null && r.Invoice.DeliveredAt==null));
    }
    public async Task<InsuranceSessionPage> SessionsAsync(int companyId,int patientId,InsuranceFinanceQuery q,CancellationToken ct=default)
    {
        q.CompanyId=companyId;var all=await RowsAsync(q,ct);var rows=InMonth(all,q.ServiceMonth ?? throw Rule("MonthRequired","Select a service month.")).Where(r=>r.Invoice.PatientId==patientId).OrderBy(r=>r.Invoice.DeliveredAt).ThenBy(r=>r.Invoice.Id).ToList();
        if(q.PaymentState!=null && (q.PaymentState=="Outstanding" ? Sum(rows).Remaining<=0 : State(Sum(rows))!=q.PaymentState))rows=[];
        return new(rows.Skip(Skip(q)).Take(q.PageSize).ToList(),rows.Count,q.Page,q.PageSize,await TotalsAsync(rows,ct));
    }
    public async Task<IReadOnlyList<InsuranceSession>> SelectedSessionsAsync(InsuranceFinanceQuery q,CancellationToken ct=default)
    {
        var rows=await RowsAsync(q,ct);rows=(q.ServiceMonth!=null?InMonth(rows,q.ServiceMonth):rows.Where(Delivered)).ToList();
        if(q.PaymentState!=null){var patients=rows.GroupBy(r=>r.Invoice.PatientId).Where(g=>q.PaymentState=="Outstanding" ? Sum(g).Remaining>0 : State(Sum(g))==q.PaymentState).Select(g=>g.Key).ToHashSet();rows=rows.Where(r=>patients.Contains(r.Invoice.PatientId)).ToList();}
        return rows.OrderBy(r=>r.Invoice.PatientName,StringComparer.OrdinalIgnoreCase).ThenBy(r=>r.Invoice.PatientId).ThenBy(r=>r.Invoice.DeliveredAt).ThenBy(r=>r.Invoice.Id).ToList();
    }
    public async Task<MonthlyClaimReceipt> SubmitMonthlyAsync(MonthlyClaimRequest request,string actor,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(actor))throw Rule("StaffRequired","Authenticated staff identity is required.",403);
        if(request.InvoiceIds==null || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length>90 || request.CompanyId<=0 || string.IsNullOrWhiteSpace(request.Reference) || request.Reference.Trim().Length>150 ||
            request.InvoiceIds.Count is <1 or >1000 || request.InvoiceIds.Any(id=>id<=0) || request.InvoiceIds.Distinct().Count()!=request.InvoiceIds.Count)throw Rule("InvalidClaimSubmission","Choose eligible invoices, a submission reference and a stable operation key.");
        var month=Month(request.ServiceMonth);var invoiceIds=request.InvoiceIds.Order().ToArray();var reference=request.Reference.Trim();var hash=Hash(new{request.CompanyId,month,invoiceIds,reference});
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);await ClinicMutationLock.AcquireAsync(db);
        var previous=await db.InsuranceClaimSubmissions.SingleOrDefaultAsync(s=>s.IdempotencyKey==request.IdempotencyKey,ct);
        if(previous!=null){if(previous.RequestHash!=hash)throw Rule("RetryKeyConflict","This operation key belongs to a different monthly submission.",409);return await SubmissionAsync(previous.Id,ct);}
        var rows=await SelectedSessionsAsync(new(){CompanyId=request.CompanyId,ServiceMonth=request.ServiceMonth},ct);
        if(invoiceIds.Any(id=>!rows.Any(r=>r.Invoice.Id==id && r.CanSubmitClaim)))throw Rule("ClaimEligibilityChanged","Some selected services have already been submitted or lack saved delivery/approval evidence. Refresh the month.",409);
        var submission=new InsuranceClaimSubmission{InsuranceCompanyId=request.CompanyId,ServiceMonth=month,IdempotencyKey=request.IdempotencyKey,RequestHash=hash,Reference=reference,RecordedBy=actor,RecordedAt=time.GetUtcNow().UtcDateTime};
        db.InsuranceClaimSubmissions.Add(submission);await db.SaveChangesAsync(ct);
        foreach(var id in invoiceIds){var claim=await finance.SubmitClaimCoreAsync(new("monthly:"+Hash(request.IdempotencyKey)+":"+id,id,reference),actor);claim.SubmissionBatchId=submission.Id;}
        await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);return await SubmissionAsync(submission.Id,ct);
    }
    public async Task<MonthlyClaimReceipt> SubmissionAsync(int id,CancellationToken ct=default)
    {
        var header=await db.InsuranceClaimSubmissions.AsNoTracking().SingleOrDefaultAsync(s=>s.Id==id,ct) ?? throw Rule("SubmissionNotFound","Monthly submission was not found.",404);
        var claims=await db.InsuranceClaims.AsNoTracking().Where(c=>c.SubmissionBatchId==id).OrderBy(c=>c.Id).ToListAsync(ct);
        return new(id,header.InsuranceCompanyId,header.ServiceMonth.ToString("yyyy-MM",CultureInfo.InvariantCulture),header.Reference,header.RecordedAt,header.RecordedBy,claims.Select(c=>c.Id).ToList(),claims.Sum(c=>c.Amount));
    }
    public async Task<MonthlyClaimReceipt> ReconcileSubmissionAsync(string key,CancellationToken ct=default)
    {
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);await ClinicMutationLock.AcquireAsync(db);
        var id=await db.InsuranceClaimSubmissions.Where(s=>s.IdempotencyKey==key).Select(s=>(int?)s.Id).SingleOrDefaultAsync(ct);
        return id==null?throw Rule("SubmissionNotFound","No committed submission was found. Retry the same saved operation.",404):await SubmissionAsync(id.Value,ct);
    }
    private void ValidateCheque(ChequeRequest request)
    {
        var errors=new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if(!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(request,new(request),errors,true) || request.ServiceMonths.Distinct().Count()!=request.ServiceMonths.Length || request.ClaimIds.Any(id=>id<=0) || request.ClaimIds.Distinct().Count()!=request.ClaimIds.Length ||
            !CourseFinanceService.Money(request.Amount) || request.Amount<=0 || request.ReceivedOn.Year<1900 || request.ReceivedOn>Today || request.ChequeDate is {} date && date.Year<1900 ||
            string.IsNullOrWhiteSpace(request.BankName) || string.IsNullOrWhiteSpace(request.ChequeNumber))throw Rule("InvalidCheque","Enter cheque number, bank, actual positive amount and a receipt date no later than the current clinic date.");
        foreach(var month in request.ServiceMonths)Month(month);
    }
    private static string Normal(string text)=>Regex.Replace(text.Normalize(NormalizationForm.FormKC).Trim(),@"\s+"," ").ToUpperInvariant();
    private static string ChequeIdentity(ChequeRequest r)=>Hash(new{r.CompanyId,Bank=Normal(r.BankName),Number=Normal(r.ChequeNumber)});
    private static string RequestIdentity(ChequeRequest r)=>Hash(new{r.CompanyId,Months=r.ServiceMonths.Order().ToArray(),Claims=r.ClaimIds.Order().ToArray(),Number=r.ChequeNumber.Trim(),Bank=r.BankName.Trim(),r.Amount,r.ReceivedOn,r.ChequeDate,Notes=r.Notes?.Trim()});
    public async Task<ChequePreview> PreviewAsync(ChequeRequest request,CancellationToken ct=default)
    {
        ValidateCheque(request);
        var duplicate=await db.InsuranceChequeReceipts.AsNoTracking().SingleOrDefaultAsync(r=>r.ChequeIdentityHash==ChequeIdentity(request),ct);
        if(duplicate!=null)throw Rule("DuplicateCheque","This company/bank/cheque-number combination is already recorded. Open the existing receipt instead of recording it again.",409,new{receiptId=duplicate.Id,duplicate.Amount,duplicate.ReceivedOn});
        var rows=await RowsAsync(new(){CompanyId=request.CompanyId},ct);
        var monthSet=request.ServiceMonths.ToHashSet(StringComparer.Ordinal);
        var eligible=rows.Where(r=>Delivered(r) && monthSet.Contains(MonthOf(r.Invoice.DeliveredAt!.Value)) && r.ClaimId!=null && r.Invoice.Amounts.InsurerEarnedOutstanding>0 && r.SubmittedAmount>r.Invoice.Amounts.InsurerReceipts)
            .Where(r=>request.ClaimIds.Length==0 || request.ClaimIds.Contains(r.ClaimId!.Value)).OrderBy(r=>r.Invoice.DeliveredAt).ThenBy(r=>r.Invoice.Id).ToList();
        if(request.ClaimIds.Any(id=>!eligible.Any(r=>r.ClaimId==id)))throw Rule("ClaimEligibilityChanged","A selected claim is outside this company/month, is already settled, or lacks delivered-service evidence.",409);
        if(eligible.Count==0)throw Rule("SubmittedClaimRequired","No eligible submitted claim is available. Submit the selected month's eligible services before recording a cheque.",409);
        var before=eligible.Sum(r=>Math.Min(r.SubmittedAmount,r.Invoice.Amounts.InsurerEarned)-r.Invoice.Amounts.InsurerReceipts);
        if(request.Amount>before)throw Rule("AmountExceedsBalance",$"The cheque amount exceeds the eligible selected balance by EGP {(request.Amount-before).ToString("F2",CultureInfo.InvariantCulture)}.",400,new{eligibleBalance=before,excess=request.Amount-before});
        var remaining=request.Amount;var allocations=new List<ChequeAllocation>();
        foreach(var row in eligible){var balance=Math.Max(0,Math.Min(row.SubmittedAmount,row.Invoice.Amounts.InsurerEarned)-row.Invoice.Amounts.InsurerReceipts);var amount=Math.Min(remaining,balance);if(amount<=0)continue;
            allocations.Add(new(0,row.ClaimId!.Value,row.Invoice.Id,row.Invoice.Reference,row.Invoice.PatientId,row.Invoice.PatientName,MonthOf(row.Invoice.DeliveredAt!.Value),row.Invoice.DeliveredAt.Value,row.ClaimReference ?? "",balance,amount,balance-amount));remaining-=amount;}
        var snapshot=eligible.Select(r=>new{r.ClaimId,r.Invoice.Id,Month=MonthOf(r.Invoice.DeliveredAt!.Value),Earned=r.Invoice.Amounts.InsurerEarned.ToString("F2",CultureInfo.InvariantCulture),Submitted=r.SubmittedAmount.ToString("F2",CultureInfo.InvariantCulture),Paid=r.Invoice.Amounts.InsurerReceipts.ToString("F2",CultureInfo.InvariantCulture)}).ToArray();
        var hash=Hash(new{request.CompanyId,Months=request.ServiceMonths.Order().ToArray(),Claims=request.ClaimIds.Order().ToArray(),Amount=request.Amount.ToString("F2",CultureInfo.InvariantCulture),snapshot});
        var company=await db.InsuranceCompanies.Where(c=>c.Id==request.CompanyId).Select(c=>c.Name).SingleOrDefaultAsync(ct) ?? throw Rule("CompanyNotFound","Insurance company was not found.",404);
        return new(request.CompanyId,company,request.ServiceMonths.Order().ToList(),request.Amount,before,before-request.Amount,hash,allocations);
    }
    public async Task<ChequeReceipt> RecordChequeAsync(ChequeRequest request,string actor,CancellationToken ct=default)
    {
        ValidateCheque(request);if(string.IsNullOrWhiteSpace(actor))throw Rule("StaffRequired","Authenticated staff identity is required.",403);
        var hash=RequestIdentity(request);
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);await ClinicMutationLock.AcquireAsync(db);
        var previous=await db.InsuranceChequeReceipts.SingleOrDefaultAsync(r=>r.IdempotencyKey==request.IdempotencyKey,ct);
        if(previous!=null){if(previous.RequestHash!=hash)throw Rule("RetryKeyConflict","This retry key belongs to different cheque details. Reconcile the original receipt.",409,new{receiptId=previous.Id});return await ReceiptAsync(previous.Id,ct);}
        var preview=await PreviewAsync(request,ct);
        if(request.ExpectedAllocationHash!=preview.Hash)throw Rule("StaleAllocation","The allocation changed or was not reviewed. Refresh and review the exact allocation before confirming.",409);
        var receipt=new InsuranceChequeReceipt{InsuranceCompanyId=request.CompanyId,IdempotencyKey=request.IdempotencyKey,RequestHash=hash,ChequeIdentityHash=ChequeIdentity(request),
            ChequeNumber=request.ChequeNumber.Trim(),BankName=request.BankName.Trim(),Amount=request.Amount,ReceivedOn=request.ReceivedOn,ChequeDate=request.ChequeDate,Notes=request.Notes?.Trim(),RecordedBy=actor,RecordedAt=time.GetUtcNow().UtcDateTime};
        db.InsuranceChequeReceipts.Add(receipt);await db.SaveChangesAsync(ct);
        foreach(var allocation in preview.Allocations)await finance.AllocateInsurerReceiptAsync(allocation.ClaimId,allocation.Amount,"Cheque",receipt.ChequeNumber,"cheque:"+Hash(request.IdempotencyKey)+":"+allocation.ClaimId,ClinicCalendar.DayStart(request.ReceivedOn),receipt.Id,Month(allocation.ServiceMonth),allocation.ServiceDate);
        await db.SaveChangesAsync(ct);
        var allocated=await db.Payments.Where(p=>p.InsuranceChequeReceiptId==receipt.Id).ToListAsync(ct);
        if(allocated.Sum(p=>p.Amount)!=receipt.Amount)throw new InvalidOperationException("Cheque allocations do not reconcile.");
        await transaction.CommitAsync(ct);return await ReceiptAsync(receipt.Id,ct);
    }
    public async Task<ChequeReceipt> ReceiptAsync(int id,CancellationToken ct=default)
    {
        var receipts=await ReceiptsAsync([id],ct);
        return receipts.GetValueOrDefault(id) ?? throw Rule("ReceiptNotFound","Cheque receipt was not found.",404);
    }
    private async Task<Dictionary<int,ChequeReceipt>> ReceiptsAsync(int[] ids,CancellationToken ct)
    {
        if(ids.Length==0)return [];
        var headers=await db.InsuranceChequeReceipts.AsNoTracking().Where(r=>ids.Contains(r.Id)).ToListAsync(ct);
        // Receipt evidence must retain archived identities and its complete original allocation scope.
        var payments=await db.Payments.IgnoreQueryFilters().AsNoTracking().Include(p=>p.Invoice).ThenInclude(i=>i.Patient).ThenInclude(p=>p.ApplicationUser)
            .Where(p=>p.InsuranceChequeReceiptId!=null && ids.Contains(p.InsuranceChequeReceiptId.Value)).OrderBy(p=>p.Id).ToListAsync(ct);
        var claimIds=payments.Select(p=>p.InsuranceClaimId).ToArray();
        var claims=await db.InsuranceClaims.IgnoreQueryFilters().AsNoTracking().Where(c=>claimIds.Contains(c.Id)).ToDictionaryAsync(c=>c.Id,ct);
        var companyIds=headers.Select(h=>h.InsuranceCompanyId).Distinct().ToArray();
        var companies=await db.InsuranceCompanies.IgnoreQueryFilters().AsNoTracking().Where(c=>companyIds.Contains(c.Id)).ToDictionaryAsync(c=>c.Id,c=>c.Name,ct);
        var byReceipt=payments.ToLookup(p=>p.InsuranceChequeReceiptId!.Value);
        return headers.ToDictionary(h=>h.Id,h=>new ChequeReceipt(h.Id,h.IdempotencyKey,h.InsuranceCompanyId,companies.GetValueOrDefault(h.InsuranceCompanyId) ?? "Historical company unavailable",
            h.ChequeNumber,h.BankName,h.Amount,h.ReceivedOn,h.ChequeDate,h.Notes,h.RecordedBy,h.RecordedAt,
            byReceipt[h.Id].Select(p=>new ChequeAllocation(p.Id,p.InsuranceClaimId!.Value,p.InvoiceId,p.Invoice.InvoiceNumber,p.Invoice.PatientId,p.Invoice.Patient.ApplicationUser.FullName,
                p.InsuranceServiceMonth!.Value.ToString("yyyy-MM",CultureInfo.InvariantCulture),p.InsuranceServiceDeliveredAt!.Value,claims[p.InsuranceClaimId.Value].SubmissionReference,
                p.InsurerBalanceBefore!.Value,p.Amount,p.InsurerBalanceBefore.Value-p.Amount)).ToList()));
    }
    public async Task<ChequeReceipt> ReconcileChequeAsync(string key,CancellationToken ct=default)
    {
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);await ClinicMutationLock.AcquireAsync(db);
        var id=await db.InsuranceChequeReceipts.Where(r=>r.IdempotencyKey==key).Select(r=>(int?)r.Id).SingleOrDefaultAsync(ct);
        return id==null?throw Rule("ReceiptNotFound","No committed cheque was found. Retry only the original saved operation.",404):await ReceiptAsync(id.Value,ct);
    }
    public async Task<InsuranceReceiptHistory> HistoryAsync(InsuranceFinanceQuery q,CancellationToken ct=default)
    {
        var rows=await SelectedSessionsAsync(q,ct);var ids=rows.Select(r=>r.Invoice.Id).ToArray();
        var start=q.From is {} from?ClinicCalendar.DayStart(from):(DateTime?)null;var end=q.To is {} to?ClinicCalendar.DayStart(to.AddDays(1)):(DateTime?)null;
        var payments=await db.Payments.AsNoTracking().Include(p=>p.Invoice).Where(p=>ids.Contains(p.InvoiceId) && p.Status==PaymentStatus.Paid &&
            (p.PaymentMethod==PaymentMethod.Insurance || p.PayerType=="Insurer" || p.PayerType=="Insurance") && (start==null || p.PaymentDate>=start) && (end==null || p.PaymentDate<end)).ToListAsync(ct);
        var chequeIds=payments.Where(p=>p.InsuranceChequeReceiptId!=null).Select(p=>p.InsuranceChequeReceiptId!.Value).Distinct().ToArray();
        var headers=await db.InsuranceChequeReceipts.AsNoTracking().Where(r=>chequeIds.Contains(r.Id)).ToListAsync(ct);
        var legacy=payments.Where(p=>p.InsuranceChequeReceiptId==null).Select(p=>new InsurerLegacyReceipt(p.Id,p.InvoiceId,p.Invoice.InvoiceNumber,p.InsuranceClaimId,p.TransactionId,p.Amount,p.PaymentDate,p.CollectionMethod ?? p.PaymentMethod.ToString())).ToList();
        var ordered=headers.Select(h=>(Date:h.RecordedAt,Id:h.Id,Cheque:true)).Concat(legacy.Select(p=>(Date:p.ReceivedAt,Id:p.PaymentId,Cheque:false))).OrderByDescending(i=>i.Date).ThenByDescending(i=>i.Id).ToList();
        var page=ordered.Skip(Skip(q)).Take(q.PageSize).ToList();var receipts=await ReceiptsAsync(page.Where(p=>p.Cheque).Select(p=>p.Id).ToArray(),ct);
        var result=page.Where(p=>p.Cheque).Select(p=>receipts[p.Id]).ToList();
        return new(result,ordered.Count,q.Page,q.PageSize,headers.Sum(h=>h.Amount)+legacy.Sum(p=>p.Amount),payments.Sum(p=>p.Amount),legacy.Where(p=>page.Any(i=>!i.Cheque && i.Id==p.PaymentId)).ToList(),legacy.Sum(p=>p.Amount));
    }
    public async Task<byte[]> StatementAsync(int companyId,InsuranceFinanceQuery q,CancellationToken ct=default)
    {
        q.CompanyId=companyId;var month=q.ServiceMonth ?? throw Rule("MonthRequired","Select a service month.");
        var rows=await SelectedSessionsAsync(q,ct);var totals=await TotalsAsync(rows.ToList(),ct);var company=companyId==0?"Historical company unavailable":await db.InsuranceCompanies.Where(c=>c.Id==companyId).Select(c=>c.Name).SingleOrDefaultAsync(ct) ?? throw Rule("CompanyNotFound","Insurance company was not found.",404);
        var csv=new FinancialCsv();csv.Line("Company",company,"Company ID",companyId);csv.Line("Service month",month,"Timezone","Africa/Cairo");csv.Line("Generated UTC",time.GetUtcNow().ToString("O"),"Currency","EGP");csv.Line("Filters",JsonSerializer.Serialize(q));
        csv.Line("Delivered sessions",totals.Sessions,"Earned insurer liability",totals.Earned,"Submitted claims",totals.Submitted,"Actual insurer allocations",totals.Receipts,"Earned remaining",totals.Remaining,"Receipt count",totals.ReceiptCount);
        csv.Line("Patient ID","Patient","Service UTC","Invoice ID","Invoice reference","Claim ID","Claim reference","Claim state","Earned insurer liability","Actual insurer allocations","Remaining insurer balance","Patient share");
        foreach(var r in rows)csv.Line(r.Invoice.PatientId,r.Invoice.PatientName,r.Invoice.DeliveredAt!.Value.ToString("O"),r.Invoice.Id,r.Invoice.Reference,r.ClaimId,r.ClaimReference,r.ClaimState,r.Invoice.Amounts.InsurerEarned,r.Invoice.Amounts.InsurerReceipts,r.Invoice.Amounts.InsurerEarnedOutstanding,r.Invoice.PatientShare);
        return csv.Bytes();
    }
}
