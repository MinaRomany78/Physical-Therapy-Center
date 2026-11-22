using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CeoWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAt",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusChangedBy",
                table: "AspNetUsers",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClinicSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicSettings", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: "e4b2f91d-9e12-4f4a-b3db-1b58fca15e66",
                columns: new[] { "IsActive", "StatusChangedAt", "StatusChangedBy" },
                values: new object[] { true, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Status_PaymentDate",
                table: "Payments",
                columns: new[] { "Status", "PaymentDate" })
                .Annotation("SqlServer:Include", new[] { "Amount", "InvoiceId", "PayerType", "PaymentMethod" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_IsActive",
                table: "AspNetUsers",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_StartTime_Status",
                table: "Appointments",
                columns: new[] { "StartTime", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClinicSettings");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Status_PaymentDate",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_IsActive",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_StartTime_Status",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StatusChangedBy",
                table: "AspNetUsers");
        }
    }
}
