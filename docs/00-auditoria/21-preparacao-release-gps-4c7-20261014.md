# GPS 4C.7 — release diagnóstica preparada localmente

Preparação concluída em 14/10/2026. **Sem acesso ao homeserver, transferência, deploy, migrations, commit, push ou merge.** A causa da perda de passagens continua dependendo da futura evidência correlacionada; nenhuma correção funcional foi aplicada.

## Escopo e correções

Partiu-se dos artefatos interrompidos, preservando a candidata 4C.4 congelada, o executor v2-fix1 e os demais trabalhos. Foram corrigidas duas lacunas demonstradas: rejeições podiam consumir o orçamento global antes de normal/BRT, e não havia informação para distinguir supressão de ausência de evento elegível.

Com a configuração fixa da release, cada família possui 19 tentativas e 40.960 bytes independentes. Há cinco resumos periódicos e um final com reserva própria; no máximo 63 registros/128 KiB. Resumos mostram observações elegíveis, exportações aceitas pelo logger, supressões, falhas do logger e aceites BRT fora dos slots selecionados. Falhas do filtro/logger também consomem orçamento. HMAC e correlação são exclusivos da instância/janela, apagados no encerramento; nenhuma coordenada, identidade física em claro, código de linha, payload ou segredo é registrado. BRT conserva dois aceites consecutivos de até dois veículos; snapshots ignorados não ocupam esses slots. Ausência de par não prova expiração do Redis.

Arquivos transportados sobre os bytes 4C.4, listados em `source-context-manifest.json`: `GpsCicloPerformance.Operacional.cs`, `GpsPollingService.cs`, `ViagemOperacionalRepository.cs`, `Program.cs`, `GpsCorrelatedDiagnostics.cs`, `GpsCorrelatedDiagnosticsTests.cs` e `GpsPassagens4C6Tests.cs`. Nesta revisão foram alterados somente o amostrador, seu hook de resumo no polling e suas fixtures. OFF permanece o default; sem alteração de matching, cruzamento, TTL, deduplicação, persistência, dependências, Dockerfile ou ETA/ML. Os sete arquivos são a allowlist completa; os demais 679 arquivos do contexto são idênticos ao congelado 4C.4.

## Imagem e integridade

Base: `6f64538b47cb3987c5bf023d9878931b69228dac`, com delta 4C.4 homologado mais alterações 4C.7 **não commitadas**, declaradas nos labels. Contexto de **686 arquivos**, SHA-256 `83d42468ee991b788e50edbd51bcda72a8d1eaec09c7196beb27f6bbd674b5ba`. Delta autorizado SHA-256 `322cf72dd9d4222ea2d50448ecdb662fc5c75f52dbb4808af91f19d2ca967c5b`.

| Identificação | Valor verificado |
|---|---|
| Tag exclusiva | `noponto-api:gps-4c7-322cf72dd9d4-20261014` |
| Plataforma | `linux/amd64` |
| Índice OCI | `sha256:9140ffb2e789754e452ffaec6c0fb95b72c1cf50de666389205afa47f4a708e1` |
| Manifesto linux/amd64 | `sha256:4176441b5e63747d73bfab37ab9c4be7d24b0c5215284e0288e2bc67a8a787af` |
| Config digest | `sha256:e6833deefbcbbc9aa36205d9fef6396316a5e71baf5c82f435b46810c0b11d5d` |
| TAR | `gps-4c7-322cf72dd9d4-20261014.tar`, **113.886.208 bytes** |
| SHA-256 TAR | `3f2aaf73d0a91b14bfa084efa9b2b41281c55b12fbd2b0375daee93c6646054a` |
| SHA-256 executor | `72b44784632912eb7853502947d7750f7fc3456cc9c124274ce281480d483d37` |

A revisão final corrigiu também uma starvation reproduzida: duas leituras BRT sem aceite impediam registrar outro veículo realmente aceito. Agora somente leituras pendentes podem ser substituídas; veículos já aceitos ficam protegidos, e leitura não retida é explicitamente ReadNotObserved. A regressão falhou antes do patch e passou depois. Foram construídas duas revisões locais distintas, uma tentativa cada; a primeira, não transferida, foi substituída por esta revisão final. Artefatos anteriores preservados em revision-487e28e76f95; usar somente a tag e o TAR desta tabela. Dockerfile existente; SDK resolveu `sha256:2f5eb32870b4c0074a3d5b5191a03553bcab7b74463a76a7c0a5f3ef65c90fe5`; runtime continua no digest fixado pelo Dockerfile. Espaço observado em C: cerca de **9,4 GiB**, sem limpeza. Verificada no TAR a cadeia índice → manifesto → config, os hashes de todas as camadas, labels e plataforma; o ID de `inspect --platform` coincide com o manifesto, não com o config. A tag diagnóstica 4C.5 local e os arquivos do executor anterior permaneceram intactos.

## Executor e validação

Executor próprio com snapshot `4c7.*`, pointer privado persistente e recuperação separada. Congela fontes/renderização integral, fixa explicitamente a imagem original no `current.json` e admite na candidata somente índice, plataforma, rotação API e sete opções diagnósticas. Retorno deriva da configuração original, sem ativação herdada. `--check` não escreve nem recria; mesma exclusão por deploy.lock; execução requer confirmação, bloqueio da automação e política numérica aprovada.

Marco UTC anterior ao up; uma chamada logs --since recupera registros anteriores ao health. Janela candidata até 300 s incluindo inicialização; entrada/saída até 8 MiB, eventos até 512 KiB, arquivos privados fora do snapshot. Resumo exige rejeição, normal e par BRT da mesma janela/veículo com predecessores GPS e aceite correspondentes. Inconclusiva não dispensa retorno. Até 180 s sem nova amostra encerra antecipadamente; status periódico não reinicia esse prazo. Resumo final registra também se o final da aplicação foi realmente observado, sem presumir entrega de logs emitidos durante o shutdown.

**Executados:** 413 testes C# offline, incluindo 24 do amostrador; **72 testes Python**, zero falhas/ignorados finais; AST de sete arquivos Python e `bash -n` de oito blocos do roteiro, sem executar os comandos. Cobertura inclui cotas, final/privacidade/filtro com falha, decisões OFF/ON idênticas, captura anterior ao health, duplicatas, categorias ausentes, limites, interrupção, health/Compose com falha, rollback, configuração/source render divergente, config fingerprint, imagem original, check sem escrita e recuperação por pointer. Ajustes intermediários nas fixtures e documento ainda ausente foram resolvidos antes da suíte final. Compilação com cinco avisos preexistentes.

**Não executados:** API da nova imagem, SSH, preflight real do executor novo no systemd/Compose do servidor, carga remota, transferência ou janela. Os testes usam fixtures/fakes locais; não substituem o preflight autorizado no contexto real.

## Artefatos e próximos passos

Diretório absoluto: **`D:\repositorio_github\NoPonto\gps-diagnostics-4c7-release-20261013`**.

- TAR e `.tar.sha256`; `image-identity.json`; `source-context-manifest.json`; `gps4c7-only.final.patch`.
- `executor.py`, `prepare_snapshot.py`, `recover.py`, `test_executor.py`, `test_4c7.py`.
- `OPERACAO.md`: comandos separados de transporte/carga, snapshot/check, systemd, acompanhamento e recuperação — todos **NÃO EXECUTADOS**.
- `fixtures/candidate.synthetic.json` e `fixtures/return.synthetic.json`: propostas sintéticas, não usar em produção. O preparador produzirá os JSON completos a partir da configuração vigente após autorização.
- `SHA256SUMS`, `SHA256SUMS-code`, `validation-final.json`, `operation-static-validation.json`, logs de build/testes e TRX em `test-results`.

**Pronto localmente para revisão e futura transferência autorizada.** Nenhum bloqueio local de build/integridade/testes permanece. Transferência depende de autorização separada e confirmação de espaço/acesso no destino; roteiro usa subdiretório novo para preservar os artefatos 4C.5. Implantação ainda depende de carga/preflight, novo snapshot, política numérica vigente aprovada e confirmação de automação bloqueada/nenhum job concorrente. Valores dessa política e estado remoto atual não foram presumidos. A janela pode ser inconclusiva por falta de eventos/pares; SIGKILL, queda do Docker/host ou disco não têm garantia de recuperação automática. Retorno manual usa exclusivamente `sha256:dbe503a480387dc1ab6e54857cc7f052d3524a61991666fc14e022dac833af79`, sem latest, pull ou reconstrução.
