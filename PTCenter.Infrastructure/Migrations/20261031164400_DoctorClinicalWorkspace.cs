using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DoctorClinicalWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TreatmentEndedAt",
                table: "Appointments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreatmentEndedBy",
                table: "Appointments",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TreatmentStartedAt",
                table: "Appointments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreatmentStartedBy",
                table: "Appointments",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReassessmentRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    TreatmentPlanId = table.Column<int>(type: "int", nullable: false),
                    RelatedAppointmentId = table.Column<int>(type: "int", nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReasonCategory = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Recommendation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ActivePlanId = table.Column<int>(type: "int", nullable: true),
                    ReviewingSeniorId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    ReviewStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAssessmentId = table.Column<int>(type: "int", nullable: true),
                    RevisedPlanId = table.Column<int>(type: "int", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    OperationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CompletionHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReviewRequired = table.Column<bool>(type: "bit", nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReassessmentRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReassessmentRequests_Appointments_RelatedAppointmentId",
                        column: x => x.RelatedAppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReassessmentRequests_AspNetUsers_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReassessmentRequests_AspNetUsers_ReviewingSeniorId",
                        column: x => x.ReviewingSeniorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReassessmentRequests_Assessments_CompletedAssessmentId",
                        column: x => x.CompletedAssessmentId,
                        principalTable: "Assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReassessmentRequests_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReassessmentRequests_TreatmentPlans_RevisedPlanId",
                        column: x => x.RevisedPlanId,
                        principalTable: "TreatmentPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReassessmentRequests_TreatmentPlans_TreatmentPlanId",
                        column: x => x.TreatmentPlanId,
                        principalTable: "TreatmentPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TreatmentSessionRecords",
                columns: table => new
                {
                    AppointmentId = table.Column<int>(type: "int", nullable: false),
                    ProgressNotes = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    ConditionChanges = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    ResponseToTreatment = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    TreatmentPerformed = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    AuthorId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinalizedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreatmentSessionRecords", x => x.AppointmentId);
                    table.ForeignKey(
                        name: "FK_TreatmentSessionRecords_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TreatmentSessionRecords_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_TreatmentEndedBy",
                table: "Appointments",
                column: "TreatmentEndedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_TreatmentStartedBy",
                table: "Appointments",
                column: "TreatmentStartedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ReassessmentRequests_ActivePlanId",
                table: "ReassessmentRequests",
                column: "ActivePlanId",
                unique: true,
                filter: "[ActivePlanId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReassessmentRequests_CompletedAssessmentId",
                table: "ReassessmentRequests",
                column: "CompletedAssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ReassessmentRequests_OperationKey",
                table: "ReassessmentRequests",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReassessmentRequests_PatientId",
                table: "ReassessmentRequests",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_ReassessmentRequests_RelatedAppointmentId",
                table: "ReassessmentRequests",
                column: "RelatedAppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ReassessmentRequests_RequestedBy",
                table: "ReassessmentRequests",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ReassessmentRequests_ReviewingSeniorId",
                table: "ReassessmentRequests",
                column: "ReviewingSeniorId");

            migrationBuilder.CreateIndex(
                name: "IX_ReassessmentRequests_RevisedPlanId",
                table: "ReassessmentRequests",
                column: "RevisedPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_ReassessmentRequests_TreatmentPlanId",
                table: "ReassessmentRequests",
                column: "TreatmentPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_TreatmentSessionRecords_AuthorId",
                table: "TreatmentSessionRecords",
                column: "AuthorId");

            migrationBuilder.AddForeignKey(
                name: "FK_Appointments_AspNetUsers_TreatmentEndedBy",
                table: "Appointments",
                column: "TreatmentEndedBy",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Appointments_AspNetUsers_TreatmentStartedBy",
                table: "Appointments",
                column: "TreatmentStartedBy",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Appointments_AspNetUsers_TreatmentEndedBy",
                table: "Appointments");

            migrationBuilder.DropForeignKey(
                name: "FK_Appointments_AspNetUsers_TreatmentStartedBy",
                table: "Appointments");

            migrationBuilder.DropTable(
                name: "ReassessmentRequests");

            migrationBuilder.DropTable(
                name: "TreatmentSessionRecords");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_TreatmentEndedBy",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_TreatmentStartedBy",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "TreatmentEndedAt",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "TreatmentEndedBy",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "TreatmentStartedAt",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "TreatmentStartedBy",
                table: "Appointments");
        }
    }
}
