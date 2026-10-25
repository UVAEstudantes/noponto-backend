# Preflight Debian da captura passiva — 24/10/2026

**GO para solicitar autorização separada da terceira janela. Nenhuma ITS foi ativada.** Prova operacional limitada concluída no Debian12/Python3.11.2 com os cinco arquivos canônicos do commit `3fcbce87f63fbee3d1d87ed360757e8f6e3d9e98`. Horários UTC25/10 02:12:42–02:16:24, equivalentes a24/10 23:12:42–23:16:24 UTC−03. Não foi uma janela híbrida nem baseline produtivo para comparar fontes.

## Produção antes e depois

Release151 e imagem `ghcr.io/guilhermedesales/noponto-api@sha256:66cfa6491074ed639c2bcfe29948c7e93c3d03775f3ad3f2eea95e144cce94f8`, persistência active.run/active.env consistente. BUS=GTFSRT_BUS, fallback vazio, FullFallback efetivo e trace=false; BRT=GTFSRT_BRT. Uma API escritora, mesmo ID e início22:38:01.659UTC de24/10; zero reinícios/OOM. Os oito containers mantiveram IDs, imagens, fingerprints de configuração, StartedAt, reinícios e saúde. Quatro Compose efetivos do deploy151 preservados; overrides históricos não removidos.

RedisPONG, PostgreSQL aceitando conexões e Docker healthy antes/depois. HTTP raiz saudável; SignalR24listas/uma conexão antes e24listas/uma conexão depois, zero erros/regressões/duplicatas/coordenadas inválidas em ambas as sondas de35s. São leituras limitadas, sem promessa de frescor por toda a rede. Lock livre antes/depois; nenhum outro supervisor/janela detectado.

API CPU amostrada4,56%→4,92%; memória237,1→233,8MiB/768MiB. Host RAM disponível2342296→2338088KiB; SwapFree4857080→4857336KiB (sem aumento de uso); disco livre~20,05GiB. Load1min0,49→0,13. PostgreSQL teve pico amostrado97,16% de uma CPU no final, sem perda de saúde ou reinício; não atribuir esse pico à captura nem tratá-lo como benchmark.

## Arquivos privados e integridade

Diretório exclusivo `/home/guilherme/.noponto-deploy/log-capture-proof-20261025T021126Z`,0700. Somente cinco scripts/overrides aprovados copiados; arquivos operacionais0600, hashes comparados no servidor com bytes de `git show 3fcbce8:path`, UTF-8/LF, sem depender do checkout Windows. State/provas/archives privados foram produzidos no próprio diretório. Os arquivos e evidências foram preservados, sem reutilizar state/PID antigos; uma futura janela usará outro diretório e outra prova.

| Arquivo | SHA256 |
|---|---|
| `its_window.py` | `d166f170e773c60f5024c895eb32e43c8016c775060cff2fd7e9e70231ba7eed` |
| `its_log_capture.py` | `d1ce61448534750fb005c1952b81149062bd9bd3217836d47b75fd61a5f1ca31` |
| `probe_http.py` | `b774a3cde28e3b3dadec0a8372d293515f900709194ef96bdbfa0e93e3724248` |
| `its.primary.override.yml` | `f2b920b24263bf4a81ed2300fc85ec788cfeb257229f2c7cf22c3288bc948ea1` |
| `gtfs.rollback.override.yml` | `4490a0d521653a1b0fab0e51f5839c8c6d593b9f6d10562342f61f0e14b11900` |

## Captura real e provas POSIX

Captura passiva por117,05s; `docker logs --follow --timestamps --since ...` observado usando ID imutável da API. Heartbeat, PID/nascimento, sessão, hash do script, imagem e container conferidos. Leitura356797bytes; arquivo final573bytes/3linhas (begin, stream_start, end), zero lacunas/truncamento. Como ITS e trace estavam OFF, **zero famílias ITS/GPS flow é esperado** e não reprova esta prova GTFS.

`--verify` executado no Debian após TERM ao **PID do coletor verificado**, nunca à API: integrity_valid=true, archive_complete=true, evidence_complete=true. SHA256 do arquivo encerrado: `339428d45d9f54c70bfee7273672717a6911dde4bc239e6ddba041c3006468ab`. Cópia idêntica retida localmente em artifacts/log-capture-debian-20261025T021126Z/capture-aggregates.jsonl. Captura e health0600, diretórios0700 comprovados por stat no Debian. Não dependemos de consultar um container removido posteriormente.

A leitura GTFS não contém agregados permitidos para replay: contador de duplicatas reais=0. Duas provas pequenas, sintéticas e sem rede, usando o Archive aprovado no Debian, confirmaram replay deduplicado (1duplicata) e orçamento reduzido a4linhas com footer íntegro/truncated=true/completude=false. Não são dados ITS nem mudanças nos limites operacionais8MiB/12000linhas. Recriação de container não foi provocada; permanece coberta pelos testes determinísticos anteriores.

A captura não herda deploy.lock nem stdin/stdout/stderr do SSH (todos /dev/null); SID/PGID próprios, processo reparentado aoPID1 e heartbeat avançando após desconexão real do primeiro SSH. Apenas inspect/logs nos filhos Docker, sem conexões Redis/PostgreSQL, chamadas ao provedor ou outra API escritora. A API continuou fazendo suas escritas GPS normais; não se afirma que os bancos ficaram globalmente sem escritas.

## Cron, exclusão e rollback preparado

Cron ativo. Instalado somente um job marcado `noponto-capture-proof-20261025T021126Z`, executando exclusivamente `--tick` com state.phase=proof. Cron real em02:14:01.343UTC produziu ancestralidade python3→sh→cron→cron→systemd e independent_cron=true após encerrar o SSH de instalação. Não entramos em scheduled, baseline ou its.

Lock real: uma segunda tentativa independente de flock não bloqueante foi rejeitada (código73) enquanto o primeiro detinha deploy.lock; após liberar o primeiro, outro processo adquiriu o lock com a captura ainda viva. Isso prova exclusão e ausência de herança, sem executar dois controladores. A prova do cron confirma entrega independente do watchdog; os guards PID/nascimento/cmdline e uso do mesmo lock foram verificados no código aprovado. **O ramo de recuperação que encerra controlador e aplica rollback não foi exercitado**, pois exigiria fases e alterações expressamente excluídas desta autorização. Não declarar um rollback real com base nesta prova.

`validate` renderizou em memória baseline, candidata e rollback: apenas as três chaves BUS e trace diferem; imagem/limites/conexões/demais serviços preservados. Rollback GTFS está preparado, não aplicado. Job marcado removido após proof; crontab voltou a ausente, como antes, e não restam processos ativos desse diretório. Nenhum sinal foi enviado à API nem serviço reiniciado.

## Custo e orçamento

Em55,18s entre amostras: CPU Python0,10s (~0,181% de um núcleo), RSS18,94MiB estável, leitura física adicional92KiB e escrita física216KiB (~3,91KiB/s); inclui fsync do health. Não inclui CPU dos clientes Docker e não mede o custo de fsync dos futuros agregados ITS.

1000s mantidos, contra120s de baseline +600s de Hybrid =280s de margem nominal para transições/rollback/fechamento. O teste terminou limpo antes do prazo; não esperamos1000s nem executamos a futura janela. O limite de tempo é de proteção, **não garantia de conclusão diante de todos os timeouts encadeados ou Docker indisponível**. Vencimento torna evidência incompleta; nunca se prolonga ITS para salvá-la. Supervisor/watchdog continuam responsáveis pelo retorno GTFS e a captura não segura seu lock.

8MiB/12000linhas, entrada64MiB, linha16KiB,2streams/4containers/32aberturas e1000s inalterados. Ritmo GTFS observado projetaria~3,05MB de entrada/1000s, longe de64MiB, mas não mede volume detalhado ITS. Sem overrides ItsCurrent/Trace no ambiente/appsettings; defaults do binário: intervalo HTTP mínimo1s,60páginas/aquisição, trace256logs e intervalo do coletor10s confirmado no ambiente. Esses limites tornam plausível a folga de linhas/bytes para600s; truncamento continua fail-closed e deve ser observado na janela. Não há evidência desta prova que justifique aumentar limites.

Gate89890s e critérios GPS/cadência/TTL inalterados. A aquisição histórica48,6s continua uma questão a medir; GTFS-only não a explica.

## Decisão e próximos gates

- Implementação/captura POSIX/Docker: aprovadas para a finalidade limitada desta prova.
- Cron independente/exclusão/encerramento: comprovados; recuperação destrutiva não executada.
- **GO para solicitar autorização específica de uma terceira janela supervisionada de até600s**, com rollback obrigatório ao mesmo digest151 em GTFSRT_BUS/fallback vazio/FullFallback/trace=false. Não é autorização nem aprovação do híbrido.
- Antes de armar: preflight imediato renovado, diretório/hash/prova cron novos, uma escritora/lock livre/recursos saudáveis, captura saudável e baseline120s com seteBUS/trêsBRT. Provar a mesma observação898 dentro90s; interromper por qualquer gate existente. Remover somente job próprio depois do retorno confirmado e reter evidências/overrides.

Não houve push, merge, CI, deploy, migrations, limpeza ou alteração de fonte/imagem/configuração da API/BRT. Não foram repetidas suítes locais/C# já aprovadas: nenhum código funcional mudou. Relatório e resumo registram esta execução; os60testes locais anteriores continuam referência da simulação de recriação/falhas.
