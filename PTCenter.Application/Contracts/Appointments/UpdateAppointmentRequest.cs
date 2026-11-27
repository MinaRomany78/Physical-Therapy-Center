using PTCenter.Domain.Entities.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace PTCenter.Application.Contracts.Appointments
{
    public class UpdateAppointmentRequest
    {
        public string? DoctorId { get; set; }
        public string? SeniorId { get; set; }
        public int? RoomId { get; set; }
        public string AppointmentType { get; set; } = null!;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public AppointmentStatus Status { get; set; }
    }
}
