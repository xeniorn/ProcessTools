using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xeniorn.ProcessTools.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tracked_process",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ParentProcessId = table.Column<int>(type: "INTEGER", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedBeforeTracking = table.Column<bool>(type: "INTEGER", nullable: false),
                    EndedObservedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExitedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    EndReason = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracked_process", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "period",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrackedProcessId = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstSampleUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSampleUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPartial = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_period", x => x.Id);
                    table.ForeignKey(
                        name: "FK_period_tracked_process_TrackedProcessId",
                        column: x => x.TrackedProcessId,
                        principalTable: "tracked_process",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "process_dump",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrackedProcessId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Metric = table.Column<int>(type: "INTEGER", nullable: false),
                    Rule = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ObservedValue = table.Column<double>(type: "REAL", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_process_dump", x => x.Id);
                    table.ForeignKey(
                        name: "FK_process_dump_tracked_process_TrackedProcessId",
                        column: x => x.TrackedProcessId,
                        principalTable: "tracked_process",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "period_metric",
                columns: table => new
                {
                    PeriodId = table.Column<long>(type: "INTEGER", nullable: false),
                    Metric = table.Column<int>(type: "INTEGER", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    Min = table.Column<double>(type: "REAL", nullable: true),
                    Max = table.Column<double>(type: "REAL", nullable: true),
                    Mean = table.Column<double>(type: "REAL", nullable: true),
                    Median = table.Column<double>(type: "REAL", nullable: true),
                    StdDev = table.Column<double>(type: "REAL", nullable: true),
                    Rms = table.Column<double>(type: "REAL", nullable: true),
                    Last = table.Column<double>(type: "REAL", nullable: true),
                    RawValues = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_period_metric", x => new { x.PeriodId, x.Metric });
                    table.ForeignKey(
                        name: "FK_period_metric_period_PeriodId",
                        column: x => x.PeriodId,
                        principalTable: "period",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_period_FirstSampleUtc",
                table: "period",
                column: "FirstSampleUtc");

            migrationBuilder.CreateIndex(
                name: "IX_period_TrackedProcessId_FirstSampleUtc",
                table: "period",
                columns: new[] { "TrackedProcessId", "FirstSampleUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_process_dump_TrackedProcessId_CreatedUtc",
                table: "process_dump",
                columns: new[] { "TrackedProcessId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_tracked_process_Name",
                table: "tracked_process",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_tracked_process_ProcessId_StartTimeUtc",
                table: "tracked_process",
                columns: new[] { "ProcessId", "StartTimeUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "period_metric");

            migrationBuilder.DropTable(
                name: "process_dump");

            migrationBuilder.DropTable(
                name: "period");

            migrationBuilder.DropTable(
                name: "tracked_process");
        }
    }
}
