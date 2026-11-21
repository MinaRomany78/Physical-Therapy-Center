using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PTCenter.Domain.Entities;

namespace PTCenter.Infrastructure.Persistence.Configurations;

public class ClinicSettingsConfiguration : IEntityTypeConfiguration<ClinicSettings>
{
    public void Configure(EntityTypeBuilder<ClinicSettings> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Phone).HasMaxLength(50);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.Email).HasMaxLength(200);
        b.Property(x => x.UpdatedBy).HasMaxLength(450);
        b.Property(x => x.Version).IsConcurrencyToken();
    }
}
public class CeoUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> b)
    {
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.StatusChangedBy).HasMaxLength(450);
        b.HasIndex(x => x.IsActive);
    }
}
public class CeoPaymentIndexConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b) => b.HasIndex(x => new { x.Status, x.PaymentDate }).IncludeProperties(x => new { x.Amount, x.InvoiceId, x.PayerType, x.PaymentMethod });
}
public class CeoAppointmentIndexConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> b) => b.HasIndex(x => new { x.StartTime, x.Status });
}
