using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NoPonto.Migrations;

[DbContext(typeof(TransporteDbContext))]
[Migration("20261008090000_EtaTripEvidenceFoundation")]
public sealed class EtaTripEvidenceFoundation : Migration
{
    // Raw-SQL repository, like ViagensOperacionais/OutboxViagens: no operational model changed.
    public const string SchemaSql = """
        CREATE TABLE "EtaProducerEpochs" (
            "EpochId" uuid PRIMARY KEY CHECK ("EpochId" <> '00000000-0000-0000-0000-000000000000'),
            "ProfileHash" text NOT NULL CHECK ("ProfileHash" ~ '^[0-9a-f]{64}$'),
            "Payload" jsonb NOT NULL CHECK (jsonb_typeof("Payload")='object')
        );
        CREATE TABLE "EtaEvidenceOwners" (
            "ScopeHash" text NOT NULL CHECK ("ScopeHash" ~ '^[0-9a-f]{64}$'),
            "Token" bigint NOT NULL CHECK ("Token">0),
            "EpochId" uuid NOT NULL REFERENCES "EtaProducerEpochs"("EpochId") ON DELETE RESTRICT,
            "AcquiredUs" bigint NOT NULL CHECK ("AcquiredUs">0),
            "ReleasedUs" bigint NULL CHECK ("ReleasedUs" IS NULL OR "ReleasedUs">="AcquiredUs"),
            PRIMARY KEY ("ScopeHash","Token")
        );
        CREATE UNIQUE INDEX "IX_EtaEvidenceOwners_Current" ON "EtaEvidenceOwners"("ScopeHash") WHERE "ReleasedUs" IS NULL;
        CREATE TABLE "EtaEvidenceHeads" (
            "ViagemId" uuid PRIMARY KEY CHECK ("ViagemId" <> '00000000-0000-0000-0000-000000000000'),
            "ScopeHash" text NOT NULL,
            "OwnerToken" bigint NOT NULL,
            "EpochId" uuid NOT NULL REFERENCES "EtaProducerEpochs"("EpochId") ON DELETE RESTRICT,
            "Sequence" bigint NOT NULL CHECK ("Sequence">0),
            "Digest" text NOT NULL CHECK ("Digest" ~ '^[0-9a-f]{64}$'),
            "Payload" jsonb NOT NULL CHECK (jsonb_typeof("Payload")='object'),
            FOREIGN KEY ("ScopeHash","OwnerToken") REFERENCES "EtaEvidenceOwners"("ScopeHash","Token") ON DELETE RESTRICT
        );
        CREATE TABLE "EtaEvidenceJournal" (
            "EventId" text PRIMARY KEY,
            "ViagemId" uuid NOT NULL REFERENCES "EtaEvidenceHeads"("ViagemId") ON DELETE RESTRICT,
            "Sequence" bigint NOT NULL CHECK ("Sequence">0),
            "Payload" jsonb NOT NULL CHECK (jsonb_typeof("Payload")='object'),
            UNIQUE ("ViagemId","Sequence")
        );
        """;
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SchemaSql);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new InvalidOperationException(
        "Evidence must be preserved; disable its future producer instead of destroying the ledger.");
}
