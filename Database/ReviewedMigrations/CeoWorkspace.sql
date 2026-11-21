BEGIN TRANSACTION;
ALTER TABLE [AspNetUsers] ADD [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit);

ALTER TABLE [AspNetUsers] ADD [StatusChangedAt] datetime2 NULL;

ALTER TABLE [AspNetUsers] ADD [StatusChangedBy] nvarchar(450) NULL;

CREATE TABLE [ClinicSettings] (
    [Id] int NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Phone] nvarchar(50) NULL,
    [Address] nvarchar(500) NULL,
    [Email] nvarchar(200) NULL,
    [Version] int NOT NULL,
    [UpdatedBy] nvarchar(450) NULL,
    [UpdatedAt] datetime2 NULL,
    CONSTRAINT [PK_ClinicSettings] PRIMARY KEY ([Id])
);

UPDATE [AspNetUsers] SET [IsActive] = CAST(1 AS bit), [StatusChangedAt] = NULL, [StatusChangedBy] = NULL
WHERE [Id] = N'e4b2f91d-9e12-4f4a-b3db-1b58fca15e66';
SELECT @@ROWCOUNT;


CREATE INDEX [IX_Payments_Status_PaymentDate] ON [Payments] ([Status], [PaymentDate]) INCLUDE ([Amount], [InvoiceId], [PayerType], [PaymentMethod]);

CREATE INDEX [IX_AspNetUsers_IsActive] ON [AspNetUsers] ([IsActive]);

CREATE INDEX [IX_Appointments_StartTime_Status] ON [Appointments] ([StartTime], [Status]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261121153041_CeoWorkspace', N'10.0.2');

COMMIT;
GO

