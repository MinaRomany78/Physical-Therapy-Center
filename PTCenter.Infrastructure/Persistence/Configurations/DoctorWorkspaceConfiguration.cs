using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PTCenter.Domain.Entities;

namespace PTCenter.Infrastructure.Persistence.Configurations;

public class TreatmentSessionRecordConfiguration : IEntityTypeConfiguration<TreatmentSessionRecord>
{
    public void Configure(EntityTypeBuilder<TreatmentSessionRecord> b)
    {
        b.HasKey(x => x.AppointmentId);
        b.HasOne<Appointment>().WithOne().HasForeignKey<TreatmentSessionRecord>(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.ProgressNotes).HasMaxLength(10000);
        b.Property(x => x.ConditionChanges).HasMaxLength(10000);
        b.Property(x => x.ResponseToTreatment).HasMaxLength(10000);
        b.Property(x => x.TreatmentPerformed).HasMaxLength(10000);
    }
}

public class ReassessmentRequestConfiguration : IEntityTypeConfiguration<ReassessmentRequest>
{
    public void Configure(EntityTypeBuilder<ReassessmentRequest> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.ActivePlanId).IsUnique().HasFilter("[ActivePlanId] IS NOT NULL");
        b.HasIndex(x => x.OperationKey).IsUnique();
        b.Property(x => x.OperationKey).HasMaxLength(100);
        b.Property(x => x.CompletionHash).HasMaxLength(64);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.State).HasMaxLength(20);
        b.Property(x => x.Decision).HasMaxLength(20);
        b.Property(x => x.ReasonCategory).HasMaxLength(50);
        b.Property(x => x.Recommendation).HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(4000);
        b.Property(x => x.CancellationReason).HasMaxLength(1000);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TreatmentPlan>().WithMany().HasForeignKey(x => x.TreatmentPlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TreatmentPlan>().WithMany().HasForeignKey(x => x.RevisedPlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Appointment>().WithMany().HasForeignKey(x => x.RelatedAppointmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ReviewingSeniorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Assessment>().WithMany().HasForeignKey(x => x.CompletedAssessmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
