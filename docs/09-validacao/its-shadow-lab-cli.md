# CLI de laboratório ITS × GTFS-RT bruto

Branch de trabalho `feat/its-shadow-lab-cli`, baseada em
`b35c18b1427a39f34d438b6487d8c4c1e586d83b` (`feat/its-shadow-probe`).

A ferramenta em `tools/ItsShadowLab` executa um laboratório separado do processo
da API. Não cria host ASP.NET, não executa o entrypoint de `NoPonto/Program.cs`,
não registra hosted services, não lê `.env`/appsettings e não abre conexões
Redis/PostgreSQL. Só faz GET nos endpoints públicos ITS e GTFS-RT BUS e cria o
arquivo JSONL solicitado. Nenhum deploy, merge ou execução no homeserver faz
parte desta etapa.

## Build e pré-requisitos

Requer SDK .NET 9 para compilar. A referência ao backend segue o padrão do
exportador existente `EtaMl.Export`: importar sua assembly não executa seu
entrypoint. O artefato mantém dependências transitivas do backend, incluindo
`Microsoft.AspNetCore.App`; para executá-lo com `dotnet`, é necessário também o
runtime ASP.NET Core 9, presente na instalação completa do SDK, para o build
framework-dependent normal. A publicação Linux self-contained abaixo inclui os
runtimes e dispensa essa instalação na máquina de destino. Isso não implica
inicializar um servidor. A referência EF Relational 9.0.10 alinha a resolução
transitiva à versão usada pelo backend, como no exportador existente. Não há
nova biblioteca de CLI ou outro cliente ITS.

A primeira tentativa local de `dotnet build NoPonto.sln --no-restore -c Release`
foi rejeitada antes da execução pela revisão automática. Posteriormente, o
usuário executou esse comando no PowerShell e forneceu sucesso dos três projetos
em 2,9s. Esse resultado confirma o estado anterior ao acréscimo de frescor na
conclusão de cada fonte. O build completo após esse acréscimo será executado
separadamente pelo usuário; os testes abaixo compilam os projetos envolvidos,
mas não substituem a execução desse comando.

No Debian, em um checkout de desenvolvimento com SDK 9 já instalado:

```bash
cd /caminho/para/noponto-backend
dotnet --list-sdks
dotnet --list-runtimes
dotnet restore NoPonto.sln
dotnet build NoPonto.sln --no-restore -c Release
dotnet tools/ItsShadowLab/bin/Release/net9.0/NoPonto.ItsShadowLab.dll --help
```

Esses comandos não instalam SDKs, alteram serviços ou acessam bancos. O restore
pode acessar NuGet. A repetição do build completo após as métricas de aquisição
permanece para o PowerShell do usuário, conforme o fechamento anterior.

## Publicação Linux x64 self-contained

No checkout de desenvolvimento, com SDK e pacotes de runtime disponíveis:

```bash
dotnet publish tools/ItsShadowLab/ItsShadowLab.csproj -c Release -r linux-x64 \
    -o tools/ItsShadowLab/bin/publish/linux-x64
```

O `ItsShadowLab.csproj` configura `SelfContained=true`, `PublishSingleFile=true`,
`PublishTrimmed=false` e `IncludeNativeLibrariesForSelfExtract=true` somente para
`RuntimeIdentifier=linux-x64`. O build normal e os testes Windows permanecem
framework-dependent. Não é necessário repetir essas propriedades via `-p:`.

A referência ao backend remove as propriedades globais `SelfContained`,
`PublishSelfContained`, `PublishSingleFile`, `PublishTrimmed` e
`IncludeNativeLibrariesForSelfExtract` nesse RID. O SDK já remove o RID de
referências agnósticas como o backend, que também contém testes. Deixar
`PublishSingleFile=true` global chegar à referência faz o SDK inferir
`PublishSelfContained=true` e depois `SelfContained=true` sem RID, causando
NETSDK1191. O isolamento impede essa reinferência sem alterar `NoPonto.csproj`.

No Debian x64, depois de copiar a pasta publicada para um diretório local:

```bash
cd /caminho/local/its-shadow-lab
chmod +x NoPonto.ItsShadowLab
./NoPonto.ItsShadowLab --help
# Para coleta, use os mesmos argumentos documentados abaixo, incluindo os route_ids.
```

Esse executável inclui os runtimes .NET e ASP.NET Core; self-contained não
elimina dependências nativas do sistema operacional. Bibliotecas nativas do
bundle são extraídas ao executar e precisam de diretório gravável. Não houve
instalação nem execução no Debian/homeserver nesta validação.

Validação em Windows com SDK9.0.306: publicação com as flags originais e
publicação simplificada acima concluídas. Artefato `NoPonto.ItsShadowLab` com
122.781.375bytes, cabeçalho ELF64/x86-64, reconhecido por
`Microsoft.NET.HostModel.AppHost.HostWriter.IsBundle`; runtimeconfig de origem
contém `includedFrameworks` para .NET/ASP.NETCore9.0.10, sem dependência de
framework instalado. Binlog confirmou backend com `SelfContained=false` e
remoção das propriedades globais. Os logs e binários ficam em diretório de
laboratório ignorado pelo Git.

Testes após a publicação: CLI50/50 aprovados no build Windows normal, incluindo
recompilação dos três projetos; apphost Windows PE e `--help` verificados.
Offline oficial teve inicialmente969/970, com o timeout já registrado em
`HistoricalEtaGtfsPollingTests.PollingContinuesWithIndependentShadow`
(BUS/sem batch/export_failure, espera3s). Sem alterar teste ou prazo, repetição
970/970 e verificador oficial aprovados. Ambos os TRXs foram preservados;
intermitência continua registrada, sem causa comprovada nesta correção.

A saída também preserva conteúdos transitivos existentes do backend: JSONs de
configuração/trem, horários, XML/PDBs e launchers auxiliares Windows
`NoPonto.exe`/`testhost.exe`. Eles não são o entrypoint Linux e não são executados
pela CLI. O comando de execução é sempre `./NoPonto.ItsShadowLab`; não houve
mudança de conteúdo/configuração produtiva ou filtragem adicional dos arquivos
do projeto referenciado nesta correção.

## Associar a linha ao route_id sem banco

**Não presumir que o `route_id` do GTFS-RT seja `838`.** A seleção é explícita
por `--gtfs-route-id`, repetível. Sem pelo menos um ID, a ferramenta rejeita a
execução antes de fazer HTTP. Use um GTFS estático local correspondente ao feed
em avaliação e confirme os IDs em `routes.txt`; não há consulta ao crosswalk SQL.
Um mapeamento incorreto pode produzir amostra vazia da seleção errada.

Exemplo Bash com Python 3 padrão, para extrair os IDs da linha 838 de um ZIP
local. São IDs de **rotas**, sem identificadores de veículos:

```bash
gtfs_zip=/caminho/para/gtfs-estatico.zip
mapfile -t lab_route_ids < <(python3 - "$gtfs_zip" 838 <<'PY'
import csv, io, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    with archive.open('routes.txt') as source:
        routes = csv.DictReader(io.TextIOWrapper(source, encoding='utf-8-sig', newline=''))
        ids = sorted({r['route_id'] for r in routes
                      if r['route_short_name'].strip().upper() == sys.argv[2].upper()})
        if not ids:
            raise SystemExit('Linha ausente no routes.txt local.')
        for route_id in ids:
            print(route_id)
PY
)
test "${#lab_route_ids[@]}" -gt 0 || { echo 'Mapeamento local obrigatório.' >&2; exit 2; }
lab_route_args=()
for lab_route_id in "${lab_route_ids[@]}"; do
    lab_route_args+=(--gtfs-route-id "$lab_route_id")
done
```

Confirme que as rotas selecionadas correspondem ao sistema SPPO/ônibus e à
versão estática utilizada pelo GTFS-RT. A ferramenta não certifica automaticamente
essa correspondência e não infere uma rota por prefixo ou número comercial.

## Executar o laboratório no Debian

Depois do build e da definição de `lab_route_args` acima:

```bash
mkdir -p /tmp/noponto-its-lab
lab_output="/tmp/noponto-its-lab/linha838-$(date -u +%Y%m%dT%H%M%SZ).jsonl"
dotnet tools/ItsShadowLab/bin/Release/net9.0/NoPonto.ItsShadowLab.dll \
    --line 838 --cycles 10 --interval 60 \
    --its-timeout 15 --gtfs-timeout 15 \
    --windows 3 --max-pages 6 --page-limit 5000 --max-records 30000 \
    --spatial-seconds 10 --output "$lab_output" "${lab_route_args[@]}"
```

Para uma única observação, use `--cycles 1`. Para informar IDs manualmente,
substitua a expansão do array por `--gtfs-route-id ID_REAL_DO_ROUTES_TXT`, repetindo
a opção quando necessário. A ferramenta sempre exige um arquivo novo, não faz
append nem sobrescreve evidências existentes. O diretório pai deve existir.

Ctrl+C e SIGTERM cancelam as duas aquisições. Linhas já concluídas são mantidas;
um ciclo interrompido não gera uma linha parcial por cancelamento. Não há fsync
ou garantia transacional contra falha de disco/queda de energia. Nenhum corpo de
resposta, ID, hash de ID, coordenada ou trajetória individual é salvo.

Códigos de saída: `0` ciclos concluídos; `2` argumentos inválidos; `1` falha de
execução/arquivo; `130` cancelamento. Um ciclo com fontes indisponíveis é uma
observação válida do laboratório e pode terminar com código `0`: examine os
campos de disponibilidade no JSONL, não apenas o código do processo.

## Limites, aquisição e compatibilidade

| Parâmetro | Padrão | Limite |
|---|---:|---|
| `--line` | 838 | Até 20 caracteres ASCII alfanuméricos/hífen |
| `--gtfs-route-id` | Obrigatório | 1–32 IDs distintos; até 128 caracteres por ID |
| `--cycles` | 10 | 1–1440 |
| `--interval` | 60s | 60–3600s, após terminar o ciclo |
| `--its-timeout` | 15s | 1–30s |
| `--gtfs-timeout` | 15s | 1–30s |
| `--windows` | 3 | 1–3 minutos UTC |
| `--max-pages` | 6 | 1–10, global entre janelas |
| `--page-limit` | 5000 | 1–5000 |
| `--max-records` | 30000 | 1–50000 registros ITS retidos |
| `--spatial-seconds` | 10s | 0–60s |

Os GETs ITS e GTFS-RT começam no mesmo ciclo e possuem prazos independentes.
O ciclo aguarda ambas as tentativas, normalmente até o maior dos dois prazos,
sem cancelar a fonte saudável quando a outra falha. O intervalo é após a
conclusão; não há rajadas para recuperar ciclos atrasados.

ITS reutiliza `GpsDatarioClient`, seu parser/DTO, a paginação limitada do probe,
controle de cursor, corpo máximo de 8 MiB, intervalo mínimo de 1s e `Retry-After`.
O novo caminho `CaptureAsync` pula completamente a leitura do runtime; registros
existem somente em memória e são `[JsonIgnore]` na captura. O caminho existente
do worker continua chamando `RunAsync` com a política anterior.

GTFS-RT reutiliza `GtfsRealtimeGpsClient`, protobuf, validação estrutural,
coordenadas, deduplicação e proteção de conflitos. São preservados limites de
2.000.000 bytes/5.000 entidades, cadência mínima do cliente de 30s, cache e
validação HTTP condicional. `Reused=true` identifica reutilização de snapshot,
inclusive cache local/304. A idade continua calculada pelo GPS físico, sem
renovar timestamps ao reutilizar um corpo.

Um construtor interno exclusivo do diagnóstico usa o mesmo parser **sem o corte
operacional de idade/futuro**, permitindo medir dados antigos e contar futuros.
O construtor público e `Parse` operacionais mantêm seus defaults e regras; os
testes existentes continuam exigindo o corte operacional. O laboratório elimina
futuros das métricas válidas e os conta separadamente.

Timeouts limitam a espera mesmo se uma implementação de I/O ignorar o token.
Enquanto um I/O anterior daquela fonte estiver pendente, ela informa
`PreviousIoPending` e não inicia outra chamada; a outra fonte continua podendo
ser medida. Falhas tardias são observadas sem imprimir seus detalhes. O cálculo
síncrono dos agregados fica depois da aquisição e tem volume limitado; não é uma
garantia de prazo rígido de CPU ou de cancelamento forçado de um I/O externo.

Ambos os handlers impedem redirects automáticos e cookies, não têm logs de
payload/requisição configurados e têm descarte explícito. Os limites são por
processo; múltiplas CLIs concorrentes não compartilham cotas.

## Interpretar JSONL

Cada linha é um `LabReport` com `SchemaVersion=1`, ciclo, linha, instantes UTC,
duração e agregados por fonte. O rótulo é
`ComparisonBasis=ITS_vs_raw_GTFSRT_before_operational_pipeline`; nenhuma posição
publicada/cache operacional participa desta comparação.

- Quantidades/frescor: veículos normalizados únicos, até 40/60s inclusive,
  mediana/p90 de idade, registros inválidos/futuros e agregados por fornecedor
  ITS. Deduplicação usa o maior **timestamp físico**, inclusive entre Zirix e
  Conecta; `datetime_servidor` apenas seleciona os minutos ITS. Mesmo timestamp
  entre fornecedores usa o empate alfabético já documentado no probe.
- Idade calculada no instante comum `ObservedAtUtc`, depois de obter as duas
  amostras. GETs e snapshots não são atômicos. GTFS-RT tem resolução de segundos;
  a ITS pode ter frações. Igualdade temporal é exata, e diferenças menores que um
  segundo não bastam para decidir qualidade de uma fonte.
- Cada fonte também inclui `AtAcquisitionCompletion`: `ObservedAtUtc` próprio,
  veículos válidos, recentes40/60s, mediana/p90 de idade e inválidos/futuros nesse
  instante. Os campos existentes continuam usando o instante comum; comparação,
  cobertura e concordância espacial não mudam. A ITS reutiliza os agregados já
  calculados ao concluir sua captura; GTFS registra o retorno da sua própria
  aquisição, antes de aguardar a ITS. Em cache/304, esse instante é o retorno no
  ciclo atual, preservando o timestamp físico original. Não é o instante em que
  o corpo foi originalmente recebido nem uma nova leitura física do GPS.
  Sem amostra utilizável, o objeto é null; amostra válida vazia tem contagens0
  e idades null. Amostras parciais utilizáveis mantêm esse objeto e os sinais de
  incompletude da fonte. Um timestamp futuro na conclusão pode tornar-se válido
  no instante comum, portanto as populações podem diferir. Para populações
  iguais, a diferença de idades explicita a espera pela outra fonte; a duração
  de aquisição continua em `AcquisitionMs`. Isso não separa latência de rede,
  paginação, processamento do fornecedor e atraso físico do GPS.
- `State`: `Available`, `Empty`, `Partial` ou `Unavailable`. Ausência/erro gera
  contadores indisponíveis **null**. `Empty` significa resposta válida sem
  observações na seleção, não prova que a linha não opera. Dados físicos apenas
  inválidos/futuros não são promovidos a uma amostra vazia comparável.
- `Incomplete` também sinaliza dados rejeitados/futuros, além de pending,
  limites e erros. `ProviderStatuses` preserva estados observados e reduz valores
  externos a categorias permitidas. `ok` não certifica cobertura integral.
- `Both`, `ItsOnly`, `GtfsOnly`, `ObservedUnion` e participações relativas medem
  a união dos veículos válidos **observados** nos dois feeds. O rótulo é
  `CoverageBasis=relative_to_observed_union_not_total_fleet`; não é percentual da
  frota real. Parcialidade exige leitura conjunta com `Incomplete`.
- Duas amostras brutas válidas vazias são comparáveis com contagem zero, mas a
  participação relativa é null porque o denominador é zero. Isso difere da
  ausência de posições no cache, que no worker anterior era não comparável.
- ITS mais recente/igual/antiga e delta assinado mediano (ITS menos GTFS-RT).
  Distância Haversine mediana/p90 somente para pares com diferença absoluta de
  timestamps dentro de `--spatial-seconds`; sem pares, distância null.
- `Records` ITS é a quantidade retida antes da validação/deduplicação. GTFS é a
  quantidade selecionada após o parser bruto/deduplicação e antes de excluir
  futuros. `RejectedEntitiesInWholeFeed` pertence ao feed GTFS inteiro, não é
  atribuível só à linha solicitada. `Pages` é da aquisição ITS.

Exemplo sintético **abreviado** de uma linha (as linhas reais incluem todos os
campos agregados e nenhuma posição individual):

```json
{"SchemaVersion":1,"Cycle":1,"Line":"838","ComparisonBasis":"ITS_vs_raw_GTFSRT_before_operational_pipeline","CoverageBasis":"relative_to_observed_union_not_total_fleet","Its":{"State":"Available","Vehicles":18,"Recent60":15},"Gtfs":{"State":"Unavailable","Vehicles":null,"Recent60":null},"Comparison":{"Comparable":false,"Both":null,"ItsOnly":null,"GtfsOnly":null,"ObservedUnion":null}}
```

Inspeção local sem publicar dados externos:

```bash
python3 - "$lab_output" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as source:
    rows = [json.loads(line) for line in source if line.strip()]
print('ciclos:', len(rows))
print('comparáveis:', sum(r['Comparison']['Comparable'] for r in rows))
print('comparáveis sem sinais de incompletude:', sum(
    r['Comparison']['Comparable'] and not r['Its']['Incomplete'] and not r['Gtfs']['Incomplete']
    for r in rows))
PY
```

## Testes offline e limites da entrega

Novo projeto `tools/ItsShadowLab.Tests`, xUnit/VSTest alinhado às versões do
backend, registrado na solução raiz junto da CLI. Seus handlers HTTP e relógio
são falsos; testes de arquivos criam somente artefatos próprios no diretório
temporário e removem esses arquivos ao terminar. Nenhum teste conecta feeds ou
bancos reais.

```bash
dotnet test tools/ItsShadowLab.Tests/ItsShadowLab.Tests.csproj --no-restore -c Release \
    --logger 'trx;LogFileName=its-cli-tests.trx' \
    --results-directory NoPonto/lab-output/its-shadow-cli
dotnet test NoPonto.sln --no-build --no-restore -c Release \
    --filter 'FullyQualifiedName~NoPonto.ItsShadowLab.Tests.'

lab_offline_filter="$(python3 .ci/offline_tests.py filter)"
dotnet test NoPonto/NoPonto.csproj --no-build --no-restore -c Release \
    --filter "$lab_offline_filter" --logger 'trx;LogFileName=its-cli-backend-offline.trx' \
    --results-directory NoPonto/lab-output/its-shadow-cli
python3 .ci/offline_tests.py verify \
    NoPonto/lab-output/its-shadow-cli/its-cli-backend-offline.trx
```

Não executar a suíte completa do backend sem filtro: existem testes conectados.
O workflow existente não foi alterado; a CLI tem teste direto e descoberta pela
solução, mas o job atual de CI executa somente a allowlist da assembly backend.
As duas regressões adicionadas ao backend estão em classes já selecionadas.

Validação local desta etapa, SDK 9.0.306/Windows:

- CLI final: **50/50 aprovados**, zero falhas/ignorados; TRX parseado por resultado e
  classe (17 argumentos, 26 sessão/fontes, sete execução/JSONL). Inclui espera
  assimétrica nos dois sentidos, limites40/60s, cache/304, null/vazio/pending e
  transição futuro/presente nas duas bases temporais.
- Descoberta pela solução raiz anterior: **45/45** executados com filtro do
  namespace da CLI. A assembly backend tem zero testes desse namespace, conforme
  esperado; isso não representa execução da sua suíte inteira.
- Direcionados GPS/ITS/GTFS backend: **152/152 aprovados**, zero falhas/ignorados.
- Seleção oficial backend: **970/970 aprovados**, zero falhas/ignorados, incluindo
  as duas novas regressões; `offline_tests.py verify` aprovou classes/resultados.
  Na primeira rodada do fechamento, um caso existente
  `HistoricalEtaGtfsPollingTests.PollingContinuesWithIndependentShadow`
  (BRT/sem batch/export_failure) excedeu a espera de3s por resultado assíncrono:
  969/970 aprovados. Sem modificar código ou prazos desse teste, sua classe
  passou isoladamente52/52 e a repetição oficial passou970/970. A intermitência
  permanece como risco de teste; sua causa não foi comprovada nesta etapa.
- Os três projetos compilaram em Release via testes, com os cinco avisos
  preexistentes do backend e nenhum aviso novo. Binário CLI executado com
  `--help`, sem iniciar API ou abrir recursos externos.
- Restore dos novos projetos passou com dependências disponíveis. Build completo
  confirmado pelo usuário antes das novas métricas; repetição após elas fica
  para seu PowerShell, conforme solicitado.
- Uma rodada de 43 testes teve falha de fixture ao ler o JSONL ainda aberto no
  Windows. Corrigidos compartilhamento de leitura e teardown da fixture, sem
  retirar verificações de linha completa/intervalo; rodada final passou.
- O teste adicional de304 inicialmente falhou porque a fixture omitia o ETag
  exigido pelo cliente existente. A fixture foi corrigida com o validador
  correspondente, preservando a política operacional; rodada final passou.
- Nenhuma coleta real, execução no Debian, alteração de produção ou acesso a
  bancos reais foi realizada. Fechamento autorizado com commit/push somente da
  branch `feat/its-shadow-lab-cli`; sem merge, deploy, homeserver ou alteração
  da release#142. SHA e resultado do push são informados na entrega.

Próxima coleta deve variar horários/condições, validar o mapeamento estático,
acompanhar ciclos não comparáveis/parciais e analisar comportamento por fornecedor.
Este patch não executa coleta real, não certifica cobertura/frescor ITS ou
GTFS-RT, não decide fonte principal e não implementa seleção híbrida.
