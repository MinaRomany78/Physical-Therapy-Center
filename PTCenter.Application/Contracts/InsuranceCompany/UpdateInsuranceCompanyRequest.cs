using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace PTCenter.Application.Contracts.InsuranceCompany
{
    public class UpdateInsuranceCompanyRequest
    {
        public string? Version { get; set; }

        [Required]
        [MaxLength(200)]
        public string Name { get; set; } = null!;

        [Url]
        public string? PortalUrl { get; set; }
        public string? Hotline { get; set; } // أضف هذا الحقل

        public bool IsActive { get; set; }
    }
}
