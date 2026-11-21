using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PTCenter.Infrastructure.Persistence;
using Xunit;

namespace PTCenter.Tests;

public class BookingMigrationTests
{
    [Fact] public void Merged_migration_chain_retains_registration_cheque_and_clinical_models_without_schema_drift()
    {
        using var db=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=MergeDesignOnly;Trusted_Connection=True").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var chain=db.Database.GetMigrations().ToArray();
        var retained=new[]{"20261031164400_DoctorClinicalWorkspace","20261104195622_ClinicianAbsenceRecovery",
            "20261106140342_ReleaseReadinessPatientRegistration","20261106160811_InsuranceChequeReceipts"};
        Assert.Equal(retained,chain.Where(retained.Contains).ToArray());
        Assert.Equal("20261121153041_CeoWorkspace",chain[^1]);
        var draft=db.GetService<IMigrator>().GenerateScript(retained[^1],"20261107105511_AssessmentDraftPersistence");
        Assert.Contains("ALTER TABLE [Assessments] ADD [DraftJson] nvarchar(max) NULL",draft);
        Assert.DoesNotContain("UPDATE [",draft); Assert.DoesNotContain("DROP ",draft);
        var upload=db.GetService<IMigrator>().GenerateScript("20261107105511_AssessmentDraftPersistence","20261107120334_DocumentUploadRetrySafety");
        Assert.Contains("[UploadOperationKey] nvarchar(100) NULL",upload);Assert.Contains("CREATE UNIQUE INDEX",upload);
        Assert.DoesNotContain("UPDATE [",upload);Assert.DoesNotContain("DROP ",upload);Assert.DoesNotContain("DELETE ",upload);
        var ceo=db.GetService<IMigrator>().GenerateScript("20261107120334_DocumentUploadRetrySafety",chain[^1]);
        Assert.Contains("CREATE TABLE [ClinicSettings]",ceo);Assert.Contains("[IsActive] bit NOT NULL DEFAULT",ceo);
        Assert.Contains("IX_Payments_Status_PaymentDate",ceo);
        foreach(var forbidden in new[]{"DROP TABLE","DROP COLUMN","ALTER COLUMN","DELETE FROM","TRUNCATE","UPDATE [Invoices]","UPDATE [Payments]","UPDATE [Appointments]"})Assert.DoesNotContain(forbidden,ceo);
        var sql=db.GetService<IMigrator>().GenerateScript(retained[1],retained[3],MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("[RegistrationOperationKey]",sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Patients_RegistrationOperationKey]",sql);
        Assert.Contains("CREATE TABLE [InsuranceChequeReceipts]",sql);
        Assert.Contains("CREATE TABLE [InsuranceClaimSubmissions]",sql);
        Assert.Contains("[InsuranceServiceMonth] date NULL",sql);
        foreach(var forbidden in new[]{"DROP TABLE","DROP COLUMN","ALTER COLUMN","UPDATE [","DELETE FROM","TRUNCATE","ON DELETE CASCADE","INSERT INTO [Invoices]","INSERT INTO [Payments]"})Assert.DoesNotContain(forbidden,sql);
    }

    [Fact] public void Absence_recovery_migration_is_additive_and_preserves_legacy_day_absences_and_finances()
    {
        using var db=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=AbsenceDesignOnly;Trusted_Connection=True").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var sql=db.GetService<IMigrator>().GenerateScript("20261031164400_DoctorClinicalWorkspace","20261104195622_ClinicianAbsenceRecovery",MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("[StartsAt] datetime2 NULL",sql);Assert.Contains("[EndsAt] datetime2 NULL",sql);Assert.Contains("DEFAULT N'[]'",sql);Assert.Contains("[ConsentRecordedAt]",sql);
        foreach(var forbidden in new[]{"DROP TABLE","DROP COLUMN","ALTER COLUMN","UPDATE [","DELETE FROM","TRUNCATE","ON DELETE CASCADE","INSERT INTO [Invoices]","INSERT INTO [Payments]"})Assert.DoesNotContain(forbidden,sql);
    }
    [Fact] public void Doctor_migration_is_additive_and_does_not_infer_history_or_touch_financial_records()
    {
        using var db=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=DoctorDesignOnly;Trusted_Connection=True").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var sql=db.GetService<IMigrator>().GenerateScript("20261031122754_AppointmentScheduleAudit","20261031164400_DoctorClinicalWorkspace",MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [TreatmentSessionRecords]",sql);Assert.Contains("CREATE TABLE [ReassessmentRequests]",sql);
        Assert.Contains("[TreatmentStartedAt] datetime2 NULL",sql);Assert.Contains("[TreatmentEndedAt] datetime2 NULL",sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_ReassessmentRequests_ActivePlanId]",sql);
        foreach(var forbidden in new[]{"DROP TABLE","DROP COLUMN","ALTER COLUMN","UPDATE [","DELETE FROM","TRUNCATE","ON DELETE CASCADE","INSERT INTO [Invoices]","INSERT INTO [Payments]"})Assert.DoesNotContain(forbidden,sql);
    }
    [Fact] public void SqlServer_migration_matches_the_current_model_and_generates_without_a_database_connection()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=PTCenter_MigrationDesignOnly;Trusted_Connection=True;TrustServerCertificate=True").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript("20261024151158_AddInsuranceDetails", "20261029124607_BookingWorkflow", MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [BookingBatches]", sql);
        Assert.Contains("IX_Payments_IdempotencyKey", sql);
        Assert.Contains("Review legacy SeniorId", sql);
        Assert.Contains("[CompletedAt]", sql);
        var finance = db.GetService<IMigrator>().GenerateScript("20261029124607_BookingWorkflow", "20261030184822_CourseFinancialAgreements", MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [TreatmentCourseAgreements]",finance);
        Assert.Contains("CREATE TABLE [InsuranceClaims]",finance);
        Assert.Contains("ALTER COLUMN [AppointmentId] int NULL",finance);
        Assert.DoesNotContain("DROP TABLE",finance);Assert.DoesNotContain("DROP COLUMN",finance);
        Assert.DoesNotContain("DELETE FROM",finance);Assert.DoesNotContain("UPDATE [",finance);Assert.DoesNotContain("TRUNCATE",finance);
        var audit=db.GetService<IMigrator>().GenerateScript("20261030184822_CourseFinancialAgreements","20261031122754_AppointmentScheduleAudit",MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [AppointmentScheduleChanges]",audit);Assert.Contains("CREATE UNIQUE INDEX [IX_AppointmentScheduleChanges_OperationKey]",audit);
        Assert.DoesNotContain("DROP TABLE",audit);Assert.DoesNotContain("DROP COLUMN",audit);Assert.DoesNotContain("DELETE FROM",audit);Assert.DoesNotContain("UPDATE [",audit);Assert.DoesNotContain("ALTER COLUMN",audit);
    }
}
