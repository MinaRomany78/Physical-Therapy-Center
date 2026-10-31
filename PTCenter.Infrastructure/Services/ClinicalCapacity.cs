using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
namespace PTCenter.Infrastructure.Services;

public static class ClinicalCapacity
{
    public static bool Occupied(Appointment a) => a.ActualCheckIn != null && a.ActualCheckOut == null;
    public static bool Running(Appointment a) => a.TreatmentStartedAt != null && a.TreatmentEndedAt == null;
    public static bool Live(Appointment a) => a.Status is not (AppointmentStatus.Cancelled or AppointmentStatus.NoShow or AppointmentStatus.Completed);
    public static int Peak(DateTime start, DateTime end, IEnumerable<Appointment> source, bool actual = false)
    {
        var points = source.Where(a => a.StartTime < end && a.EndTime > start || actual && Occupied(a))
            .SelectMany(a => new[] {
                (At: actual && Occupied(a) ? start : (a.StartTime < start ? start : a.StartTime), Delta: 1),
                (At: actual && Occupied(a) ? end : (a.EndTime > end ? end : a.EndTime), Delta: -1)
            }).OrderBy(p => p.At).ThenBy(p => p.Delta);
        int load = 0, peak = 0;
        foreach(var p in points) { load += p.Delta; peak = Math.Max(peak, load); }
        return peak;
    }
    public static List<string> Conflicts(DateTime start, DateTime end, int patient, string clinician, int room, IEnumerable<Appointment> source, bool doctorTreatment, int roomCapacity = 1, bool actual = false)
    {
        var appointments = source.Where(Live).ToList();
        bool Intersects(Appointment a) => a.StartTime < end && a.EndTime > start;
        var conflicts = new List<string>();
        if (appointments.Any(a => a.PatientId == patient && (Intersects(a) || actual && Occupied(a)))) conflicts.Add("PatientConflict");
        if (Peak(start,end,appointments.Where(a => a.RoomId == room),actual) >= roomCapacity) conflicts.Add("RoomConflict");
        var related = appointments.Where(a => a.DoctorId == clinician || a.SeniorId == clinician).Where(a => a.TreatmentEndedAt == null).ToList();
        var seniors = related.Where(a => a.SeniorId == clinician);
        if (seniors.Any(a => Intersects(a) || actual && Occupied(a))) conflicts.Add("ClinicianConflict");
        var doctors = related.Where(a => a.DoctorId == clinician && (Intersects(a) || actual && Running(a))).ToList();
        if (doctorTreatment && doctors.Any(a => a.RoomId == room)) conflicts.Add("DoctorSameRoom");
        if (!doctorTreatment && doctors.Count > 0) conflicts.Add("ClinicianConflict");
        else if (doctorTreatment)
        {
            var events = doctors.SelectMany(a => new[] {
                (At: actual && Running(a) ? start : (a.StartTime < start ? start : a.StartTime), Delta: 1),
                (At: actual && Running(a) ? end : (a.EndTime > end ? end : a.EndTime), Delta: -1)
            }).OrderBy(e => e.At).ThenBy(e => e.Delta);
            var active = 1;
            foreach (var point in events) { active += point.Delta; if (active > 2) { conflicts.Add("DoctorCapacity"); break; } }
        }
        return conflicts.Distinct().ToList();
    }
}
