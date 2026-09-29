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
    public Guid Linha1 { get; } = Guid.NewGuid();
    public Guid Sentido1 { get; } = Guid.NewGuid();
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
    public Guid LimiteFracao { get; } = Guid.NewGuid();
    public Guid Unpublished { get; } = Guid.NewGuid();
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
                    CREATE TABLE "{Schema}"."PadroesOperacionais" ("Id" uuid PRIMARY KEY, "SentidoId" uuid NOT NULL, "VersaoAtualId" uuid);
                    CREATE TABLE "{Schema}"."PadroesVersoes" ("Id" uuid PRIMARY KEY, "PadraoOperacionalId" uuid NOT NULL,
                        "Geometria" geometry(LineString,4326) NOT NULL, "Topologia" text NOT NULL DEFAULT 'LINEAR');
                    CREATE TABLE "{Schema}"."Paradas" ("Id" uuid PRIMARY KEY, "Nome" text, "Localizacao" geometry(Point,4326));
                    CREATE TABLE "{Schema}"."OcorrenciasParadasPadroes" (
                        "Id" uuid PRIMARY KEY, "ParadaId" uuid, "PadraoVersaoId" uuid,
                        "Ordem" integer, "PosicaoTracado" double precision,
                        "DistanciaAcumuladaMetros" double precision NOT NULL DEFAULT 0,
                        "DistanciaDaLinhaMetros" double precision NOT NULL DEFAULT 0
                    );
                    """;
                await ddl.ExecuteNonQueryAsync();
            }
            builder.SearchPath = $"{Schema},public";
            DataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            var linha1 = Linha1;
            var linha2 = Guid.NewGuid();
            var sentido1 = Sentido1;
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
            var linhaLimite = Guid.NewGuid();
            var sentidoLimite = Guid.NewGuid();
            await using var seed = DataSource.CreateCommand("""
                INSERT INTO "Linhas" VALUES (@l1,'GPS23'),(@l2,'OUTRA23'),(@lx,'X25'),(@lp,'P25'),
                    (@le,'EMPATE'),(@ls,'SCORE'),(@lc,'CIRCULAR'),(@ll,'EDGE165');
                INSERT INTO "Sentidos" VALUES (@s1,@l1),(@s2,@l2),(@sx,@lx),(@sp,@lp),
                    (@se,@le),(@ss,@ls),(@sc,@lc),(@sl,@ll);
                INSERT INTO "PadroesOperacionais" ("Id","SentidoId","VersaoAtualId") VALUES
                  (@r1,@s1,@r1),(@r2,@s1,@r2),(@volta,@s1,@volta),(@outra,@s2,@outra),
                  (@diag,@s1,@diag),(@x,@sx,@x),(@p,@sp,@p),(@eb,@se,@eb),(@ea,@se,@ea),
                  (@su,@ss,@su),(@sm,@ss,@sm),(@circ,@sc,@circ),(@limite,@sl,@limite);
                INSERT INTO "PadroesVersoes" ("Id","PadraoOperacionalId","Geometria","Topologia") VALUES
                  (@r1,@r1,ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326),'LINEAR'),
                  (@r2,@r2,ST_GeomFromText('LINESTRING(-43.21 -22.8998,-43.19 -22.8998)',4326),'LINEAR'),
                  (@volta,@volta,ST_GeomFromText('LINESTRING(-43.19 -22.9001,-43.21 -22.9001)',4326),'LINEAR'),
                  (@outra,@outra,ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326),'LINEAR'),
                  (@diag,@diag,ST_GeomFromText('LINESTRING(-43.201 -22.901,-43.199 -22.899)',4326),'LINEAR'),
                  (@x,@x,ST_GeomFromText('LINESTRING(-0.01 -0.01,0.01 0.01,-0.01 0.01,0.01 -0.01)',4326),'LINEAR'),
                  (@p,@p,ST_GeomFromText('LINESTRING(-0.01 0,0.01 0,0.01 0.005,-0.01 0.005,-0.01 0.00002,0.01 0.00002)',4326),'LINEAR'),
                  (@eb,@eb,ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326),'LINEAR'),
                  (@ea,@ea,ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326),'LINEAR'),
                  (@su,@su,ST_GeomFromText('LINESTRING(-43.21 -22.8998,-43.19 -22.8998)',4326),'LINEAR'),
                  (@sm,@sm,ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326),'LINEAR'),
                  (@circ,@circ,ST_GeomFromText('LINESTRING(0 0,0.01 0,0.01 0.01,0 0.01,0 0)',4326),'CIRCULAR');
                INSERT INTO "PadroesVersoes" ("Id","PadraoOperacionalId","Geometria","Topologia") VALUES
                  (@limite,@limite,ST_GeomFromText('LINESTRING(-43.18571 -22.93105,-43.18617 -22.93049,-43.19312 -22.92137,-43.19385 -22.92046,-43.19416 -22.92002,-43.1943 -22.91974,-43.19439 -22.91951,-43.19444 -22.91931,-43.1945 -22.91892,-43.19451 -22.91874,-43.19445 -22.91813,-43.1942 -22.91696,-43.19415 -22.91654,-43.19415 -22.91632,-43.19419 -22.91606,-43.19431 -22.91565,-43.19591 -22.91143,-43.19678 -22.90901,-43.19771 -22.90659,-43.19771 -22.90636,-43.19767 -22.90613,-43.19759 -22.90599,-43.1975 -22.90589,-43.19728 -22.90577,-43.19713 -22.90573,-43.19701 -22.90571,-43.19677 -22.90574,-43.19665 -22.9058,-43.19656 -22.90588,-43.19651 -22.90598,-43.19649 -22.9061,-43.1965 -22.90621,-43.19655 -22.90634,-43.19662 -22.90643,-43.19671 -22.90649,-43.19799 -22.90708,-43.20087 -22.90801,-43.20524 -22.90942,-43.20557 -22.90947,-43.20591 -22.90957,-43.20611 -22.90964,-43.20638 -22.90976,-43.20638 -22.90977,-43.20686 -22.90995,-43.20743 -22.9101,-43.20758 -22.91012,-43.20785 -22.9101,-43.20893 -22.90988,-43.20903 -22.90983,-43.20913 -22.90973,-43.20922 -22.90956,-43.20929 -22.90922,-43.20933 -22.90801,-43.20936 -22.90758,-43.20936 -22.907574,-43.2094 -22.90705,-43.20958 -22.90424,-43.20958 -22.904234,-43.20981 -22.90063,-43.20986 -22.90003,-43.210325 -22.90004,-43.21033 -22.90004,-43.21042 -22.90005,-43.21053 -22.90011,-43.21059 -22.90014,-43.21068 -22.90013,-43.2107 -22.90011,-43.2107 -22.90011)',4326),'LINEAR');
                INSERT INTO "PadroesOperacionais" VALUES (@up_po,@s1,NULL);
                INSERT INTO "PadroesVersoes" VALUES (@up,@up_po,
                    ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326),'LINEAR');
                INSERT INTO "Paradas" VALUES
                  (@parada,'Parada controlada',ST_SetSRID(ST_MakePoint(-43.195,-22.9),4326)),
                  (@parada_r2,'Parada paralela',ST_SetSRID(ST_MakePoint(-43.195,-22.8998),4326));
                INSERT INTO "OcorrenciasParadasPadroes"
                    ("Id","ParadaId","PadraoVersaoId","Ordem","PosicaoTracado","DistanciaAcumuladaMetros","DistanciaDaLinhaMetros")
                    VALUES (gen_random_uuid(),@parada,@r1,1,0.75,750,0),
                           (gen_random_uuid(),@parada_r2,@r2,1,0.75,750,0);
                """);
            foreach (var pair in new (string, Guid)[] { ("l1",linha1),("l2",linha2),("s1",sentido1),("s2",sentido2),
                ("r1",R1),("r2",R2),("volta",Volta),("outra",OutraLinha),("diag",Diagonal),
                ("parada",Guid.NewGuid()),("parada_r2",Guid.NewGuid()),
                ("lx",linhaX),("sx",sentidoX),("lp",linhaP),("sp",sentidoP),("x",X),("p",ParalelasMesmaLinha),
                ("le",linhaEmpate),("se",sentidoEmpate),("ea",EmpateA),("eb",EmpateB),
                ("ls",linhaScore),("ss",sentidoScore),("su",ScoreUuidMenor),("sm",ScoreMelhor),
                ("lc",linhaCircular),("sc",sentidoCircular),("circ",Circular),
                ("ll",linhaLimite),("sl",sentidoLimite),("limite",LimiteFracao),
                ("up",Unpublished),("up_po",Guid.NewGuid()) })
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
