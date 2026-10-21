# Diagnóstico independente de DNS, TLS e transferência

**SOMENTE PLANO; NÃO EXECUTADO.** Não depende de deploy, Redis, PostgreSQL ou frontend. Não altera DNS, IPv6, proxy, certificados, firewall, sysctl, timeouts operacionais ou containers. Requer autorização para sondas adicionais e ferramentas já disponíveis; registrar ausência sem instalar pacotes. Não executar simultaneamente com o comparativo41. Saída privada, com metadados limitados e nenhum corpo GPS.

## 1. Contexto antes de sondar

No homeserver, comandos futuros somente de leitura:

```bash
date -u '+%Y-%m-%dT%H:%M:%SZ'
timedatectl show --property=NTPSynchronized --property=Timezone
curl --version
openssl version
python3 --version
python3 -c 'import os; print({k:bool(os.environ.get(k)) for k in ("HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","NO_PROXY","http_proxy","https_proxy","all_proxy","no_proxy","CURL_CA_BUNDLE","SSL_CERT_FILE")})'
timeout 5 getent ahosts dados.mobilidade.rio
timeout 5 getent ahostsv4 dados.mobilidade.rio
timeout 5 getent ahostsv6 dados.mobilidade.rio
```

Guardar respostas A/AAAA/endereços somente no artefato privado para correlacionar mudanças de destino. Um processo getent separado usa resolução/cache do host; isso não mede automaticamente a resolução do HttpClient no container. Presença de variável proxy não prova uso pelo request e valores não devem ser publicados.

Se getent não existir ou o resolver não oferecer AAAA, marcar indisponível/sem resposta. Não concluir falha de rede IPv6 apenas por uma ausência DNS. Não executar resolvectl flush-caches, reiniciar resolved, editar resolv.conf, desabilitar IPv6 nem acrescentar --insecure.

## 2. Uma transferência completa com metadados limitados

Função Bash futura. Uma chamada = um GET completo descartado, sem retry, sem redirect, sem guardar corpo. O primeiro argumento --disable evita opções ocultas de curlrc na sonda. Mantém limite diagnóstico de conexão5s/total15s informado pelo usuário; esses valores não são transferidos para o backend. O limite de tamanho evita download excessivo e não altera a resposta do servidor.

```bash
probe() (
  mode=${1:-baseline}
  extra=()
  case "$mode" in
    baseline) ;;
    http1) extra=(--http1.1) ;;
    ipv4) extra=(-4) ;;
    ipv6) extra=(-6) ;;
    *) printf 'Modo invalido.\n' >&2; exit 2 ;;
  esac
  date -u '+probe_utc=%Y-%m-%dT%H:%M:%SZ'
  printf 'probe_mode=%s\n' "$mode"
  set +e
  curl --disable --silent --show-error --compressed \
    --connect-timeout 5 --max-time 15 --max-filesize 8000000 \
    --dump-header - --output /dev/null "${extra[@]}" \
    --write-out '\nprobe_status=code:%{http_code} version:%{http_version} dns:%{time_namelookup} tcp_end:%{time_connect} tls_end:%{time_appconnect} first_byte:%{time_starttransfer} total:%{time_total} bytes:%{size_download} speed:%{speed_download} ssl_verify:%{ssl_verify_result}\n' \
    https://dados.mobilidade.rio/gtfs/realtime/onibus/vehicle-positions 2>/dev/null |
  python3 -c 'import sys
for line in sys.stdin:
 if line.lower().startswith(("http/","date:","cache-control:","age:","retry-after:","content-type:","content-length:","content-encoding:","probe_status=")):
  print(line.rstrip())'
  status=("${PIPESTATUS[@]}")
  printf 'curl_exit=%s metadata_filter_exit=%s\n' "${status[0]}" "${status[1]}"
  if (( status[1] != 0 )); then exit 2; fi
  exit "${status[0]}"
)

# Executar uma chamada por vez, somente na janela autorizada:
probe baseline
```

Não há --fail: HTTP429/503 ficam visíveis pelo status, sem confundi-los com erro de transporte. Código curl0 exige ainda HTTP200 e transferência completa para contar disponibilidade do feed; 304 sem corpo não é amostra nova de GPS. HTTPS via proxy pode mostrar status de CONNECT anterior; usar code da linha probe_status como resposta final. Content-Length comprimido e bytes transferidos não são tamanho protobuf descomprimido. Sem Content-Length, usar EOF bem-sucedido e validador do conteúdo no comparativo; tamanho sozinho não valida GTFS.

Uma transferência válida deve completar dentro dos limites existentes. Não aumentar max-time após falha só para contar sucesso. As consultas diagnósticas acrescentam tráfego ao provedor; entre chamadas aguardar pelo menos60s e também o prazo maior de cache/Retry-After observado, incluindo Retry-After em data absoluta. Limitar a8 transferências em até15min; se a política exigir espera maior, encerrar com menos amostras. Zero retries automáticos. Registrar o horário de cada tentativa, inclusive as malsucedidas.

## 3. Isolar uma variável por vez

Se houver falha e o provedor permitir novas sondas, repetir baseline e escolher somente uma comparação por rodada:

```bash
# Cada linha em outra rodada, com espera obrigatoria entre elas.
probe http1
probe ipv4
probe ipv6
```

Não executar as três em sequência sem espaçamento. Comparar HTTP1 com negociação padrão mantendo família/DNS, ou IPv4/IPv6 mantendo protocolo padrão. Se curl não suportar HTTP2, isso limita a comparação e não diagnostica o provedor. Não forçar uma versão TLS, um IP em --resolve ou troca de DNS sem hipótese/evidência e autorização próprias.

Um handshake separado, se openssl já existir, com verificação de cadeia e hostname:

```bash
set +e
timeout 15 openssl s_client -connect dados.mobilidade.rio:443 \
  -servername dados.mobilidade.rio -verify_hostname dados.mobilidade.rio \
  -verify_return_error -brief </dev/null 2>&1 |
python3 -c 'import sys
for s in sys.stdin:
 t=s.strip()
 if t.startswith(("CONNECTION ESTABLISHED","Protocol version:","Ciphersuite:","Verification:","Verification error:","Verify return code:")):
  print(t)'
tls_status=("${PIPESTATUS[@]}")
printf 'openssl_or_timeout_exit=%s metadata_filter_exit=%s\n' "${tls_status[0]}" "${tls_status[1]}"
```

Este comando não envia HTTP nem reutiliza automaticamente o proxy do curl; comparar exige a mesma rota efetiva. Timeout124 vem do utilitário timeout. Não imprimir tickets TLS, sessão, certificados completos, headers de autenticação ou conteúdo de environment. Um handshake aprovado não prova vazão HTTP nem valida GTFS.

## 4. Contexto do container, opcional e ainda somente leitura

Não iniciar container de diagnóstico nem instalar curl/python na API. As ferramentas da imagem podem não existir; o resultado deve continuar útil sem elas. Comandos abaixo não mudam configuração e são separados das sondas do host:

```bash
docker inspect --format '{{.HostConfig.NetworkMode}}' noponto_api
docker exec noponto_api cat /etc/resolv.conf
docker exec noponto_api cat /etc/os-release
```

Não publicar endereços/search domains internos. Conferir privadamente apenas presença de proxies/CA/DNS efetivos, sem expor valores sensíveis. Se curl já existir no container, um request extra ainda consome a quota total da janela e precisa do mesmo filtro/limites. Ausência de curl não autoriza instalação ou container temporário. Não assumir que sucesso no host comprova sucesso no namespace, proxy ou trust store do processo .NET.

## 5. Interpretação e separação do patch

| Evidência | Interpretação possível / limite |
| --- | --- |
| curl6 e DNS ausente/lento | Falha de resolução na tentativa. Correlacionar getent, cache e resolver; não trocar DNS automaticamente. |
| curl7 | Conexão TCP não estabelecida. Não comprova qual componente de rota recusou/falhou. |
| curl28 com tls_end=0 | Timeout antes de completar TLS. DNS/TCP podem consumir parte do budget; não concluir que a causa seja o certificado. |
| curl35 ou60 | Falha de handshake ou de validação TLS, respectivamente. Verificar relógio/CA/SNI/rota com metadados limitados; não usar -k. |
| HTTP200, primeiros bytes, curl28/18 e corpo parcial | Headers chegaram; aquisição do feed não completou. Comparar fase/bytes/TTFB/total, sem contabilizar GPS novo. |
| Handshake rápido, TTFB demorado | Espera após TLS ou resposta/intermediário; não diagnostica DNS como causa principal daquela tentativa. |
| TTFB rápido, transferência lenta | Investigar vazão/congestionamento/servidor/HTTP2/proxy. O patch local não corrige esse transporte. |
| HTTP429/503 e Retry-After | Pressão/indisponibilidade declarada. Suspender sondas e respeitar espera; não aumentar paralelismo. |
| Host bom, container ruim | Diferença de resolver/namespace/proxy/CA/handler possível. Não é prova suficiente para alterar infraestrutura. |

As métricas time_* são tempos acumulados desde o início: derivar TCP≈time_connect−time_namelookup; TLS≈time_appconnect−time_connect; espera≈time_starttransfer−time_appconnect; corpo≈time_total−time_starttransfer somente quando as fases correspondentes completaram e o protocolo/rota permitirem. Não subtrair zeros de fases incompletas para produzir durações fictícias. Referência primária: [manual curl](https://curl.se/docs/manpage.html).

O relatório final do diagnóstico deve conter janela UTC, versões/família/protocolo, contagens por status/código, fases completas/incompletas, bytes/latência/vazão, mudança de destino e disponibilidade de ferramentas. Falha genérica da API acquisition_timeout também inclui espera por gate/crosswalk; isoladamente não diferencia TLS e corpo. As sondas externas fornecidas em21/10 são evidência de transporte, não prova de que os achados304 ocorreram em produção. Registrar hipótese e evidência faltante antes de qualquer proposta de mudança.
