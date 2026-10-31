BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    ALTER TABLE [Appointments] ADD [TreatmentEndedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    ALTER TABLE [Appointments] ADD [TreatmentEndedBy] nvarchar(450) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    ALTER TABLE [Appointments] ADD [TreatmentStartedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    ALTER TABLE [Appointments] ADD [TreatmentStartedBy] nvarchar(450) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE TABLE [ReassessmentRequests] (
        [Id] int NOT NULL IDENTITY,
        [PatientId] int NOT NULL,
        [TreatmentPlanId] int NOT NULL,
        [RelatedAppointmentId] int NULL,
        [RequestedBy] nvarchar(450) NOT NULL,
        [RequestedAt] datetime2 NOT NULL,
        [ReasonCategory] nvarchar(50) NOT NULL,
        [Notes] nvarchar(4000) NOT NULL,
        [Recommendation] nvarchar(20) NOT NULL,
        [State] nvarchar(20) NOT NULL,
        [ActivePlanId] int NULL,
        [ReviewingSeniorId] nvarchar(450) NULL,
        [ReviewStartedAt] datetime2 NULL,
        [CompletedAssessmentId] int NULL,
        [RevisedPlanId] int NULL,
        [CancellationReason] nvarchar(1000) NULL,
        [ResolvedAt] datetime2 NULL,
        [ResolvedBy] nvarchar(max) NULL,
        [Version] bigint NOT NULL,
        [OperationKey] nvarchar(100) NOT NULL,
        [CompletionHash] nvarchar(64) NULL,
        [ReviewRequired] bit NOT NULL,
        [Decision] nvarchar(20) NULL,
        CONSTRAINT [PK_ReassessmentRequests] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ReassessmentRequests_Appointments_RelatedAppointmentId] FOREIGN KEY ([RelatedAppointmentId]) REFERENCES [Appointments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ReassessmentRequests_AspNetUsers_RequestedBy] FOREIGN KEY ([RequestedBy]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ReassessmentRequests_AspNetUsers_ReviewingSeniorId] FOREIGN KEY ([ReviewingSeniorId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ReassessmentRequests_Assessments_CompletedAssessmentId] FOREIGN KEY ([CompletedAssessmentId]) REFERENCES [Assessments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ReassessmentRequests_Patients_PatientId] FOREIGN KEY ([PatientId]) REFERENCES [Patients] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ReassessmentRequests_TreatmentPlans_RevisedPlanId] FOREIGN KEY ([RevisedPlanId]) REFERENCES [TreatmentPlans] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ReassessmentRequests_TreatmentPlans_TreatmentPlanId] FOREIGN KEY ([TreatmentPlanId]) REFERENCES [TreatmentPlans] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE TABLE [TreatmentSessionRecords] (
        [AppointmentId] int NOT NULL,
        [ProgressNotes] nvarchar(max) NOT NULL,
        [ConditionChanges] nvarchar(max) NOT NULL,
        [ResponseToTreatment] nvarchar(max) NOT NULL,
        [TreatmentPerformed] nvarchar(max) NOT NULL,
        [Version] bigint NOT NULL,
        [AuthorId] nvarchar(450) NOT NULL,
        [SavedAt] datetime2 NOT NULL,
        [FinalizedAt] datetime2 NULL,
        CONSTRAINT [PK_TreatmentSessionRecords] PRIMARY KEY ([AppointmentId]),
        CONSTRAINT [FK_TreatmentSessionRecords_Appointments_AppointmentId] FOREIGN KEY ([AppointmentId]) REFERENCES [Appointments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TreatmentSessionRecords_AspNetUsers_AuthorId] FOREIGN KEY ([AuthorId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE INDEX [IX_Appointments_TreatmentEndedBy] ON [Appointments] ([TreatmentEndedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE INDEX [IX_Appointments_TreatmentStartedBy] ON [Appointments] ([TreatmentStartedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_ReassessmentRequests_ActivePlanId] ON [ReassessmentRequests] ([ActivePlanId]) WHERE [ActivePlanId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE INDEX [IX_ReassessmentRequests_CompletedAssessmentId] ON [ReassessmentRequests] ([CompletedAssessmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ReassessmentRequests_OperationKey] ON [ReassessmentRequests] ([OperationKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE INDEX [IX_ReassessmentRequests_PatientId] ON [ReassessmentRequests] ([PatientId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE INDEX [IX_ReassessmentRequests_RelatedAppointmentId] ON [ReassessmentRequests] ([RelatedAppointmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE INDEX [IX_ReassessmentRequests_RequestedBy] ON [ReassessmentRequests] ([RequestedBy]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE INDEX [IX_ReassessmentRequests_ReviewingSeniorId] ON [ReassessmentRequests] ([ReviewingSeniorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE INDEX [IX_ReassessmentRequests_RevisedPlanId] ON [ReassessmentRequests] ([RevisedPlanId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE INDEX [IX_ReassessmentRequests_TreatmentPlanId] ON [ReassessmentRequests] ([TreatmentPlanId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    CREATE INDEX [IX_TreatmentSessionRecords_AuthorId] ON [TreatmentSessionRecords] ([AuthorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    ALTER TABLE [Appointments] ADD CONSTRAINT [FK_Appointments_AspNetUsers_TreatmentEndedBy] FOREIGN KEY ([TreatmentEndedBy]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    ALTER TABLE [Appointments] ADD CONSTRAINT [FK_Appointments_AspNetUsers_TreatmentStartedBy] FOREIGN KEY ([TreatmentStartedBy]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164400_DoctorClinicalWorkspace'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031164400_DoctorClinicalWorkspace', N'10.0.2');
END;

COMMIT;
GO

