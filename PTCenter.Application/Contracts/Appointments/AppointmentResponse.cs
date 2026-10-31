using PTCenter.Domain.Entities.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace PTCenter.Application.Contracts.Appointments
{
    public class AppointmentResponse
    {
        public int Id { get; set; }
        public DateTime? ActualCheckIn { get; set; }
        public DateTime? ActualCheckOut { get; set; }
        public DateTime? TreatmentStartedAt { get; set; }
        public DateTime? TreatmentEndedAt { get; set; }
        public bool AssessmentCompleted { get; set; }
        public int PatientId { get; set; }
        public string PatientName { get; set; } = default!;
        public string? DoctorId { get; set; }
        public string? DoctorName { get; set; }
        public string? SeniorId { get; set; }
        public int? RoomId { get; set; }
        public string? RoomName { get; set; }
        public int? TreatmentPlanSessionId { get; set; }
        public string AppointmentType { get; set; } = default!;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public AppointmentStatus  Status { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
    }
}
