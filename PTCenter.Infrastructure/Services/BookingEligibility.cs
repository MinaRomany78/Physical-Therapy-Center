using Microsoft.EntityFrameworkCore;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Infrastructure.Services;

public partial class BookingService
{
    private IQueryable<Patient> BookablePatients() => db.Patients.Where(patient =>
        !db.Assessments.Any(a=>a.PatientId==patient.Id && a.CompletedAt!=null) ||
        db.TreatmentPlans.Any(plan=>plan.Assessment.PatientId==patient.Id && plan.Assessment.CompletedAt!=null && plan.Status==StatusPlan.Active &&
            plan.NumberOfSessions > db.TreatmentPlanSessions.Count(s=>s.TreatmentPlanId==plan.Id &&
                (s.Status==SessionStatus.Completed || db.Appointments.Any(a=>a.TreatmentPlanSessionId==s.Id &&
                    (a.ActualCheckIn!=null || a.Status!=AppointmentStatus.Cancelled && a.Status!=AppointmentStatus.NoShow)))) &&
            (!db.TreatmentCourseAgreements.Any(c=>c.TreatmentPlanId==plan.Id) || db.TreatmentPlanSessions.Count(s=>s.TreatmentPlanId==plan.Id &&
                (s.Status==SessionStatus.Completed || db.Appointments.Any(a=>a.TreatmentPlanSessionId==s.Id &&
                    (a.ActualCheckIn!=null || a.Status!=AppointmentStatus.Cancelled && a.Status!=AppointmentStatus.NoShow)))) <
                db.TreatmentCourseAgreements.Where(c=>c.TreatmentPlanId==plan.Id).Select(c=>c.CoveredSessions).FirstOrDefault())))
        .Where(patient=>patient.PaymentType!="Insurance" || !db.Assessments.Any(a=>a.PatientId==patient.Id && a.CompletedAt!=null) ||
            patient.InsuranceDetails!=null && patient.InsuranceDetails.ApprovedSessions > db.Appointments.Count(a=>a.PatientId==patient.Id && a.AppointmentType=="Session" &&
                (a.InsuranceFunded || a.BookingBatchId==null) && (a.ActualCheckIn!=null || a.Status!=AppointmentStatus.Cancelled && a.Status!=AppointmentStatus.NoShow)));

    private async Task<Dictionary<DateOnly,int>> WeeklyCountsAsync(int planId,int? exclude)
    {
        var starts=await db.Appointments.Where(a=>a.Id!=exclude && a.TreatmentPlanSession!=null && a.TreatmentPlanSession.TreatmentPlanId==planId &&
            a.Status!=AppointmentStatus.Cancelled && a.Status!=AppointmentStatus.NoShow).Select(a=>a.StartTime).ToListAsync();
        return starts.GroupBy(s=>ClinicCalendar.Week(new DateTimeOffset(DateTime.SpecifyKind(s,DateTimeKind.Utc)))).ToDictionary(g=>g.Key,g=>g.Count());
    }
    private async Task<List<PTCenter.Application.Contracts.Bookings.BookedAppointment>> CompleteRowsAsync(List<Appointment> appointments)
    {
        var ids=appointments.Select(a=>a.Id).ToArray();
        var rows=await db.Appointments.AsNoTracking().Include(a=>a.Doctor).Include(a=>a.Room).Include(a=>a.TreatmentPlanSession).Where(a=>ids.Contains(a.Id)).ToListAsync();
        var invoices=await db.Invoices.AsNoTracking().Where(i=>i.AppointmentId!=null && ids.Contains(i.AppointmentId.Value)).ToListAsync();
        return appointments.Select(original=>{
            var a=rows.Single(r=>r.Id==original.Id);var bills=invoices.Where(i=>i.AppointmentId==a.Id).ToList();
            return new PTCenter.Application.Contracts.Bookings.BookedAppointment(a.Id,a.TreatmentPlanSessionId,
                new DateTimeOffset(DateTime.SpecifyKind(a.StartTime,DateTimeKind.Utc)),new DateTimeOffset(DateTime.SpecifyKind(a.EndTime,DateTimeKind.Utc))) {
                ClinicianId=a.DoctorId??a.SeniorId,ClinicianName=a.Doctor?.FullName,RoomId=a.RoomId,RoomName=a.Room?.Name,
                TreatmentPlanId=a.TreatmentPlanSession?.TreatmentPlanId,SessionNumber=a.TreatmentPlanSession?.SessionNumber,
                CourseAgreementId=bills.Select(i=>i.CourseAgreementId).FirstOrDefault(),InvoiceIds=bills.Select(i=>i.Id).ToList()
            };
        }).ToList();
    }
}
