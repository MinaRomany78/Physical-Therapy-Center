using System.Data;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Bookings;
using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Infrastructure.Services;

public partial class BookingService
{
    private static string ScheduleVersion(Appointment a) => Hash(new { a.Id,Start=a.StartTime.Ticks,End=a.EndTime.Ticks,a.RoomId,a.DoctorId,a.SeniorId,a.Status,CheckIn=a.ActualCheckIn?.Ticks,CheckOut=a.ActualCheckOut?.Ticks,Updated=a.UpdatedAt?.Ticks });
    private static bool CanReschedule(Appointment a) => a.Status == AppointmentStatus.Pending && a.ActualCheckIn == null && a.ActualCheckOut == null && a.TreatmentStartedAt == null && a.TreatmentEndedAt == null && a.StartTime > DateTime.UtcNow &&
        (a.TreatmentPlanSession == null || a.TreatmentPlanSession.CheckIn == null && a.TreatmentPlanSession.CheckOut == null && a.TreatmentPlanSession.Status == SessionStatus.Pending);
    private static void RequireUpcoming(Appointment a) {
        if(!CanReschedule(a))throw Rule("NotUpcoming","Only upcoming, unstarted pending appointments can be rescheduled. Completed, cancelled, no-show and started records stay unchanged.",409);
    }
    private static List<string> IntervalConflicts(DateTimeOffset start,int patientId,string clinicianId,int roomId,IEnumerable<Appointment> candidates,bool doctorTreatment=true,int roomCapacity=1)
        => ClinicalCapacity.Conflicts(start.UtcDateTime,start.UtcDateTime.AddMinutes(60),patientId,clinicianId,roomId,candidates,doctorTreatment,roomCapacity);
    private static List<string> WorkingTimeConflicts(DateTimeOffset start,IEnumerable<DoctorSchedule> schedules,IEnumerable<DoctorAbsence> absences)
    {
        var local=TimeZoneInfo.ConvertTime(start,ClinicTimeZone);var end=TimeZoneInfo.ConvertTime(start.AddMinutes(60),ClinicTimeZone);var conflicts=new List<string>();
        if(start<=DateTimeOffset.UtcNow)conflicts.Add("TimeInPast");
        if(!schedules.Any())conflicts.Add("WorkingHoursNotConfigured");
        else if(!schedules.Any(s=>s.DayOfWeek==(int)local.DayOfWeek && s.StartTime<=TimeOnly.FromDateTime(local.DateTime) && s.EndTime>=TimeOnly.FromDateTime(end.DateTime) && local.Date==end.Date))conflicts.Add("OutsideWorkingHours");
        if(absences.Any(a=>a.AbsentDate==DateOnly.FromDateTime(local.DateTime)))conflicts.Add("ClinicianAbsent");
        return conflicts;
    }

    public async Task<PatientProfileData> ProfileAsync(int patientId)
    {
        await PatientAsync(patientId);
        var plans=await db.TreatmentPlans.AsNoTracking().Include(p=>p.Assessment).Include(p=>p.Doctor).Where(p=>p.Assessment.PatientId==patientId).OrderByDescending(p=>p.Id).ToListAsync();
        var planIds=plans.Select(p=>p.Id).ToList();
        var sessions=await db.TreatmentPlanSessions.AsNoTracking().Where(s=>planIds.Contains(s.TreatmentPlanId)).ToListAsync();
        var appointments=await db.Appointments.AsNoTracking().Include(a=>a.TreatmentPlanSession).Include(a=>a.Doctor).Include(a=>a.Room).Where(a=>a.PatientId==patientId).ToListAsync();
        var invoices=await db.Invoices.AsNoTracking().Include(i=>i.Payments).Where(i=>i.PatientId==patientId).ToListAsync();
        var agreements=await db.TreatmentCourseAgreements.AsNoTracking().Where(a=>a.PatientId==patientId).ToListAsync();
        var changes=await db.AppointmentScheduleChanges.AsNoTracking().Where(c=>c.PatientId==patientId).OrderByDescending(c=>c.ChangedAt).ToListAsync();
        var result=new List<ProfileCourse>();
        foreach(var plan in plans) {
            var courseSessions=sessions.Where(s=>s.TreatmentPlanId==plan.Id).OrderBy(s=>s.SessionNumber).ToList();var ids=courseSessions.Select(s=>s.Id).ToHashSet();
            var appts=appointments.Where(a=>a.TreatmentPlanSessionId.HasValue&&ids.Contains(a.TreatmentPlanSessionId.Value)).ToList();
            var agreement=agreements.SingleOrDefault(a=>a.TreatmentPlanId==plan.Id);
            var bills=invoices.Where(i=>agreement!=null&&i.CourseAgreementId==agreement.Id || i.TreatmentPlanSessionId.HasValue&&ids.Contains(i.TreatmentPlanSessionId.Value) || appts.Any(a=>a.Id==i.AppointmentId)).ToList();
            bool Insurer(Payment p)=>CourseFinanceService.InsurerReceipt(p);
            decimal PatientPaid(Invoice i)=>i.Payments.Where(p=>p.Status==PaymentStatus.Paid&&!Insurer(p)).Sum(p=>p.Amount);
            var paid=bills.Sum(PatientPaid);var insurerPaid=bills.Sum(i=>i.Payments.Where(p=>p.Status==PaymentStatus.Paid&&Insurer(p)).Sum(p=>p.Amount));
            var patientAmount=agreement?.PricingMode=="Package"?agreement.PatientAmount:bills.Sum(i=>i.PatientAmount);
            var insurerAmount=agreement?.PricingMode=="Package"?agreement.InsuranceAmount:bills.Sum(i=>i.InsuranceAmount);
            var consumed=courseSessions.Where(s=>s.CheckIn!=null||s.CheckOut!=null||s.Status is SessionStatus.Completed or SessionStatus.InProgress).Select(s=>s.Id)
                .Concat(appts.Where(a=>a.ActualCheckIn!=null||a.ActualCheckOut!=null||a.Status is AppointmentStatus.Completed or AppointmentStatus.InProgress).Select(a=>a.TreatmentPlanSessionId!.Value)).ToHashSet();
            Invoice? Bill(int sessionId)=>bills.FirstOrDefault(i=>i.TreatmentPlanSessionId==sessionId||appts.Any(a=>a.TreatmentPlanSessionId==sessionId&&a.Id==i.AppointmentId));
            var consumedBills=consumed.Select(Bill).ToList();
            var rows=courseSessions.Select(s=> {
                var appointment=appts.Where(a=>a.TreatmentPlanSessionId==s.Id).OrderBy(a=>a.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow?1:0).ThenByDescending(a=>a.Id).FirstOrDefault();
                var bill=Bill(s.Id);var coverage="Unknown";
                if(bill!=null) {
                    var package=agreement?.PricingMode=="Package";
                    var required=consumedBills.Any(b=>b==null)?(decimal?)null:consumedBills.Sum(b=>b!.PatientAmount)+(consumed.Contains(s.Id)?0:bill.PatientAmount);
                    coverage=bill.PatientAmount==0?"NoPatientCharge":package?(required==null?"Review":paid>=required?"Covered":"Due"):PatientPaid(bill)>=bill.PatientAmount?"Paid":"Due";
                }
                return new ProfileSession(s.Id,s.SessionNumber,appointment?.Id,appointment==null?null:ScheduleVersion(appointment),appointment?.DoctorId??appointment?.SeniorId,
                    appointment?.Doctor?.FullName??plan.Doctor?.FullName??"",appointment?.RoomId,appointment?.Room?.Name,appointment?.StartTime??s.SessionDate,appointment?.EndTime,
                    appointment?.ActualCheckIn??s.CheckIn,appointment?.ActualCheckOut??s.CheckOut,appointment?.Status.ToString()??(s.Status==SessionStatus.Pending?"NotScheduled":s.Status.ToString()),coverage,bill?.PatientAmount,bill==null?null:PatientPaid(bill),appointment!=null&&plan.Status==StatusPlan.Active&&CanReschedule(appointment));
            }).ToList();
            var payments=bills.SelectMany(i=>i.Payments.Where(p=>p.Status==PaymentStatus.Paid).Select(p=>new ProfilePayment(p.Id,i.Id,i.InvoiceNumber,p.Amount,Insurer(p)?"Insurance":"Patient",p.CollectionMethod??p.PaymentMethod.ToString(),p.PaymentDate,p.TransactionId))).OrderByDescending(p=>p.Date).ToList();
            result.Add(new(plan.Id,plan.TreatmentType,plan.Status.ToString(),plan.Doctor?.FullName??"",plan.NumberOfSessions,plan.CompletedSessions,patientAmount,paid,Math.Max(0,patientAmount-paid),insurerAmount,insurerPaid,Math.Max(0,insurerAmount-insurerPaid),rows,payments,changes.Where(c=>c.TreatmentPlanId==plan.Id).Select(Activity).ToList()));
        }
        var assessments=await db.Assessments.AsNoTracking().Include(a=>a.Senior).Where(a=>a.PatientId==patientId).OrderByDescending(a=>a.AssessmentDate).ToListAsync();
        return new(patientId,result,assessments.Select(a=>new ProfileAssessment(a.Id,a.AssessmentDate,a.Diagnosis,a.Senior.FullName,a.CompletedAt)).ToList()) {
            AssessmentPayments=invoices.Where(i=>i.Type==InvoiceType.Assessment).SelectMany(i=>i.Payments.Where(p=>p.Status==PaymentStatus.Paid).Select(p=>new ProfilePayment(p.Id,i.Id,i.InvoiceNumber,p.Amount,p.PayerType=="Insurance"||p.PaymentMethod==PaymentMethod.Insurance?"Insurance":"Patient",p.CollectionMethod??p.PaymentMethod.ToString(),p.PaymentDate,p.TransactionId))).OrderByDescending(p=>p.Date).ToList()
        };
    }
    private static ProfileActivity Activity(AppointmentScheduleChange c)=>new(c.Id,c.AppointmentId,c.ChangedAt,c.ActorName,c.PreviousStart,c.PreviousEnd,c.PreviousRoomName,c.NewStart,c.NewEnd,c.NewRoomName,c.Reason);

    public async Task<RescheduleAvailability> RescheduleAvailabilityAsync(int id,DateOnly date,CancellationToken cancellationToken=default)
    {
        var appointment=await db.Appointments.AsNoTracking().Include(a=>a.TreatmentPlanSession).SingleOrDefaultAsync(a=>a.Id==id,cancellationToken)??throw Rule("AppointmentNotFound","Appointment was not found.",404);
        RequireUpcoming(appointment);
        var clinician=appointment.DoctorId??appointment.SeniorId??"";var patient=await PatientAsync(appointment.PatientId);
        var clinicians=await CliniciansForAsync(patient,appointment.AppointmentType=="Session"?"Doctor":"Senior");
        var doctor=clinicians.FirstOrDefault(c=>c.Id==clinician)??throw Rule("ClinicianEligibility","The assigned clinician is no longer eligible.",409);
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,ClinicTimeZone).DateTime);
        if(date<today||date>=DateOnly.MaxValue.AddDays(-1))throw Rule("DateRange","Choose a valid upcoming date.");
        var hours=await db.DoctorSchedules.AsNoTracking().Where(s=>s.DoctorId==clinician).ToListAsync(cancellationToken);
        var absences=await db.DoctorAbsences.AsNoTracking().Where(s=>s.DoctorId==clinician&&s.AbsentDate==date).ToListAsync(cancellationToken);
        var rooms=await db.Rooms.AsNoTracking().Where(r=>r.IsActive&&r.Capacity>0).OrderBy(r=>r.Id).ToListAsync(cancellationToken);
        var dayStart=date.ToDateTime(TimeOnly.MinValue);var dayEnd=dayStart.AddDays(1);
        // Broad UTC bounds avoid converting an invalid/ambiguous local midnight at DST transitions.
        var earliest=DateTime.SpecifyKind(dayStart.AddDays(-1),DateTimeKind.Utc);var latest=DateTime.SpecifyKind(dayEnd.AddDays(1),DateTimeKind.Utc);
        var candidates=await db.Appointments.AsNoTracking().Where(a=>a.Id!=id && a.Status!=AppointmentStatus.Cancelled&&a.Status!=AppointmentStatus.NoShow&&a.Status!=AppointmentStatus.Completed &&
            (a.StartTime<latest&&a.EndTime>earliest||a.Status==AppointmentStatus.InProgress&&a.ActualCheckOut==null)).ToListAsync(cancellationToken);
        var slots=new List<RescheduleSlot>();var notices=new List<string>();
        if(hours.Count==0)notices.Add("WorkingHoursNotConfigured");if(absences.Count>0)notices.Add("ClinicianAbsent");
        var localTimes=new SortedSet<DateTime>();
        foreach(var schedule in hours.Where(s=>s.DayOfWeek==(int)dayStart.DayOfWeek)) {
            var first=date.ToDateTime(schedule.StartTime);var last=date.ToDateTime(schedule.EndTime).AddMinutes(-60);
            for(var time=first;time<=last;time=time.AddMinutes(30))localTimes.Add(time);
            // Include a non-grid existing start, so same-day self-exclusion is explicit.
            var original=TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(appointment.StartTime,DateTimeKind.Utc),ClinicTimeZone);
            if(DateOnly.FromDateTime(original)==date&&original>=first&&original<=last)localTimes.Add(original);
        }
        foreach(var local in localTimes) {
            cancellationToken.ThrowIfCancellationRequested();
            // Never invent a DST-gap time or choose an ambiguous offset silently.
            if(ClinicTimeZone.IsInvalidTime(local)||ClinicTimeZone.IsAmbiguousTime(local))continue;
            var start=new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local,ClinicTimeZone));
            if(WorkingTimeConflicts(start,hours,absences).Count>0)continue;
            foreach(var room in rooms)if(IntervalConflicts(start,appointment.PatientId,clinician,room.Id,candidates,appointment.AppointmentType == "Session",room.Capacity).Count==0)slots.Add(new(start.UtcDateTime,start.UtcDateTime.AddMinutes(60),room.Id,room.Name,room.Capacity));
        }
        return new(id,ScheduleVersion(appointment),appointment.StartTime,appointment.EndTime,appointment.RoomId,clinician,doctor.Name,date,slots,notices);
    }
    public async Task<RescheduleSessionReceipt> RescheduleSessionAsync(int id,RescheduleSessionRequest request,string actor)
    {
        if(string.IsNullOrWhiteSpace(actor))throw Rule("ActorRequired","An authenticated actor is required.",403);
        if(string.IsNullOrWhiteSpace(request.OperationKey)||request.OperationKey.Length>100||string.IsNullOrWhiteSpace(request.ExpectedVersion)||request.Reason?.Length>500)throw Rule("RescheduleRequest","A stable operation key, version and reason of at most 500 characters are required.");
        var fingerprint=Hash(new{id,request.ExpectedVersion,Start=request.StartTime.UtcDateTime,request.RoomId,Reason=request.Reason?.Trim(),Actor=actor});
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await ClinicMutationLock.AcquireAsync(db);
        var replay=await db.AppointmentScheduleChanges.AsNoTracking().SingleOrDefaultAsync(c=>c.OperationKey==request.OperationKey);
        if(replay!=null){if(replay.AppointmentId!=id||replay.RequestHash!=fingerprint)throw Rule("RetryKeyConflict","This operation key belongs to another reschedule request.",409);return Receipt(replay);}
        var appointment=await db.Appointments.Include(a=>a.TreatmentPlanSession).Include(a=>a.Room).SingleOrDefaultAsync(a=>a.Id==id)??throw Rule("AppointmentNotFound","Appointment was not found.",404);
        RequireUpcoming(appointment);
        if(ScheduleVersion(appointment)!=request.ExpectedVersion)throw Rule("StaleSchedule","This appointment changed. Review its latest schedule and select an available slot again.",409);
        if(appointment.StartTime==request.StartTime.UtcDateTime&&appointment.RoomId==request.RoomId)throw Rule("NoScheduleChange","Choose a different time or room before saving.");
        var clinician=appointment.DoctorId??appointment.SeniorId??"";
        var originallyInsured=appointment.InsuranceFunded||await db.Invoices.AnyAsync(i=>i.AppointmentId==id&&i.InsuranceAmount>0);
        var preview=await PreviewCoreAsync(new(){PatientId=appointment.PatientId,ClinicianId=clinician,RoomId=request.RoomId,StartsAt=[request.StartTime],TreatmentPlanId=appointment.TreatmentPlanSession?.TreatmentPlanId},id,false,originallyInsured);
        if(!preview.CanSave)throw Rule("SlotUnavailable",string.Join(", ",preview.Errors.Concat(preview.Appointments.SelectMany(s=>s.Conflicts))),409);
        var newRoom=await db.Rooms.SingleAsync(r=>r.Id==request.RoomId);var now=DateTime.UtcNow;
        var change=new AppointmentScheduleChange{AppointmentId=id,PatientId=appointment.PatientId,TreatmentPlanId=appointment.TreatmentPlanSession?.TreatmentPlanId,OperationKey=request.OperationKey,RequestHash=fingerprint,ActorId=actor,
            ActorName=await db.Users.Where(u=>u.Id==actor).Select(u=>u.FullName).SingleOrDefaultAsync()??"Staff",ChangedAt=now,PreviousStart=appointment.StartTime,PreviousEnd=appointment.EndTime,
            PreviousRoomId=appointment.RoomId,PreviousRoomName=appointment.Room?.Name??"",NewStart=request.StartTime.UtcDateTime,NewEnd=request.StartTime.UtcDateTime.AddMinutes(60),NewRoomId=request.RoomId,NewRoomName=newRoom.Name,ClinicianId=clinician,Reason=request.Reason?.Trim()};
        appointment.StartTime=change.NewStart;appointment.EndTime=change.NewEnd;appointment.RoomId=change.NewRoomId;appointment.UpdatedBy=actor;
        if(appointment.TreatmentPlanSession!=null){appointment.TreatmentPlanSession.SessionDate=change.NewStart;appointment.TreatmentPlanSession.UpdatedBy=actor;}
        db.AppointmentScheduleChanges.Add(change);await db.SaveChangesAsync();await transaction.CommitAsync();return Receipt(change);
    }
    public async Task<RescheduleSessionReceipt> RescheduleReceiptAsync(int id,string key,string actor)
    {
        var change=await db.AppointmentScheduleChanges.AsNoTracking().SingleOrDefaultAsync(c=>c.AppointmentId==id&&c.OperationKey==key&&c.ActorId==actor)??throw Rule("RescheduleNotFound","No committed change has been confirmed for this operation.",404);
        return Receipt(change);
    }
    private static RescheduleSessionReceipt Receipt(AppointmentScheduleChange c)=>new(c.OperationKey,c.AppointmentId,c.PatientId,c.TreatmentPlanId,c.NewStart,c.NewEnd,c.NewRoomId,c.NewRoomName,c.ChangedAt);
}
