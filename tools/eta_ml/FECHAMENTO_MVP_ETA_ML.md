# Fechamento do MVP ETA/ML — candidato isolado para o TCC

2026-10-08. **Decisão: PRONTO COM RESTRIÇÕES**, para implantação isolada do serviço histórico sem modelo. Inferência real ainda bloqueada por evidência/volume/modelo; Shadow e certificação automática continuam OFF. Nenhum deploy foi executado. Fencing, gateway, roles/ACL, novos contratos de evidência e snapshot v3 ficam congelados fora deste MVP.

## Estado efetivo e componentes prontos

| Componente | Estado |
|---|---|
| Serviço `ml/eta_history_service.py` | App independente do `server.py` legado e do ETA público |
| `/health` | Liveness leve, 200 mesmo sem modelo |
| `/ready` | 503 sem artefato real qualificado; 200 somente após carregamento compatível |
| `/eta/batch` | Lote vazio retorna []; lote não vazio sem modelo retorna 503; teto 200 itens/256 KiB |
| Modelo | Nenhum modelo histórico real qualificado disponível; sintéticos recusados por padrão |
| Treino/avaliação | Pipeline existente ExtraTrees e baselines, somente offline; splits/labels/features/gates preservados |
| Ciclo offline | Novo comando único registra bloqueio/insuficiência ou candidata imutável, sem promoção |
| Shadow histórico | Independente, limitado e OFF por padrão; cliente público/ML:BASE_URL intocados |
| Certificação 3G | OFF; sem autorização de perfil/epoch, sem implementação de fencing nesta tarefa |

O snapshot exploratório e as 758 associações técnicas não são dataset certificado. A revisão 3G.2 da distância operacional não certifica ausência de proteção nem cria labels físicos melhores. Nenhum resultado exploratório ou sintético foi apresentado como desempenho real.

## Risco de implantação do backend

`NoPonto/Program.cs:637` executa `db.Database.Migrate()` incondicionalmente no startup. O working tree contém `20261008090000_EtaTripEvidenceFoundation` e hooks experimentais 3G, além das mudanças anteriores. Portanto **não implantar o backend deste working tree neste fechamento**, mesmo com flags OFF: OFF não impede migration automática. Não se alterou bootstrap, migration, runtime ou permissões para contornar isso.

Release permitida pelo plano: somente imagem `Dockerfile.eta-history` do repositório ML. Não executar Dockerfile/docker-compose legado, compose da API, `docker compose down`, migrations, bootstrap Redis ou API completa. Backend atualmente implantado permanece independente e sem alteração. Um futuro release de backend exige seleção/revisão própria das mudanças e política explícita de migration.

## Alterações mínimas desta tarefa

- ML: novo `tools/eta_ml/offline_cycle.py`, novo `tests/test_offline_cycle.py`, ampliação de `tests/test_eta_history_service.py` e referência no `README_ETA_3C.md`.
- Backend: este relatório e HISTORICO somente por acréscimo.
- Nenhuma correção funcional de serviço/Docker/Shadow foi necessária: seus guards já atendem à release sem modelo. Nenhum gate foi reduzido; `qualify_real.py`, modelo, pipeline, SQL, perfis e contratos permanecem.

`offline_cycle.py` reutiliza `preflight` e `atomic_run` existentes. Treino existente já avalia modelo e dois baselines em TRAIN/VALIDATION/TEST; não cria outro avaliador. Volume insuficiente com demais gates aprovados produz `skipped-insufficient-volume` (exit 0), sem fit. Perfil/evidência/qualidade insuficientes produzem `blocked` (exit 2) com relatório. Gates aprovados geram `candidate-not-promoted`, hashes e métricas em diretório novo. Falha de treino não publica candidata; saída existente é recusada. Publicação usa staging próprio e rename no mesmo filesystem; índice `release.json` contém SHA-256 dos arquivos, sem caminhos absolutos ou timestamp variável. Imutabilidade significa no-overwrite mais conferência de hashes, não proteção contra administrador adulterando arquivos.

## Ciclo futuro de dados e retreinamento

1. Operador recebe snapshot local autorizado e verifica bundle/hashes pelo fluxo 3B existente. Snapshot/exportação continuam no backend; o ciclo ML não abre banco nem extrai dados.
2. Exporter local READ ONLY gera CSV, dataset.manifest, audit/options/SQL com hashes. Produzir config com limites reais de splits; datas do exemplo não representam uma coleta já concluída. Perfil completo permitido deve coincidir entre config/manifest/artefato.
3. Preparar pacote de evidências genuínas de viagem inteira. A certificação automática OFF não é pré-requisito para subir o serviço, mas também não autoriza aprovar dados. A via existente `external-controlled-session` exige auditor responsável, cobertura inteira e evidências reais hashadas; não substituir por booleans sem prova. A via durable ledger continua bloqueada pela política instalada OFF.
4. No PC/runner offline, executar o comando abaixo. Gates canônicos: pelo menos 10.000 linhas, 14 dias, 200/50/50 viagens TRAIN/VALIDATION/TEST, alguma linha com 30/10/10 viagens e oito horas TRAIN, além de identidade/label/split/perfil/evidência/política 3G.2 válidos.
5. Conservar `result/inputs`, `qualification.json`, `model/model.pkl`, `model/model.manifest.json`, `model/metrics.json` e `release.json`. Conservar também o pacote original de evidências externo: o recibo guarda seu hash, não uma cópia de todos os seus anexos. Comparar métricas, coverage e baselines com candidatas anteriores; não comparar releases com datasets/splits diferentes como ensaio controlado.
6. Aprovação humana separada seleciona a candidata para homologação/Shadow futuro. Não promove ao ETA público. Agendamento futuro pode chamar esse comando semanalmente em PC/runner externo com run ID novo, nunca no homeserver ou container de inferência. Nenhum scheduler foi instalado.

```powershell
Set-Location D:\repositorio_github\NoPonto\ml
$env:PYTHONPATH='tools/eta_ml'
$InputDirectory='D:\eta-ml\qualified-input' # local autorizado, não diretório de diagnóstico
$Run=[Guid]::NewGuid().ToString('N')
python tools/eta_ml/offline_cycle.py `
  --dataset "$InputDirectory\dataset.csv" `
  --manifest "$InputDirectory\dataset.manifest.json" `
  --config "$InputDirectory\config.json" `
  --evidence "$InputDirectory\evidence.json" `
  --output "D:\eta-ml\releases\$Run"
# Ler status/qualification; exit 0 pode significar SKIPPED, não modelo criado.
```

Não precisa alterar código para futuras datas/splits: fornecer config válida nova. Nova release de backend/coleta fora do catálogo oficial exige revisão coordenada do perfil permitido; não aceitar combinações arbitrárias para evitar trabalho manual.

## Instalação/atualização de modelo

Serviço usa `ETA_MODEL` e `ETA_MODEL_MANIFEST` (defaults `/models/model.pkl` e `/models/model.manifest.json`). Montar **somente o diretório da versão aprovada**, read-only. Não sobrescrever arquivos em uso: criar diretório novo e substituir apenas o container dedicado, com imagem/volume explicitamente escolhidos. O modelo fica em cache no processo; trocar arquivos no mount sem reiniciar não troca uma versão já carregada.

Antes de instalar: verificar todos os hashes do índice, qualificação ready, data_kind real, perfil/política/versões das bibliotecas, métricas e provenance. Modelo pickle deve ser de origem local confiável: SHA-256 não autentica autoria. Guard atual verifica hash, contrato/features, sklearn, volume/recibo/evidências/procedência antes de unpickle; recusa real sem qualificação e sintético por padrão. **Nunca configurar `ETA_ALLOW_SYNTHETIC=1` na release instalada.**

Conferência local do índice de uma candidata (não instala nem carrega pickle):

```powershell
$Candidate='D:\eta-ml\releases\RUN_APROVADO\result'
@'
import hashlib,json,sys
from pathlib import Path
root=Path(sys.argv[1]).resolve()
index=json.loads((root/'release.json').read_text())
assert index['status']=='candidate-not-promoted' and index['training_executed'] is True
for name,digest in index['files'].items():
    path=(root/name).resolve()
    assert path.is_relative_to(root) and path.is_file()
    assert hashlib.sha256(path.read_bytes()).hexdigest()==digest,name
print('Hashes conferidos; ainda exige revisão/qualificação e homologação do modelo')
'@ | python - $Candidate
```

Homologar candidata em container novo exclusivo sem rede/portas públicas e exigir `/ready` 200 com digest/data_kind esperados, depois batch causal completo e headers/correlação. Nenhum modelo real está disponível para executar essa homologação hoje. Se readiness falhar, não instalar candidata; preservar diretório/container/imagem anterior. Rollback é remontar a versão anterior e recriar só o container dedicado; sem tocar dados operacionais.

## Homologação PowerShell

```powershell
Set-Location D:\repositorio_github\NoPonto\ml
$env:PYTHONPATH='tools/eta_ml'
python -m unittest discover -s tests -p test_offline_cycle.py -q
python -m unittest discover -s tests -p test_qualify_real.py -q
python -m unittest discover -s tests -p test_eta_history_service.py -q
python -m unittest discover -s tests -p test_inference_contract.py -q
python -m unittest discover -s tools/eta_ml -p test_pipeline.py -q
docker build -f Dockerfile.eta-history -t noponto-eta-history:mvp-local-20261008 .
git diff --check
Set-Location D:\repositorio_github\NoPonto\noponto-backend
dotnet build NoPonto/NoPonto.csproj --no-restore --verbosity minimal
dotnet test NoPonto/NoPonto.csproj --no-build --no-restore --filter 'FullyQualifiedName~HistoricalEtaShadowTests|FullyQualifiedName~GpsPollingFontesTests|FullyQualifiedName~GpsPollingCadenciaTests' --verbosity minimal
pwsh -NoProfile -File tools/eta_ml/smoke_shadow_ml.ps1 -Image noponto-eta-history:mvp-local-20261008
git diff --check
```

O smoke cria nome aleatório, network none, não monta modelo/dados, não inicia API e remove apenas o próprio container no finally. Health 200/healthy e ready 503 são o resultado esperado sem modelo, não falha do serviço.

## Plano exato de deploy isolado — NÃO executado

Checklist antes da autorização: imagem final/digest registrados; smoke aprovado; nenhum modelo/sintético/credencial na imagem; nome dedicado não utilizado; nenhum backend será recriado; 512 MiB livres dentro do orçamento existente; acesso somente administrativo; plano de rollback registrado. Não ligar Shadow ou certificação.

Após autorização separada, no PC salvar a imagem já homologada, gerar hash do transporte e transferir pelo procedimento manual autorizado do operador (nenhum SSH/transferência nesta tarefa):

```powershell
docker image inspect noponto-eta-history:mvp-local-20261008 --format '{{.Id}}'
docker save --output D:\eta-ml\eta-history-mvp.tar noponto-eta-history:mvp-local-20261008
Get-FileHash D:\eta-ml\eta-history-mvp.tar -Algorithm SHA256
```

No host de destino, manualmente, conferir hash do tar, carregar imagem e conferir identidade igual à homologada. Exemplo de comandos Docker para shell do host; usar o IMAGE_ID real registrado, sem rebuild no servidor:

```sh
sha256sum /CAMINHO_APROVADO/eta-history-mvp.tar
docker load --input /CAMINHO_APROVADO/eta-history-mvp.tar
# Deve falhar por inexistência antes da PRIMEIRA instalação; se existir, PARAR e inventariar.
docker inspect noponto-eta-history-mvp
docker run -d --name noponto-eta-history-mvp \
  --label noponto.component=eta-history-mvp \
  --network none --memory 512m --cpus 0.5 --pids-limit 128 \
  --read-only --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /tmp:rw,noexec,nosuid,size=16m \
  --log-driver json-file --log-opt max-size=5m --log-opt max-file=2 \
  --restart unless-stopped IMAGE_ID_HOMOLOGADO
docker exec noponto-eta-history-mvp python -c "import urllib.request; print(urllib.request.urlopen('http://127.0.0.1:5200/health').read().decode())"
docker inspect noponto-eta-history-mvp --format '{{.State.Health.Status}}'
```

Primeira instalação sem mount/modelo: `/ready` deve responder 503. Não publicar portas; network none impede acesso a serviços operacionais. Essa instalação mantém serviço preparado e administrativamente testável, mas não conecta Shadow. Integração futura requer aprovação separada de rede privada/cliente independente; não mudar ML:BASE_URL.

Atualização/rollback: validar label `noponto.component=eta-history-mvp`, guardar image ID e mount anterior via inspect, parar **somente** esse container, renomeá-lo para nome de rollback único e executar o mesmo `docker run` com imagem escolhida. Para modelo aprovado, acrescentar `--mount type=bind,source=/DIRETORIO_DA_VERSAO_APROVADA,target=/models,readonly`. Readiness deve passar para modelo real; se falhar, parar/renomear só a nova instância, devolver nome à anterior e `docker start` nela. Se a anterior era sem modelo, readiness 503 continua esperado. Não apagar volumes, versões de modelos, containers alheios ou bancos. Sem `down -v`, prune ou limpeza global.

## Recursos e limites

Um worker, concurrency 2, BLAS/OpenMP threads 1; 512 MiB/0,5 CPU/128 PIDs; tmpfs 16 MiB e modelo read-only até 64 MiB. Compose histórico existente também prepara limites e mount read-only, mas o plano isolado usa imagem pronta e network none, sem build/rede no servidor. Logs da instalação futura têm rotação 2×5 MiB. Sem banco, Redis, trainer, scheduler, Prometheus/Grafana ou novo serviço de autoridade.

Smoke sem modelo não mede RSS/latência com ExtraTrees real nem performance sustentada. Artefato de 64 MiB não garante caber em 512 MiB; exigir benchmark antes de servir uma candidata. Não ampliar timeout de inferência para ocultar custo. Distância/versionamento/HTTP Shadow permanecem como homologados anteriormente, sem ativação.

## Bloqueios A/B/C e critério de encerramento

| Classe | Item | Decisão |
|---|---|---|
| A | Backend atual executa migration automática e inclui 3G experimental | Não implantar backend; não impede container ML isolado |
| A | Deploy sem autorização/recursos livres/identidade da imagem | Gate manual de instalação; nenhuma execução autorizada aqui |
| B | Evidência de qualidade inteira e volume suficientes ausentes | Serviço pode subir; treino/inferência reais permanecem bloqueados |
| B | Nenhum modelo real qualificado/readiness real | 503 esperado; não substituir por sintético |
| B | Carga/memória com modelo real não medidas | Homologar quando existir artefato, antes de uso |
| B | Shadow conectado não autorizado | OFF; etapa separada de habilitação após aprovação |
| C | Fencing/certificação automática/snapshot v3/scheduler/dashboard | Congelados pós-TCC, não prolongar MVP |
| C | Catálogo de novos perfis e retirada de cópias legadas | Evolução coordenada futura, sem remoção agora |

MVP encerrado quando: imagem isolada homologada e identificada; health/readiness corretos sem modelo; guards de modelo real/sintético preservados; comando offline reproduzível com skip/bloqueio/no-overwrite/métricas; checklist e rollback documentados; ETA público, Shadow OFF e certificação OFF preservados. Esse marco não é entrega de precisão real nem habilitação pública. Depois disso aguardar dados/evidência e autorização de implantação, sem abrir nova arquitetura automaticamente.

## Validação executada neste fechamento

- **37 testes Python relevantes aprovados**: ciclo offline 6, qualificação 8, serviço 7, contrato leve 3, pipeline 13. Os positivos do ciclo usam trainer fake em fixture explicitamente identificada; os 13 do pipeline treinam somente sintéticos de teste. Nenhum treino real/certificação executado.
- **45 testes C# aprovados**, zero falhas/ignorados: Shadow histórico, fontes e cadência do polling. Build incremental backend aprovado, 0 erros/warnings, ~3,74s. API não iniciada; nenhuma nova auditoria conectada de certificação.
- Docker build aprovado. Base efetivamente resolvida `python:3.11-slim@sha256:0dd364ba7e10242f07755449e3a3d0e35f9efd987952737b90def6709ab0c5ce`; dependências do projeto preservadas. Tag final `noponto-eta-history:mvp-local-20261008`, identidade local homologada `sha256:6a1796b8bacd5fb1a4b484139be83162cba3d75ffc8091578f50355354de8c53`. Não é promessa de rebuild byte-idêntico com tag de base flutuante: transportar esta imagem e conferir hash/identidade.
- Smoke final em container exclusivo/network none: startup observado **2,048s**, health 200/healthy, ready 503, batch vazio 200[], batch completo 503 sem modelo. Amostra docker stats **32,98 MiB / 512 MiB**, 0,20% CPU e 2 PIDs. Primeira imagem anterior sem o comando offline: startup4,726s/47,08MiB; não representa pico nem cold start controlado de hardware. Nenhuma previsão/modelo real/sintético carregado; ambos os containers próprios removidos.
- CLI novo executado com caminhos futuros inexistentes: status blocked, training_executed=false, automatic_promotion=false, qualification/release hashados gravados em diretório novo `ml/outputs/mvp-preflight-*`. Não foi fabricado CSV para destravar gates.
- `git diff --check` nos dois repos e whitespace dos arquivos novos conferidos. Working trees anteriores preservados; nenhum commit/push/deploy, produção, SSH, banco operacional, migration ou ativação.

Não testados nesta tarefa: modelo real, benchmark sob carga real, promoção/Shadow conectado, instalação no homeserver, fencing/certificação automática ou extração de novos dados. São restrições explícitas, não motivo para iniciar outra frente dentro do fechamento.
