using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Billing;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

/// <summary>One exact-decimal accounting pipeline shared by patient pages, invoice expansion and exports.
/// Receipt rows are allocations of cash, never joined to invoice liability aggregates.</summary>
public sealed class BillingQueryService(ApplicationDbContext db)
{
    private sealed record Row(Invoice Invoice, TreatmentCourseAgreement? Agreement, InsuranceClaim? Claim,
        int? CompanyId, string? Company, int? PlanId, SessionStatus? SessionStatus, string? ClinicianId, string? Clinician);
    private sealed record Scope(List<BillingInvoice> Invoices, List<BillingPatient> Patients, BillingAmounts Totals);

    private static void Validate(BillingQuery q, bool finance)
    {
        var errors = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(q, new(q), errors, true) ||
            q.From > q.To || q.From == DateOnly.MinValue || q.To == DateOnly.MinValue || q.To == DateOnly.MaxValue || q.CompanyIds.Any(id => id <= 0))
            throw new BookingRuleException("Billing.InvalidFilter", "Select valid billing filters and an ordered date range.", 400);
        if (q.ClaimStatus != null && !finance || q.Method == PaymentMethod.Insurance && !finance)
            throw new BookingRuleException("Billing.FinanceOnly", "Insurer financial reporting requires finance permission.", 403);
        if(q.Method==PaymentMethod.Insurance) throw new BookingRuleException("Billing.InvalidFilter","Insurance identifies a payer, not an actual collection method.",400);
    }
    private static string Funding(Row r) => r.Agreement?.FundingType ?? (r.Invoice.InsuranceAmount > 0 ? "Insurance" : "SelfPay");
    private static bool Active(Invoice i) => i.Status is not (InvoiceStatus.Waived or InvoiceStatus.Cancelled);
    private static DateTime? Delivered(Invoice i) => i.DeliveredAt ?? (i.Appointment?.Status == AppointmentStatus.Completed ? i.Appointment.ActualCheckOut : null);
    private static string PatientStatus(decimal liability, decimal paid, bool active) => !active ? "NonCollectible" : paid >= liability ? "Settled" : paid > 0 ? "Partial" : "Unpaid";
    private static BillingAmounts Sum(IEnumerable<BillingAmounts> amounts) => new(amounts.Sum(a=>a.Agreed), amounts.Sum(a=>a.PatientLiability),
        amounts.Sum(a=>a.PatientReceipts), amounts.Sum(a=>a.PatientRemaining), amounts.Sum(a=>a.CollectedInPeriod), amounts.Sum(a=>a.InsurerPlanned),
        amounts.Sum(a=>a.InsurerEarned), amounts.Sum(a=>a.ClaimsSubmitted), amounts.Sum(a=>a.InsurerReceipts), amounts.Sum(a=>a.InsurerEarnedOutstanding), 0);

    private async Task<Scope> ReadAsync(BillingQuery q, bool finance, CancellationToken ct, int? patientId = null)
    {
        Validate(q, finance);
        var start = q.From is {} from ? ClinicCalendar.DayStart(from) : (DateTime?)null;
        var end = q.To is {} to ? ClinicCalendar.DayStart(to.AddDays(1)) : (DateTime?)null;
        var query = db.Invoices.AsNoTracking().AsQueryable();
        if(patientId!=null) query=query.Where(i=>i.PatientId==patientId);
        if (!string.IsNullOrWhiteSpace(q.Search)) { var term = q.Search.Trim(); query = query.Where(i=>i.Patient.ApplicationUser.FullName.Contains(term) || i.Patient.ApplicationUser.PhoneNumber != null && i.Patient.ApplicationUser.PhoneNumber.Contains(term)); }
        if (q.Type != null) query = query.Where(i=>i.Type == q.Type);
        if (q.Status != null) query = query.Where(i=>i.Status == q.Status);
        if (q.AppointmentStatus != null) query = query.Where(i=>i.Appointment != null && i.Appointment.Status == q.AppointmentStatus);
        if(q.ClinicianId!=null) query=query.Where(i=>i.Appointment!=null && i.Appointment.TreatmentStartedBy==q.ClinicianId);
        if(q.PricingMode is "Package" or "PerSession") query=query.Where(i=>db.TreatmentCourseAgreements.Any(a=>a.Id==i.CourseAgreementId && a.PricingMode==q.PricingMode));
        if(q.CompanyIds.Length>0) query=query.Where(i=>db.TreatmentCourseAgreements.Any(a=>a.Id==i.CourseAgreementId && a.InsuranceCompanyId!=null && q.CompanyIds.Contains(a.InsuranceCompanyId.Value)) ||
            !db.TreatmentCourseAgreements.Any(a=>a.Id==i.CourseAgreementId && a.InsuranceCompanyId!=null) && db.InsuranceClaims.Any(c=>c.InvoiceId==i.Id && q.CompanyIds.Contains(c.InsuranceCompanyId)));
        if(q.DateBasis=="Service") {
            query=query.Where(i=>i.DeliveredAt!=null || i.Appointment!=null && i.Appointment.Status==AppointmentStatus.Completed && i.Appointment.ActualCheckOut!=null);
            if(start!=null)query=query.Where(i=>(i.DeliveredAt ?? i.Appointment!.ActualCheckOut)>=start);
            if(end!=null)query=query.Where(i=>(i.DeliveredAt ?? i.Appointment!.ActualCheckOut)<end);
        }
        if (q.DateBasis == "Invoice") {
            if (start != null) query = query.Where(i=>i.CreatedAt >= start);
            if (end != null) query = query.Where(i=>i.CreatedAt < end);
        }
        // Push structural/date filters into SQL; exact monetary aggregation stays decimal across providers.
        var invoices = await query.Include(i=>i.Patient).ThenInclude(p=>p.ApplicationUser).Include(i=>i.Appointment)
            .Include(i=>i.Payments).AsSplitQuery().ToListAsync(ct);
        var batchIds=invoices.Where(i=>i.Appointment?.BookingBatchId!=null).Select(i=>i.Appointment!.BookingBatchId!.Value).Distinct().ToArray();
        var batchKeys=await db.BookingBatches.AsNoTracking().Where(b=>batchIds.Contains(b.Id)).ToDictionaryAsync(b=>b.Id,b=>b.IdempotencyKey,ct);
        var ids = invoices.Select(i=>i.Id).ToArray();
        var agreementIds = invoices.Where(i=>i.CourseAgreementId != null).Select(i=>i.CourseAgreementId!.Value).Distinct().ToArray();
        var agreements = await db.TreatmentCourseAgreements.AsNoTracking().Where(a=>agreementIds.Contains(a.Id)).ToDictionaryAsync(a=>a.Id, ct);
        var claims = await db.InsuranceClaims.AsNoTracking().Where(c=>ids.Contains(c.InvoiceId)).ToDictionaryAsync(c=>c.InvoiceId, ct);
        var companies = await db.InsuranceCompanies.AsNoTracking().ToDictionaryAsync(c=>c.Id,c=>c.Name,ct);
        var sessionIds = invoices.Select(i=>i.TreatmentPlanSessionId ?? i.Appointment?.TreatmentPlanSessionId).Where(id=>id!=null).Select(id=>id!.Value).Distinct().ToArray();
        var sessions = await db.TreatmentPlanSessions.AsNoTracking().Where(s=>sessionIds.Contains(s.Id)).ToDictionaryAsync(s=>s.Id,ct);
        var clinicianIds = invoices.Select(i=>i.Appointment?.TreatmentStartedBy).Where(id=>id!=null).ToArray();
        var clinicians = await db.Users.AsNoTracking().Where(u=>clinicianIds.Contains(u.Id)).ToDictionaryAsync(u=>u.Id,u=>u.FullName,ct);
        var rows = invoices.Select(i=>{
            var a = i.CourseAgreementId is {} aid ? agreements.GetValueOrDefault(aid) : null;
            var claim = claims.GetValueOrDefault(i.Id);
            var company = a?.InsuranceCompanyId ?? claim?.InsuranceCompanyId;
            var sessionId = i.TreatmentPlanSessionId ?? i.Appointment?.TreatmentPlanSessionId;
            var session = sessionId is {} sid ? sessions.GetValueOrDefault(sid) : null;
            var clinician = i.Appointment?.TreatmentStartedBy; // Assigned clinician is not evidence of who treated.
            return new Row(i,a,claim,company,company is {} cid ? companies.GetValueOrDefault(cid) : null,a?.TreatmentPlanId ?? session?.TreatmentPlanId,session?.Status,clinician,clinician==null ? null : clinicians.GetValueOrDefault(clinician));
        }).Where(r=>q.Funding == null || Funding(r) == q.Funding)
          .Where(r=>q.CompanyIds.Length==0 || r.CompanyId is {} company && q.CompanyIds.Contains(company))
          .Where(r=>q.PricingMode==null || (r.Agreement?.PricingMode ?? "Unknown")==q.PricingMode)
          .Where(r=>q.PlanId==null || r.PlanId==q.PlanId)
          .Where(r=>q.SessionStatus==null || r.SessionStatus==q.SessionStatus)
          .Where(r=>q.ClinicianId==null || r.ClinicianId==q.ClinicianId)
          .Where(r=>q.DateBasis!="Service" || Delivered(r.Invoice) is {} day && (start==null || day>=start) && (end==null || day<end))
          .ToList();
        bool ReceiptInScope(Payment p) => p.Status==PaymentStatus.Paid && (finance || CourseFinanceService.PatientReceipt(p)) &&
            (start==null || p.PaymentDate>=start) && (end==null || p.PaymentDate<end) &&
            (q.Method==null || (p.CollectionMethod ?? p.PaymentMethod.ToString())==q.Method.ToString());
        rows = rows.Where(r=>(q.DateBasis!="Collection" || r.Invoice.Payments.Any(ReceiptInScope)) &&
            (q.Method==null || r.Invoice.Payments.Any(p=>p.Status==PaymentStatus.Paid && (finance || CourseFinanceService.PatientReceipt(p)) &&
                (p.CollectionMethod ?? p.PaymentMethod.ToString())==q.Method.ToString() && (q.DateBasis!="Collection" || ReceiptInScope(p)))))
            .Where(r=>q.ClaimStatus==null || (q.ClaimStatus=="Outstanding" ? r.Claim!=null && ClaimStatus(r)!="Settled" : ClaimStatus(r)==q.ClaimStatus)).ToList();
        var details = rows.Select(r=>{
            var i=r.Invoice; var paid=i.Payments.Where(CourseFinanceService.PatientReceipt).Sum(p=>p.Amount);
            var insurer=i.Payments.Where(CourseFinanceService.InsurerReceipt).Sum(p=>p.Amount);
            var earned=Active(i) && Delivered(i)!=null ? i.InsuranceAmount : 0;
            var remaining=Active(i) ? Math.Max(0,i.PatientAmount-paid) : 0;
            var amounts=new BillingAmounts(i.TotalAmount,Active(i) ? i.PatientAmount : 0,paid,remaining,
                q.DateBasis=="Collection" ? i.Payments.Where(ReceiptInScope).Sum(p=>p.Amount) : 0,
                finance && Active(i) ? i.InsuranceAmount : 0,finance ? earned : 0,finance ? r.Claim?.Amount ?? 0 : 0,
                finance ? insurer : 0,finance ? Math.Max(0,earned-insurer) : 0,0);
            return new BillingInvoice(i.Id,i.InvoiceNumber,i.PatientId,i.Patient.ApplicationUser.FullName,i.CreatedAt,Delivered(i),Funding(r),r.CompanyId,r.Company,
                r.Agreement?.PricingMode ?? "Unknown",r.PlanId,i.CourseAgreementId,i.TreatmentPlanSessionId ?? i.Appointment?.TreatmentPlanSessionId,
                r.ClinicianId,r.Clinician,i.Type,i.Status,i.Appointment?.Status,r.SessionStatus,amounts,PatientStatus(i.PatientAmount,paid,Active(i)),
                Active(i) && paid+insurer>=i.TotalAmount, !Active(i) ? i.Status.ToString() : remaining==0 ? "Patient share fully paid" : null)
                { PatientShare=i.PatientAmount, InsurerShare=i.InsuranceAmount };
        }).ToList();
        var phones=rows.GroupBy(r=>r.Invoice.PatientId).ToDictionary(g=>g.Key,g=>g.First().Invoice.Patient.ApplicationUser.PhoneNumber);
        var patients=details.GroupBy(i=>i.PatientId).Select(g=>{
            var amounts=Sum(g.Select(i=>i.Amounts));
            var active=g.Any(i=>i.CollectionBlockedReason is not ("Waived" or "Cancelled"));
            return new BillingPatient(g.Key,g.First().PatientName,phones[g.Key],g.Select(i=>i.Funding).Distinct().Count()>1 ? "Mixed" : g.First().Funding,
                g.Where(i=>i.Funding=="Insurance").Select(i=>i.Company ?? "Historical company unavailable").Distinct().Order().ToArray(),g.Count(),amounts,
                PatientStatus(amounts.PatientReceipts+amounts.PatientRemaining,amounts.PatientReceipts,active),g.All(i=>i.FullySettled));
        }).Where(p=>q.PatientStatus==null || p.PatientStatus==q.PatientStatus).OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).ThenBy(p=>p.PatientId).ToList();
        var patientsInScope=patients.Select(p=>p.PatientId).ToHashSet();
        details=details.Where(i=>patientsInScope.Contains(i.PatientId)).OrderByDescending(i=>i.CreatedAt).ThenByDescending(i=>i.Id).ToList();
        var totals=Sum(details.Select(i=>i.Amounts));
        // Course receipts are persisted as allocation rows. Count the original operation once, sum only its scoped allocations.
        var detailIds=details.Select(i=>i.Id).ToHashSet();
        var receipts=rows.Where(r=>detailIds.Contains(r.Invoice.Id)).SelectMany(r=>r.Invoice.Payments.Select(p=>(Payment:p,Invoice:r.Invoice)))
            .Where(item=>q.DateBasis=="Collection" ? ReceiptInScope(item.Payment) : CourseFinanceService.PatientReceipt(item.Payment));
        totals=totals with { ReceiptCount=receipts.Select(item=>ReceiptIdentity(item.Payment,item.Invoice,batchKeys)).Distinct().Count() };
        return new(details,patients,totals);
    }
    private static string ReceiptIdentity(Payment p,Invoice invoice,IReadOnlyDictionary<int,string> batchKeys) {
        if(p.InsuranceChequeReceiptId is {} cheque)return "cheque:"+cheque;
        var key=p.IdempotencyKey;
        var separator=key?.LastIndexOf(':') ?? -1;
        if(separator>0 && (key!.StartsWith("course:") || key.StartsWith("package:")) && int.TryParse(key[(separator+1)..],out _))return key[..separator];
        if(invoice.Appointment?.BookingBatchId is {} bid && batchKeys.TryGetValue(bid,out var batch) && key?.StartsWith(batch+":")==true && int.TryParse(key[(batch.Length+1)..],out _)) return "booking:"+batch;
        return key ?? $"payment:{p.Id}";
    }
    private static string ClaimStatus(Row r) {
        if(r.Claim==null) return "Unsubmitted";
        var received=r.Invoice.Payments.Where(CourseFinanceService.InsurerReceipt).Sum(p=>p.Amount);
        return received>=r.Claim.Amount ? "Settled" : received>0 ? "Partial" : "Submitted";
    }
    public async Task<BillingPage<BillingPatient>> PatientsAsync(BillingQuery q,bool finance,CancellationToken ct=default) {
        var s=await ReadAsync(q,finance,ct);
        return new(s.Patients.Skip((int)Math.Min((long)(q.Page-1)*q.PageSize,int.MaxValue)).Take(q.PageSize).ToList(),s.Patients.Count,s.Invoices.Count,q.Page,q.PageSize,s.Totals,q.DateBasis);
    }
    public async Task<IReadOnlyList<BillingInvoice>> MatchingInvoicesAsync(BillingQuery q,bool finance,CancellationToken ct=default) => (await ReadAsync(q,finance,ct)).Invoices;
    public async Task<BillingInvoice> InvoiceContextAsync(int invoiceId,bool finance,CancellationToken ct=default) {
        var patientId=await db.Invoices.Where(i=>i.Id==invoiceId).Select(i=>(int?)i.PatientId).SingleOrDefaultAsync(ct);
        if(patientId==null)throw new BookingRuleException("Billing.InvoiceNotFound","Invoice was not found.",404);
        return (await ReadAsync(new BillingQuery(),finance,ct,patientId)).Invoices.Single(i=>i.Id==invoiceId);
    }
    public async Task<BillingPage<BillingInvoice>> InvoicesAsync(int patientId,BillingQuery q,bool finance,CancellationToken ct=default) {
        var s=await ReadAsync(q,finance,ct,patientId);var invoices=s.Invoices;
        return new(invoices.Skip((int)Math.Min((long)(q.Page-1)*q.PageSize,int.MaxValue)).Take(q.PageSize).ToList(),invoices.Count>0 ? 1 : 0,invoices.Count,q.Page,q.PageSize,Sum(invoices.Select(i=>i.Amounts)),q.DateBasis);
    }
    public async Task<BillingOptions> OptionsAsync(CancellationToken ct=default) => new(
        await db.InsuranceCompanies.AsNoTracking().OrderBy(c=>c.Name).Select(c=>new BillingOption(c.Id.ToString(),c.Name)).ToListAsync(ct),
        await db.TreatmentCourseAgreements.AsNoTracking().OrderBy(a=>a.TreatmentPlanId).Select(a=>new BillingOption(a.TreatmentPlanId.ToString(),"Plan #"+a.TreatmentPlanId+" / account #"+a.Id)).ToListAsync(ct),
        await db.Users.AsNoTracking().Where(u=>db.Appointments.Any(a=>a.TreatmentStartedBy==u.Id)).OrderBy(u=>u.FullName).Select(u=>new BillingOption(u.Id,u.FullName)).ToListAsync(ct));
    public async Task<byte[]> ExportAsync(BillingQuery q,bool finance,string kind,CancellationToken ct=default) {
        if(kind is not ("patients" or "invoices")) throw new BookingRuleException("Billing.InvalidExport","Choose patients or invoices.",400);
        var s=await ReadAsync(q,finance,ct);var csv=new FinancialCsv();
        void Line(params object?[] values) => csv.Line(values);
        Line("Exported UTC",DateTime.UtcNow.ToString("O"));Line("Clinic timezone","Africa/Cairo");Line("Currency","EGP");Line("Date basis",q.DateBasis);Line("Scope","All matching records; page parameters do not limit export");
        Line("Applied filters",JsonSerializer.Serialize(q));Line("Matching patients",s.Patients.Count,"Matching invoices",s.Invoices.Count);
        Line("Agreed",s.Totals.Agreed,"Patient liability",s.Totals.PatientLiability,"Lifetime patient receipts",s.Totals.PatientReceipts,"Patient remaining",s.Totals.PatientRemaining,"Collected in period",s.Totals.CollectedInPeriod,"Receipt count",s.Totals.ReceiptCount);
        if(finance) Line("Planned insurer liability",s.Totals.InsurerPlanned,"Earned insurer receivables",s.Totals.InsurerEarned,"Submitted claims",s.Totals.ClaimsSubmitted,"Actual insurer receipts",s.Totals.InsurerReceipts,"Earned outstanding balance",s.Totals.InsurerEarnedOutstanding);
        if(kind=="patients") { Line("Patient ID","Patient","Phone","Funding","Companies","Invoices","Agreed","Patient liability","Lifetime patient receipts","Patient remaining","Collected in period","Patient status","Invoice fully settled");
            foreach(var p in s.Patients) Line(p.PatientId,p.Name,p.Phone,p.Funding,string.Join("; ",p.Companies),p.InvoiceCount,p.Amounts.Agreed,p.Amounts.PatientLiability,p.Amounts.PatientReceipts,p.Amounts.PatientRemaining,p.Amounts.CollectedInPeriod,p.PatientStatus,p.FullySettled);
        } else { Line("Invoice ID","Reference","Patient ID","Patient","Invoice UTC","Delivery UTC","Funding","Historical company","Plan","Account","Pricing","Type","Invoice status","Agreed","Original patient share","Patient liability","Insurer share","Lifetime patient receipts","Patient remaining","Collected in period","Actual clinician","Patient status","Invoice fully settled");
            foreach(var i in s.Invoices) Line(i.Id,i.Reference,i.PatientId,i.PatientName,i.CreatedAt.ToString("O"),i.DeliveredAt?.ToString("O"),i.Funding,i.Company ?? "Unavailable",i.PlanId,i.AgreementId,i.PricingMode,i.Type,i.Status,i.Amounts.Agreed,i.PatientShare,i.Amounts.PatientLiability,i.InsurerShare,i.Amounts.PatientReceipts,i.Amounts.PatientRemaining,i.Amounts.CollectedInPeriod,i.ClinicianName ?? "Unavailable",i.PatientStatus,i.FullySettled);
        }
        return csv.Bytes();
    }
}
