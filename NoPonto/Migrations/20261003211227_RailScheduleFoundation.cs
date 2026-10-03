using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class RailScheduleFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RailScheduleVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    LineId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceName = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    ImportedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReferenceWeekdayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReferenceSaturdayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReferenceSundayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    BuilderVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RailScheduleVersions", x => x.Id);
                    table.CheckConstraint("CK_RailScheduleVersions_SchemaVersion", "\"SchemaVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_RailScheduleVersions_Linhas_LineId",
                        column: x => x.LineId,
                        principalTable: "Linhas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RailSchedulePatterns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<Guid>(type: "uuid", nullable: false),
                    SentidoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalPatternId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SignatureHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MappedPadraoVersaoId = table.Column<Guid>(type: "uuid", nullable: true),
                    MappingStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    StationCount = table.Column<int>(type: "integer", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RailSchedulePatterns", x => x.Id);
                    table.CheckConstraint("CK_RailSchedulePatterns_MappingStatus", "\"MappingStatus\" IN ('EXACT','SUBSET_COMPATIBLE','UNRESOLVED','CONFLICT')");
                    table.ForeignKey(
                        name: "FK_RailSchedulePatterns_Linhas_LineId",
                        column: x => x.LineId,
                        principalTable: "Linhas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RailSchedulePatterns_PadroesVersoes_MappedPadraoVersaoId",
                        column: x => x.MappedPadraoVersaoId,
                        principalTable: "PadroesVersoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RailSchedulePatterns_RailScheduleVersions_ScheduleVersionId",
                        column: x => x.ScheduleVersionId,
                        principalTable: "RailScheduleVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RailSchedulePatterns_Sentidos_SentidoId",
                        column: x => x.SentidoId,
                        principalTable: "Sentidos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RailScheduledRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchedulePatternId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<Guid>(type: "uuid", nullable: false),
                    SentidoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalScheduledRunId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CalendarType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DepartureTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    DepartureDayOffset = table.Column<int>(type: "integer", nullable: false),
                    TerminalArrivalTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    TerminalArrivalDayOffset = table.Column<int>(type: "integer", nullable: false),
                    FirstStationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalStationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShortStartCandidate = table.Column<bool>(type: "boolean", nullable: false),
                    CrossesMidnight = table.Column<bool>(type: "boolean", nullable: false),
                    Confidence = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SourceServiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RailScheduledRuns", x => x.Id);
                    table.CheckConstraint("CK_RailScheduledRuns_CalendarType", "\"CalendarType\" IN ('WEEKDAY','SATURDAY','SUNDAY')");
                    table.CheckConstraint("CK_RailScheduledRuns_DayOffsets", "\"DepartureDayOffset\" >= 0 AND \"TerminalArrivalDayOffset\" >= \"DepartureDayOffset\"");
                    table.ForeignKey(
                        name: "FK_RailScheduledRuns_Linhas_LineId",
                        column: x => x.LineId,
                        principalTable: "Linhas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RailScheduledRuns_Paradas_FirstStationId",
                        column: x => x.FirstStationId,
                        principalTable: "Paradas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RailScheduledRuns_Paradas_TerminalStationId",
                        column: x => x.TerminalStationId,
                        principalTable: "Paradas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RailScheduledRuns_RailSchedulePatterns_SchedulePatternId",
                        column: x => x.SchedulePatternId,
                        principalTable: "RailSchedulePatterns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RailScheduledRuns_RailScheduleVersions_ScheduleVersionId",
                        column: x => x.ScheduleVersionId,
                        principalTable: "RailScheduleVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RailScheduledRuns_Sentidos_SentidoId",
                        column: x => x.SentidoId,
                        principalTable: "Sentidos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RailScheduledStops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduledRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParadaId = table.Column<Guid>(type: "uuid", nullable: false),
                    StopSequence = table.Column<int>(type: "integer", nullable: false),
                    StationOrder = table.Column<int>(type: "integer", nullable: false),
                    ScheduledTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    DayOffset = table.Column<int>(type: "integer", nullable: false),
                    AbsoluteMinute = table.Column<int>(type: "integer", nullable: false),
                    IsObservedOrigin = table.Column<bool>(type: "boolean", nullable: false),
                    IsTerminal = table.Column<bool>(type: "boolean", nullable: false),
                    SourceRequestId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RailScheduledStops", x => x.Id);
                    table.CheckConstraint("CK_RailScheduledStops_Time", "\"StopSequence\" > 0 AND \"StationOrder\" > 0 AND \"DayOffset\" >= 0 AND \"AbsoluteMinute\" = EXTRACT(HOUR FROM \"ScheduledTime\")::int * 60 + EXTRACT(MINUTE FROM \"ScheduledTime\")::int + \"DayOffset\" * 1440");
                    table.ForeignKey(
                        name: "FK_RailScheduledStops_Paradas_ParadaId",
                        column: x => x.ParadaId,
                        principalTable: "Paradas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RailScheduledStops_RailScheduledRuns_ScheduledRunId",
                        column: x => x.ScheduledRunId,
                        principalTable: "RailScheduledRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduledRuns_CalendarType_DepartureTime",
                table: "RailScheduledRuns",
                columns: new[] { "CalendarType", "DepartureTime" });

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduledRuns_FirstStationId",
                table: "RailScheduledRuns",
                column: "FirstStationId");

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduledRuns_LineId",
                table: "RailScheduledRuns",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduledRuns_SchedulePatternId",
                table: "RailScheduledRuns",
                column: "SchedulePatternId");

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduledRuns_ScheduleVersionId_CalendarType_SentidoId",
                table: "RailScheduledRuns",
                columns: new[] { "ScheduleVersionId", "CalendarType", "SentidoId" });

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduledRuns_ScheduleVersionId_ExternalScheduledRunId",
                table: "RailScheduledRuns",
                columns: new[] { "ScheduleVersionId", "ExternalScheduledRunId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduledRuns_SentidoId",
                table: "RailScheduledRuns",
                column: "SentidoId");

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduledRuns_TerminalStationId",
                table: "RailScheduledRuns",
                column: "TerminalStationId");

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduledStops_ParadaId",
                table: "RailScheduledStops",
                column: "ParadaId");

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduledStops_ScheduledRunId_ParadaId",
                table: "RailScheduledStops",
                columns: new[] { "ScheduledRunId", "ParadaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduledStops_ScheduledRunId_StopSequence",
                table: "RailScheduledStops",
                columns: new[] { "ScheduledRunId", "StopSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RailSchedulePatterns_LineId",
                table: "RailSchedulePatterns",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_RailSchedulePatterns_MappedPadraoVersaoId",
                table: "RailSchedulePatterns",
                column: "MappedPadraoVersaoId");

            migrationBuilder.CreateIndex(
                name: "IX_RailSchedulePatterns_ScheduleVersionId_ExternalPatternId",
                table: "RailSchedulePatterns",
                columns: new[] { "ScheduleVersionId", "ExternalPatternId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RailSchedulePatterns_ScheduleVersionId_MappingStatus",
                table: "RailSchedulePatterns",
                columns: new[] { "ScheduleVersionId", "MappingStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_RailSchedulePatterns_SentidoId",
                table: "RailSchedulePatterns",
                column: "SentidoId");

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduleVersions_LineId",
                table: "RailScheduleVersions",
                column: "LineId",
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduleVersions_LineId_ContentHash",
                table: "RailScheduleVersions",
                columns: new[] { "LineId", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RailScheduleVersions_LineId_IsActive",
                table: "RailScheduleVersions",
                columns: new[] { "LineId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RailScheduledStops");

            migrationBuilder.DropTable(
                name: "RailScheduledRuns");

            migrationBuilder.DropTable(
                name: "RailSchedulePatterns");

            migrationBuilder.DropTable(
                name: "RailScheduleVersions");
        }
    }
}
