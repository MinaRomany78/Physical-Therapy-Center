using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Ceo;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Infrastructure.Services;

/// <summary>SQL projections, aggregates and bounded pages. No Angular monetary calculations.</summary>
public sealed class CeoReadService(ApplicationDbContext db, TimeProvider clock)
{
    public static readonly Expression<Func<Payment, bool>> InsurerPaid = p => p.Status == PaymentStatus.Paid &&
        (p.PaymentMethod == PaymentMethod.Insurance || p.PayerType == "Insurer" || p.PayerType == "Insurance");
    public static readonly Expression<Func<Payment, bool>> PatientPaid = p => p.Status == PaymentStatus.Paid &&
        p.PaymentMethod != PaymentMethod.Insurance && p.PayerType != "Insurer" && p.PayerType != "Insurance";

    private sealed class Ledger
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public int PatientId { get; set; }
        public string Patient { get; set; } = "";
        public int? CompanyId { get; set; }
        public string? Company { get; set; }
        public string? ClinicianId { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public decimal PatientLiability { get; set; }
        public decimal PatientReceipts { get; set; }
        public decimal Planned { get; set; }
        public decimal Earned { get; set; }
        public decimal InsurerReceipts { get; set; }
        public int? ClaimId { get; set; }
        public bool Active { get; set; }
        public bool CanSubmit { get; set; }
    }
    private IQueryable<Ledger> LedgerQuery() => db.Invoices.AsNoTracking().Select(i => new Ledger {
        Id = i.Id, Reference = i.InvoiceNumber, PatientId = i.PatientId,
        // Archived identity remains valid evidence; never substitute current insurer membership.
        Patient = db.Patients.IgnoreQueryFilters().Where(p => p.Id == i.PatientId).Select(p => p.ApplicationUser.FullName).FirstOrDefault() ?? "Unavailable",
        CompanyId = db.TreatmentCourseAgreements.Where(a => a.Id == i.CourseAgreementId).Select(a => a.InsuranceCompanyId).FirstOrDefault()
            ?? db.InsuranceClaims.Where(c => c.InvoiceId == i.Id).Select(c => (int?)c.InsuranceCompanyId).FirstOrDefault(),
        ClinicianId = i.Appointment == null ? null : i.Appointment.TreatmentStartedBy,
        DeliveredAt = i.DeliveredAt ?? (i.Appointment != null && i.Appointment.Status == AppointmentStatus.Completed ? i.Appointment.ActualCheckOut : null),
        Active = i.Status != InvoiceStatus.Waived && i.Status != InvoiceStatus.Cancelled,
        PatientLiability = i.Status != InvoiceStatus.Waived && i.Status != InvoiceStatus.Cancelled ? i.PatientAmount : 0,
        Planned = i.Status != InvoiceStatus.Waived && i.Status != InvoiceStatus.Cancelled ? i.InsuranceAmount : 0,
        Earned = i.Status != InvoiceStatus.Waived && i.Status != InvoiceStatus.Cancelled &&
            (i.DeliveredAt != null || i.Appointment != null && i.Appointment.Status == AppointmentStatus.Completed && i.Appointment.ActualCheckOut != null) ? i.InsuranceAmount : 0,
        PatientReceipts = i.Payments.AsQueryable().Where(PatientPaid).Sum(p => (decimal?)p.Amount) ?? 0,
        InsurerReceipts = i.Payments.AsQueryable().Where(InsurerPaid).Sum(p => (decimal?)p.Amount) ?? 0,
        ClaimId = db.InsuranceClaims.Where(c => c.InvoiceId == i.Id).Select(c => (int?)c.Id).FirstOrDefault()
        ,CanSubmit = i.DeliveredAt != null && i.TreatmentPlanSessionId != null &&
            db.TreatmentCourseAgreements.Any(a => a.Id == i.CourseAgreementId && a.InsuranceCompanyId != null && a.ApprovalReference != null)
    });

    private IQueryable<Ledger> Scope(CeoQuery q)
    {
        var rows = LedgerQuery();
        if (q.CompanyId != null) rows = rows.Where(i => i.CompanyId == q.CompanyId);
        if (q.PatientId != null) rows = rows.Where(i => i.PatientId == q.PatientId);
        if (!string.IsNullOrWhiteSpace(q.Search)) rows = rows.Where(i => i.Patient.Contains(q.Search.Trim()));
        if (q.ClinicianId != null) rows = rows.Where(i => i.ClinicianId == q.ClinicianId);
        if (q.Funding == "Insurance") rows = rows.Where(i => i.Planned > 0 || i.CompanyId != null);
        if (q.Funding == "SelfPay") rows = rows.Where(i => i.Planned == 0 && i.CompanyId == null);
        return rows;
    }

    public (DateOnly From, DateOnly To, DateTime Start, DateTime End) Period(CeoQuery q)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), ClinicCalendar.Zone).DateTime);
        var from = q.From ?? new DateOnly(today.Year, today.Month, 1); var to = q.To ?? today;
        if (from > to || from.Year < 2000 || to.Year > 2100 || to.DayNumber - from.DayNumber > 366)
            throw new BookingRuleException("Ceo.InvalidPeriod", "Choose an ordered Cairo period of at most 367 days between 2000 and 2100.");
        return (from, to, ClinicCalendar.DayStart(from), ClinicCalendar.DayStart(to.AddDays(1)));
    }

    private IQueryable<Payment> Payments(CeoQuery q, bool insurer, DateTime start, DateTime end)
    {
        var ids = Scope(q).Select(i => i.Id);
        var payments = db.Payments.AsNoTracking().Where(insurer ? InsurerPaid : PatientPaid)
            .Where(p => p.PaymentDate >= start && p.PaymentDate < end && ids.Contains(p.InvoiceId));
        if (q.Method != null)
        {
            var method = Enum.TryParse<PaymentMethod>(q.Method, out var parsed) ? parsed : (PaymentMethod)(-1);
            payments = payments.Where(p => p.CollectionMethod == q.Method || p.CollectionMethod == null && p.PaymentMethod == method);
        }
        return payments;
    }
    private static async Task<ReportTotals> Totals(IQueryable<ReportRow> rows, bool clinical, CancellationToken ct)
    {
        if (!clinical) return await rows.GroupBy(_ => 1).Select(g => new ReportTotals(g.Sum(x => x.Amount), g.Sum(x => x.Collected), g.Sum(x => x.Remaining), g.Count(), 0, 0)).SingleOrDefaultAsync(ct) ?? new(0,0,0,0);
        return await rows.GroupBy(_ => 1).Select(g => new ReportTotals(0,0,0,g.Count(),g.Sum(x => x.DurationSeconds ?? 0),
            g.Sum(x => x.MissingClinicalTimestamps),g.Sum(x => x.VisitCount))).SingleOrDefaultAsync(ct) ?? new(0,0,0,0);
    }

    public async Task<CeoReport> ReportAsync(string kind, CeoQuery q, CancellationToken ct = default, bool export = false)
    {
        var p = Period(q); string basis; IQueryable<ReportRow> rows;
        if (kind is "patient-collections" or "insurer-collections")
        {
            if (q.Status != null) throw new BookingRuleException("Ceo.UnsupportedFilter", "Status is not a receipt filter.");
            basis = "Collection";
            rows = from x in Payments(q, kind == "insurer-collections", p.Start, p.End)
                join ledger in LedgerQuery() on x.InvoiceId equals ledger.Id
                select new ReportRow {
                    Id = "allocation:" + x.Id, Label = x.InsuranceChequeReceiptId != null ? "Cheque #" + x.InsuranceChequeReceiptId : x.IdempotencyKey ?? "Payment #" + x.Id,
                    Patient = ledger.Patient, PatientId = ledger.PatientId, CompanyId = ledger.CompanyId,
                    Company = db.InsuranceCompanies.IgnoreQueryFilters().Where(c => c.Id == ledger.CompanyId).Select(c => c.Name).FirstOrDefault(),
                    ClinicianId = ledger.ClinicianId, Date = x.PaymentDate, Amount = x.Amount, Collected = x.Amount,
                    Status = "Paid", Method = x.CollectionMethod ?? x.PaymentMethod.ToString(), Remaining = 0m,
                    DurationSeconds = null, ScheduledEnd = null, ClinicalStart = null, ClinicalEnd = null
                };
        }
        else if (kind is "patient-outstanding" or "insurer-receivables")
        {
            if (q.Method != null || q.Status != null || q.From != null || q.To != null)
                throw new BookingRuleException("Ceo.UnsupportedFilter", "Balances are current at generation time; period, method and appointment status filters do not apply.");
            basis = "Current balance"; var insurer = kind == "insurer-receivables";
            rows = Scope(q).Where(x => insurer ? x.Earned > x.InsurerReceipts : x.PatientLiability > x.PatientReceipts)
                .Select(x => new ReportRow { Id = x.Id.ToString(), Label = x.Reference, Patient = x.Patient,
                    PatientId = x.PatientId, CompanyId = x.CompanyId,
                    Company = db.InsuranceCompanies.IgnoreQueryFilters().Where(c => c.Id == x.CompanyId).Select(c => c.Name).FirstOrDefault(),
                    ClinicianId = x.ClinicianId, Date = x.DeliveredAt, Amount = insurer ? x.Earned : x.PatientLiability,
                    Collected = insurer ? x.InsurerReceipts : x.PatientReceipts,
                    Remaining = insurer ? x.Earned - x.InsurerReceipts : x.PatientLiability - x.PatientReceipts, Status = "Outstanding",
                    DurationSeconds = null, ScheduledEnd = null, ClinicalStart = null, ClinicalEnd = null });
        }
        else if (kind is "activity" or "workload")
        {
            if (q.Method != null) throw new BookingRuleException("Ceo.UnsupportedFilter", "Payment method is not an activity filter.");
            basis = "Scheduled service start";
            var visits = db.Appointments.AsNoTracking().Where(a => a.StartTime >= p.Start && a.StartTime < p.End);
            if (q.PatientId != null) visits = visits.Where(a => a.PatientId == q.PatientId);
            if (q.Search != null) visits = visits.Where(a => a.Patient.ApplicationUser.FullName.Contains(q.Search));
            if (q.ClinicianId != null) visits = visits.Where(a => kind == "workload" ? a.TreatmentStartedBy == q.ClinicianId : a.DoctorId == q.ClinicianId || a.SeniorId == q.ClinicianId || a.TreatmentStartedBy == q.ClinicianId);
            if (q.CompanyId != null) { var ids = Scope(new() { CompanyId = q.CompanyId }).Select(x => x.Id); visits = visits.Where(a => db.Invoices.Any(i => i.AppointmentId == a.Id && ids.Contains(i.Id))); }
            if (q.Funding != null) visits = visits.Where(a => a.InsuranceFunded == (q.Funding == "Insurance"));
            if (q.Status != null) { var status = Enum.Parse<AppointmentStatus>(q.Status); visits = visits.Where(a => a.Status == status); }
            // SQL Server computes recorded duration on the server; no scheduled or Reception time is substituted.
            if (kind == "workload") rows = visits.GroupBy(a => a.TreatmentStartedBy).Select(g => new ReportRow {
                Id = g.Key ?? "unknown", Label = db.Users.Where(u => u.Id == g.Key).Select(u => u.FullName).FirstOrDefault() ?? "Unavailable",
                ClinicianId = g.Key, Clinician = db.Users.Where(u => u.Id == g.Key).Select(u => u.FullName).FirstOrDefault(), Date = null,
                Amount = 0m, Collected = 0m, Remaining = 0m, VisitCount = g.Count(), CompletedCount = g.Count(a => a.TreatmentEndedAt != null),
                MissingClinicalTimestamps = g.Count(a => a.TreatmentStartedAt == null || a.TreatmentEndedAt == null),
                DurationSeconds = g.Sum(a => a.TreatmentStartedAt != null && a.TreatmentEndedAt >= a.TreatmentStartedAt ? EF.Functions.DateDiffSecond(a.TreatmentStartedAt, a.TreatmentEndedAt) ?? 0 : 0)
            });
            else rows = visits.Select(a => new ReportRow { Id = a.Id.ToString(), Label = a.AppointmentType,
                Patient = db.Patients.IgnoreQueryFilters().Where(y => y.Id == a.PatientId).Select(y => y.ApplicationUser.FullName).FirstOrDefault(),
                PatientId = a.PatientId, Clinician = db.Users.Where(u => u.Id == (a.TreatmentStartedBy ?? a.DoctorId ?? a.SeniorId)).Select(u => u.FullName).FirstOrDefault(),
                ClinicianId = a.TreatmentStartedBy ?? a.DoctorId ?? a.SeniorId, Date = a.StartTime, Status = a.Status.ToString(), Amount = 0m, Collected = 0m, Remaining = 0m,
                ScheduledEnd = a.EndTime, ArrivedAt = a.ActualCheckIn, DepartedAt = a.ActualCheckOut, ClinicalStart = a.TreatmentStartedAt, ClinicalEnd = a.TreatmentEndedAt,
                VisitCount = 1, CompletedCount = a.TreatmentEndedAt != null ? 1 : 0,
                MissingClinicalTimestamps = a.TreatmentStartedAt == null || a.TreatmentEndedAt == null ? 1 : 0,
                DurationSeconds = a.TreatmentStartedAt != null && a.TreatmentEndedAt >= a.TreatmentStartedAt ? EF.Functions.DateDiffSecond(a.TreatmentStartedAt, a.TreatmentEndedAt) : null });
        }
        else throw new BookingRuleException("Ceo.ReportNotFound", "Choose a supported report.", 404);
        var totals = await Totals(rows, kind is "activity" or "workload", ct);
        if (export && totals.Rows > 100000) throw new BookingRuleException("Ceo.ExportScope", "Narrow the export to at most 100000 rows.");
        var items = await rows.OrderByDescending(x => x.Date).ThenBy(x => x.Id)
            .Skip(export ? 0 : (q.Page - 1) * q.PageSize).Take(export ? 100000 : q.PageSize).ToListAsync(ct);
        return new(kind, basis, "All matching records; collections show payment allocations, not extra receipt headers", clock.GetUtcNow().UtcDateTime,
            basis == "Current balance" ? null : p.From, basis == "Current balance" ? null : p.To, export ? 1 : q.Page, q.PageSize, totals.Rows, totals, items);
    }

    public async Task<CeoOverview> OverviewAsync(CeoQuery q, CancellationToken ct = default)
    {
        if (q.CompanyId != null || q.PatientId != null || q.Search != null || q.ClinicianId != null || q.Funding != null || q.Method != null || q.Status != null)
            throw new BookingRuleException("Ceo.UnsupportedFilter", "Overview accepts a collection/service period; use Reports for additional filters.");
        var p = Period(q); var now = clock.GetUtcNow().UtcDateTime;
        var patient = await Payments(q, false, p.Start, p.End).SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var insurer = await Payments(q, true, p.Start, p.End).SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var ledger = Scope(q);
        var balances = await ledger.GroupBy(_ => 1).Select(g => new {
            Patient = g.Sum(x => x.PatientLiability > x.PatientReceipts ? x.PatientLiability - x.PatientReceipts : 0),
            Insurer = g.Sum(x => x.Earned > x.InsurerReceipts ? x.Earned - x.InsurerReceipts : 0), Planned = g.Sum(x => x.Planned),
            Unsubmitted = g.Count(x => x.Earned > 0 && x.ClaimId == null && x.CanSubmit),
            Claims = g.Count(x => x.ClaimId != null && x.Earned > x.InsurerReceipts)
        }).SingleOrDefaultAsync(ct);
        var visits = db.Appointments.Where(a => a.StartTime >= p.Start && a.StartTime < p.End);
        var counts = await visits.GroupBy(_ => 1).Select(g => new {
            Scheduled = g.Count(), Completed = g.Count(a => a.TreatmentEndedAt != null),
            Cancelled = g.Count(a => a.Status == AppointmentStatus.Cancelled), NoShow = g.Count(a => a.Status == AppointmentStatus.NoShow)
        }).SingleOrDefaultAsync(ct);
        var occupancy = db.Appointments.Where(a => a.TreatmentStartedAt != null && a.TreatmentEndedAt == null ||
            a.AppointmentType == "Assessment" && a.SeniorId != null && a.Status == AppointmentStatus.InProgress && a.ActualCheckIn != null && a.ActualCheckOut == null);
        var occupied = await occupancy.CountAsync(ct);
        var capacity = await db.Rooms.Where(r => r.IsActive).SumAsync(r => (int?)r.Capacity, ct) ?? 0;
        var availableRoomSlots = await db.Rooms.Where(r => r.IsActive).Select(r => new { r.Capacity, Used = occupancy.Count(a => a.RoomId == r.Id) })
            .SumAsync(r => (int?)(r.Capacity > r.Used ? r.Capacity - r.Used : 0), ct) ?? 0;
        var unresolved = await db.Appointments.CountAsync(a => a.Status == AppointmentStatus.Pending && a.TreatmentStartedAt == null &&
            db.DoctorAbsences.Any(d => !d.Withdrawn && (d.DoctorId == a.DoctorId || d.DoctorId == a.SeniorId) &&
            d.StartsAt != null && d.EndsAt != null && a.StartTime < d.EndsAt && a.EndTime > d.StartsAt), ct);
        // One server aggregate across exact Cairo boundary intervals, including DST. No receipt-history materialization.
        var receiptEvents = Payments(q, false, p.Start, p.End).Select(x => new CollectionEvent { Date = x.PaymentDate, Amount = x.Amount, Insurer = false })
            .Concat(Payments(q, true, p.Start, p.End).Select(x => new CollectionEvent { Date = x.PaymentDate, Amount = x.Amount, Insurer = true }));
        var parameter = Expression.Parameter(typeof(CollectionEvent), "receipt");
        Expression bucket = Expression.Constant(0);
        for (var i = 1; i <= p.To.DayNumber - p.From.DayNumber; i++)
            bucket = Expression.Condition(Expression.GreaterThanOrEqual(Expression.Property(parameter, nameof(CollectionEvent.Date)), Expression.Constant(ClinicCalendar.DayStart(p.From.AddDays(i)))), Expression.Constant(i), bucket);
        var key = Expression.Add(Expression.Multiply(bucket, Expression.Constant(2)), Expression.Condition(Expression.Property(parameter, nameof(CollectionEvent.Insurer)), Expression.Constant(1), Expression.Constant(0)));
        var groupKey = Expression.Lambda<Func<CollectionEvent, int>>(key, parameter);
        var aggregated = await receiptEvents.GroupBy(groupKey).Select(g => new { Key = g.Key, Amount = g.Sum(x => x.Amount) }).ToListAsync(ct);
        var days = new List<CollectionDay>();
        for (var day = p.From; day <= p.To; day = day.AddDays(1))
        {
            var offset = (day.DayNumber - p.From.DayNumber) * 2;
            days.Add(new(day.ToString("yyyy-MM-dd"), aggregated.Where(x => x.Key == offset).Sum(x => x.Amount), aggregated.Where(x => x.Key == offset + 1).Sum(x => x.Amount)));
        }
        return new(now, p.From, p.To, patient, insurer, balances?.Patient ?? 0, balances?.Insurer ?? 0,
            balances?.Planned ?? 0, counts?.Scheduled ?? 0, counts?.Completed ?? 0, counts?.Cancelled ?? 0,
            counts?.NoShow ?? 0, occupied, capacity, availableRoomSlots, balances?.Unsubmitted ?? 0,
            balances?.Claims ?? 0, unresolved, days);
    }
    private sealed class CollectionEvent { public DateTime Date { get; set; } public decimal Amount { get; set; } public bool Insurer { get; set; } }
}
