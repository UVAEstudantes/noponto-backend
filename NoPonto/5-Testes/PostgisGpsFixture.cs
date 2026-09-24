using Npgsql;
using Xunit;

namespace NoPonto.Tests;

/// <summary>
/// Requer POSTGIS_TEST_CONNECTION: Host=localhost;Port=5432;Database=transporte_db;
/// Username=transporte_user;Password=&lt;senha local&gt;. Ausência/falha do banco falha os testes.
/// Cada fixture cria apenas seu schema gps23_&lt;guid&gt;; nunca usa tabelas da aplicação.
/// </summary>
public sealed class PostgisGpsFixture : IAsyncLifetime
{
    public string Schema { get; } = "gps23_" + Guid.NewGuid().ToString("N");
    public string Version { get; private set; } = "";
    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public Guid R1 { get; } = Guid.NewGuid();
    public Guid R2 { get; } = Guid.NewGuid();
    public Guid Volta { get; } = Guid.NewGuid();
    public Guid OutraLinha { get; } = Guid.NewGuid();
    public Guid Diagonal { get; } = Guid.NewGuid();
    public Guid X { get; } = Guid.NewGuid();
    public Guid ParalelasMesmaLinha { get; } = Guid.NewGuid();
    public Guid EmpateA { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public Guid EmpateB { get; } = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public Guid ScoreUuidMenor { get; } = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public Guid ScoreMelhor { get; } = Guid.Parse("00000000-0000-0000-0000-000000000004");
    public Guid Circular { get; } = Guid.NewGuid();
    private NpgsqlDataSource? _admin;
    private bool _created;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Defina POSTGIS_TEST_CONNECTION para executar os testes PostGIS reais.");
        var builder = new NpgsqlConnectionStringBuilder(connection) { SearchPath = "public" };
        _admin = NpgsqlDataSource.Create(builder.ConnectionString);
        try
        {
            await using var conn = await _admin.OpenConnectionAsync();
            await using (var version = conn.CreateCommand())
            {
                version.CommandText = "SELECT postgis_lib_version()";
                Version = (string)(await version.ExecuteScalarAsync())!;
            }
            await using (var create = conn.CreateCommand())
            {
                create.CommandText = $"CREATE SCHEMA \"{Schema}\"";
                await create.ExecuteNonQueryAsync();
                _created = true;
            }
            await using (var ddl = conn.CreateCommand())
            {
                // Todas as tabelas referidas pela query existem aqui: nenhuma resolução em public.
                ddl.CommandText = $"""
                    CREATE TABLE "{Schema}"."Linhas" ("Id" uuid PRIMARY KEY, "Codigo" text NOT NULL);
                    CREATE TABLE "{Schema}"."Sentidos" ("Id" uuid PRIMARY KEY, "LinhaId" uuid NOT NULL);
                    CREATE TABLE "{Schema}"."Itinerarios" ("Id" uuid PRIMARY KEY, "SentidoId" uuid NOT NULL, "Geometria" geometry(LineString,4326) NOT NULL);
                    CREATE TABLE "{Schema}"."Paradas" ("Id" uuid PRIMARY KEY, "Nome" text, "Localizacao" geometry(Point,4326));
                    CREATE TABLE "{Schema}"."ParadasItinerario" (
                        "ParadaId" uuid,
                        "ItinerarioId" uuid,
                        "PosicaoLinha" double precision,
                        "Ativo" boolean NOT NULL DEFAULT true
                    );
                    """;
                await ddl.ExecuteNonQueryAsync();
            }
            builder.SearchPath = $"{Schema},public";
            DataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            var linha1 = Guid.NewGuid();
            var linha2 = Guid.NewGuid();
            var sentido1 = Guid.NewGuid();
            var sentido2 = Guid.NewGuid();
            var linhaX = Guid.NewGuid();
            var sentidoX = Guid.NewGuid();
            var linhaP = Guid.NewGuid();
            var sentidoP = Guid.NewGuid();
            var linhaEmpate = Guid.NewGuid();
            var sentidoEmpate = Guid.NewGuid();
            var linhaScore = Guid.NewGuid();
            var sentidoScore = Guid.NewGuid();
            var linhaCircular = Guid.NewGuid();
            var sentidoCircular = Guid.NewGuid();
            await using var seed = DataSource.CreateCommand("""
                INSERT INTO "Linhas" VALUES (@l1,'GPS23'),(@l2,'OUTRA23'),(@lx,'X25'),(@lp,'P25'),
                    (@le,'EMPATE'),(@ls,'SCORE'),(@lc,'CIRCULAR');
                INSERT INTO "Sentidos" VALUES (@s1,@l1),(@s2,@l2),(@sx,@lx),(@sp,@lp),
                    (@se,@le),(@ss,@ls),(@sc,@lc);
                INSERT INTO "Itinerarios" VALUES
                  (@r1,@s1,ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326)),
                  (@r2,@s1,ST_GeomFromText('LINESTRING(-43.21 -22.8998,-43.19 -22.8998)',4326)),
                  (@volta,@s1,ST_GeomFromText('LINESTRING(-43.19 -22.9001,-43.21 -22.9001)',4326)),
                  (@outra,@s2,ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326)),
                  (@diag,@s1,ST_GeomFromText('LINESTRING(-43.201 -22.901,-43.199 -22.899)',4326)),
                  (@x,@sx,ST_GeomFromText('LINESTRING(-0.01 -0.01,0.01 0.01,-0.01 0.01,0.01 -0.01)',4326)),
                  (@p,@sp,ST_GeomFromText('LINESTRING(-0.01 0,0.01 0,0.01 0.005,-0.01 0.005,-0.01 0.00002,0.01 0.00002)',4326)),
                  (@eb,@se,ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326)),
                  (@ea,@se,ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326)),
                  (@su,@ss,ST_GeomFromText('LINESTRING(-43.21 -22.8998,-43.19 -22.8998)',4326)),
                  (@sm,@ss,ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326)),
                  (@circ,@sc,ST_GeomFromText('LINESTRING(0 0,0.01 0,0.01 0.01,0 0.01,0 0)',4326));
                INSERT INTO "Paradas" VALUES
                  (@parada,'Parada controlada',ST_SetSRID(ST_MakePoint(-43.195,-22.9),4326)),
                  (@parada_r2,'Parada paralela',ST_SetSRID(ST_MakePoint(-43.195,-22.8998),4326));
                INSERT INTO "ParadasItinerario" VALUES
                  (@parada,@r1,0.75),(@parada_r2,@r2,0.75);
                """);
            foreach (var pair in new (string, Guid)[] { ("l1",linha1),("l2",linha2),("s1",sentido1),("s2",sentido2),
                ("r1",R1),("r2",R2),("volta",Volta),("outra",OutraLinha),("diag",Diagonal),
                ("parada",Guid.NewGuid()),("parada_r2",Guid.NewGuid()),
                ("lx",linhaX),("sx",sentidoX),("lp",linhaP),("sp",sentidoP),("x",X),("p",ParalelasMesmaLinha),
                ("le",linhaEmpate),("se",sentidoEmpate),("ea",EmpateA),("eb",EmpateB),
                ("ls",linhaScore),("ss",sentidoScore),("su",ScoreUuidMenor),("sm",ScoreMelhor),
                ("lc",linhaCircular),("sc",sentidoCircular),("circ",Circular) })
                seed.Parameters.AddWithValue(pair.Item1, pair.Item2);
            await seed.ExecuteNonQueryAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_admin is null) return;
        try
        {
            if (_created)
            {
                // Alvo gerado pela fixture, validado antes do único DROP CASCADE.
                if (!System.Text.RegularExpressions.Regex.IsMatch(Schema, "^gps23_[0-9a-f]{32}$"))
                    throw new InvalidOperationException("Schema de cleanup inválido.");
                await using var drop = _admin.CreateCommand($"DROP SCHEMA \"{Schema}\" CASCADE");
                await drop.ExecuteNonQueryAsync();
                _created = false;
                await using var check = _admin.CreateCommand("SELECT count(*) FROM pg_namespace WHERE nspname=@schema");
                check.Parameters.AddWithValue("schema", Schema);
                if ((long)(await check.ExecuteScalarAsync())! != 0)
                    throw new InvalidOperationException("Cleanup do schema PostGIS não foi concluído.");
            }
        }
        finally { await _admin.DisposeAsync(); _admin = null; }
    }
}
