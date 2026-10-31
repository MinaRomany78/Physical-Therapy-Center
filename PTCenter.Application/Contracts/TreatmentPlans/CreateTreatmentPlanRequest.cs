using PTCenter.Domain.Entities;
using PTCenter.Domain.Entities.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace PTCenter.Application.Contracts.TreatmentPlans
{
  
    public class CreateTreatmentPlanRequest
    {
        public int AssessmentId { get; set; }

        public string DoctorId { get; set; } = null!;

        public int NumberOfSessions { get; set; }

        [System.ComponentModel.DataAnnotations.Range(1,7)]
        public int FrequencyPerWeek { get; set; }

        public string TreatmentType { get; set; } = null!;

        public string? Exercises { get; set; }

        public StatusPlan Status { get; set; } = StatusPlan.Active;
    }
}
