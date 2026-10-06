# NoPonto — build, CI/CD, deploy e rollback

**Finalidade:** reconstruir a cadeia de artefatos e explicitar o que é automatizado, verificável ou ausente.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Estado:** workflows, Dockerfile, Compose, `package.json`, `eas.json` e `app.json` inspecionados; histórico remoto de execuções e script de deploy não auditados.

## Backend: código até produção

```text
push main ou workflow_dispatch -> runner Ubuntu -> Buildx -> imagem linux/amd64
-> GHCR (latest + timestamp-SHA curto + build-N)
-> somente workflow_dispatch: Tailscale -> SCP do Compose -> SSH -> deploy.sh
-> pull/recriação da API, comportamento interno NÃO VERIFICADO
```

O job `build` autentica no GHCR com Secrets, usa cache GHA e publica três tags. `push main` constrói/publica, mas não implanta. O job `deploy` só executa em `workflow_dispatch` e após build bem-sucedido. A conexão administrativa usa Tailscale e chave SSH guardada em Secrets; valores não foram consultados.

O Compose produtivo aponta para `latest`, embora tags mais identificáveis sejam geradas. A fotografia anterior obteve digest abreviado e data da imagem, mas nenhuma label OCI `revision`; coincidência temporal/contratual não prova igualdade binária com o commit local. Tags mutáveis dificultam auditoria e rollback.

## Validações reais e lacunas

O Docker build executa `dotnet restore` e `dotnet publish`; compilação é uma validação implícita. O workflow não possui etapa explícita de `dotnet test`, lint, integração PostgreSQL/Redis, migration check, SAST/SCA, SBOM, assinatura, smoke test ou healthcheck pós-deploy. O Compose possui healthchecks apenas de PostgreSQL/Redis, não da API.

O script remoto pode conter verificações ou rollback, mas não foi lido; portanto não se deve atribuí-los ao pipeline. Copiar o Compose também pode alterar declaração da stack, porém o efeito exato depende do script. Não há estratégia blue/green, canary de container ou alta disponibilidade demonstrada.

## Rollback e rastreabilidade

Não foi comprovado rollback formal. Em princípio, uma tag versionada já publicada poderia ser selecionada, mas o Compose atual referencia `latest` e não automatiza essa escolha. Um rollback seguro também precisa considerar compatibilidade de migrations; voltar apenas a imagem pode ser incompatível com schema avançado.

Melhorias propostas: pin por digest ou tag imutável; labels OCI com revision/source/created; registrar digest implantado; promover o mesmo artefato testado; preservar N versões; smoke test read-only; procedimento de rollback com matriz de compatibilidade de schema. Essas medidas não estão implantadas.

## Frontend Expo/EAS

O workflow dispara em push de `main` ou manualmente, usa Node 22 e pnpm 10, instala com lockfile, configura EAS `latest` via `EXPO_TOKEN` e executa `eas build --platform android --profile preview --non-interactive --clear-cache`. O perfil `preview` produz APK de distribuição interna; o perfil `production` produziria app bundle com auto incremento, mas não é usado pelo workflow examinado.

Não há lint/test/typecheck explícito nem download/upload do APK como artifact do GitHub. O artefato fica sob o fluxo EAS; retenção, URL e instalação efetiva não foram confirmadas. Publicação em loja não foi demonstrada. `appVersionSource` é remoto; `app.json` anuncia versão 2.0.0, enquanto `package.json` contém 1.0.0, divergência que exige definição de fonte de verdade. Configuração de URL da API provém do código/ambiente de build e a versão distribuída continua `NÃO VERIFICADO`.

## Riscos e evidências

Riscos: deploy manual sem smoke test comprovado; `latest`; actions fixadas por tags major/minor e EAS `latest`; ausência de testes; script remoto opaco; possível indisponibilidade na recriação de uma única API. Fontes: `.github/workflows/deploy.yml`, workflow mobile, Dockerfile, Compose, `eas.json`, `app.json` e `package.json`. Nenhum workflow, build ou deploy foi executado.

Relacionados: [implantação](../02-arquitetura/07-arquitetura-de-implantacao.md), [segurança](06-seguranca-e-superficie-de-exposicao.md) e [plano](08-operacao-riscos-e-plano-de-melhorias.md).
