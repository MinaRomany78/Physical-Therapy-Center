using PTCenter.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PTCenter.Domain.Entities.Enums;

namespace PTCenter.Infrastructure.Persistence;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ApplicationUser> ApplicationUsers { get; set; } = null!;
    
    public DbSet<Patient> Patients { get; set; }

    public DbSet<InsuranceCompany> InsuranceCompanies { get; set; }
    public DbSet<InsuranceDetails> InsuranceDetails { get; set; }

    public DbSet<InsuranceDocument> InsuranceDocuments { get; set; }

    public DbSet<Room> Rooms { get; set; }

    public DbSet<DoctorSchedule> DoctorSchedules { get; set; }

    public DbSet<Assessment> Assessments { get; set; }

    public DbSet<TreatmentPlan> TreatmentPlans { get; set; }

    public DbSet<TreatmentPlanSession> TreatmentPlanSessions { get; set; }

    public DbSet<Appointment> Appointments { get; set; }
    public DbSet<AppointmentScheduleChange> AppointmentScheduleChanges { get; set; }
    public DbSet<BookingBatch> BookingBatches { get; set; }
    public DbSet<TreatmentCourseAgreement> TreatmentCourseAgreements { get; set; }
    public DbSet<InsuranceClaim> InsuranceClaims { get; set; }

    public DbSet<SessionNote> SessionNotes { get; set; }
    public DbSet<TreatmentSessionRecord> TreatmentSessionRecords { get; set; }
    public DbSet<ReassessmentRequest> ReassessmentRequests { get; set; }

    public DbSet<ReAssessment> ReAssessments { get; set; }

    public DbSet<Invoice> Invoices { get; set; }

    public DbSet<Payment> Payments { get; set; }

    public DbSet<DoctorAbsence> DoctorAbsences { get; set; }

    public DbSet<Notification> Notifications { get; set; }
    public DbSet<OTPVerify> OTPVerifies { get; set; }
    
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
    public override int SaveChanges()
    {
        ApplySoftDelete();
        return base.SaveChanges();
    }
    
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplySoftDelete();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void ApplySoftDelete()
    {
        foreach (var e in ChangeTracker.Entries<BaseEntity>())
        {
            if (e.State == EntityState.Deleted)
            {
                e.State = EntityState.Modified;
                e.Entity.IsDeleted = true;
                e.Entity.DeletedAt = DateTime.UtcNow;
            }
            if (e.State == EntityState.Added)
                e.Entity.CreatedAt = DateTime.UtcNow;
            if (e.State == EntityState.Modified)
                e.Entity.UpdatedAt = DateTime.UtcNow;
        }
    }
}
