using System;
using System.Collections.Generic;
using System.Text;

namespace PTCenter.Application.Contracts.Receptionist
{
         public class TodaySessionResponse
        {
            public int Id { get; set; }
            public int PatientId { get; set; }
            public string? DoctorId { get; set; }
            public string? SeniorId { get; set; }
            public int? RoomId { get; set; }
            public string AppointmentType { get; set; } = "";
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public DateTime? ActualCheckIn { get; set; }
            public DateTime? ActualCheckOut { get; set; }
        public DateTime? TreatmentStartedAt { get; set; }
        public DateTime? TreatmentEndedAt { get; set; }
            public string Time { get; set; } = default!;
            public string PatientName { get; set; } = default!;
            public string? SessionNumber { get; set; } // مثال: Session 5 of 10
            public string DoctorName { get; set; } = default!;
            public string? RoomName { get; set; }
            public string Status { get; set; } = default!;
        }
    }

