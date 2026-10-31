using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Text;

namespace PTCenter.Infrastructure.Persistence.Configurations
{
    public class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
    {
        public void Configure(EntityTypeBuilder<Appointment> b)
        { 
            b.HasKey(x => x.Id); b.HasQueryFilter(x => !x.IsDeleted); 
            b.Property(x => x.UpdatedAt).IsConcurrencyToken();
            b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.TreatmentStartedBy).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.TreatmentEndedBy).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BookingBatch>().WithMany().HasForeignKey(x => x.BookingBatchId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.BookingBatchId, x.BookingSequence }).IsUnique().HasFilter("[BookingBatchId] IS NOT NULL");
            b.HasIndex(x => new { x.DoctorId, x.StartTime });
            b.HasIndex(x => new { x.SeniorId, x.StartTime });
            b.HasIndex(x => new { x.RoomId, x.StartTime });
            b.HasIndex(x => new { x.PatientId, x.StartTime });
        }
    }
}
