# 3G.3B.2B.2 — proposta de autoridade operacional (não implementada)

## Decisão

Certificação automática REPROVADA/OFF. Esta proposta exige aprovação separada antes de qualquer mudança de permissões, roles, ACLs, credenciais, implantação ou autoridade de escrita. Nenhuma dessas mudanças foi aplicada na 2B.1.

## Caminhos realmente existentes

`ViagemOperacionalRepository` mantém estado/outbox em transação PostgreSQL; a integração de qualidade reaproveita essa transação. `ViagemOperacionalRedisScripts.CommitHot` compara os 27 campos do codec e a versão durável, enquanto `ProjectDurable` projeta estado/versão. Esses scripts não exigem owner/epoch/release. O CAS de qualidade protege sua própria cadeia, mas não impede um binário legado de escrever estado operacional usando SQL ou Lua.

Advisory lock, token cooperativo, assinatura fornecida pelo próprio chamador ou variável de sessão não impedem esse escritor. Manter sua credencial com DML/EVAL torna impossível demonstrar exclusividade.

## Menor barreira demonstrável a homologar

1. PostgreSQL: retirar do principal da API escrita direta em `ViagensOperacionais`, `OutboxViagens` e tabelas de qualidade; inventariar todos os demais consumidores dessas tabelas antes de revogar. Disponibilizar uma operação transacional restrita que valide owner, epoch, token, release autorizado e CAS operacional, sob locks duráveis, escrevendo estado/outbox/qualidade atomicamente. Caso implementada como função `SECURITY DEFINER`, proprietário sem login, `search_path` fixo, nomes qualificados, parâmetros validados e `EXECUTE` apenas para o principal autorizado. Não conceder ao principal da API capacidade de redefinir função, trocar proprietário ou criar objetos nos schemas envolvidos.
2. Redis: adicionar campos de fencing ao Lua é necessário, mas insuficiente se a mesma credencial pode executar HSET/EVAL arbitrário. Uma autoridade exclusiva de escrita precisa possuir a credencial de mutação; os produtores enviam comandos tipados para ela. A API antiga deve perder acesso a mutação das chaves operacionais. Um gateway pequeno é uma possibilidade, não uma infraestrutura autorizada nesta etapa. Provar primeiro quais comandos/chaves os outros fluxos da API precisam; uma ACL que preserve EVAL irrestrito e escrita nas mesmas chaves não atende ao requisito.
3. Identidade de release: um commit declarado no payload não é autenticação. Supervisor/autoridade deve associar a identidade do processo a uma autorização não disponível a instâncias antigas (credencial de curta duração ou identidade de serviço). Epoch autorizado possui lease/token e revogação duráveis. Token não deve ser escolhido pelo produtor.
4. Toda escrita quente deve passar pela autoridade, inclusive retry, projeção, recuperação e caminho legado. PostgreSQL é autoridade de fencing; inconsistência entre commit e projeção Redis invalida cobertura até reconciliação comprovada. Lease Redis sozinho não substitui o controle durável.

Essas alterações têm impacto operacional real: permissões SQL, acesso Redis, configuração de instâncias e recuperação. Exigem inventário dos leitores/escritores, orçamento medido e revisão da integração antes de escolher gateway/procedimento definitivo. Não se promete exclusividade implementando apenas metade da barreira.

## Startup, takeover, release e rollback

- Startup só recebe epoch após provar versão compatível e que escritores antigos perderam autoridade. Viagens abertas anteriores permanecem NaoVerificada; não adotar witness limpo.
- Takeover revoga owner antigo na autoridade durável antes de autorizar novo token. Testar corrida com transações em andamento; comandos antigos devem falhar mesmo após reconexão.
- Troca de release: desativar certificação, drenar processamento admitido, fechar/reconciliar o que puder, invalidar execuções abertas, revogar acesso antigo e conexões persistentes, validar nova autoridade e só depois autorizar novo epoch. Revogação de ACL precisa considerar conexões existentes.
- Rollback para binário incompatível só após desligar certificação e invalidar as execuções afetadas; não devolver credenciais capazes de contornar fencing enquanto outro produtor mantém cadeia ativa. Rollback não apaga journal nem limpa flags negativas.
- Falha da autoridade deve rejeitar certificação; disponibilidade operacional e preservação do ETA público precisam de plano aprovado, sem fallback silencioso para escritor sem fencing.

## Critérios de aceitação antes da ativação

Fixture exclusiva deve demonstrar: SQL direto negado; HSET/EVAL legado negado; reconexão com credencial antiga negada; dois produtores disputando owner; token obsoleto após takeover; release falsa; revogação durante commit; crash entre PostgreSQL e Redis; outbox reentregue; rollback; expiração de lease; interrupção da autoridade. Nenhum desses casos pode produzir AuditadaSemProtecao indevida.

Medir p50/p90/p95, recursos e atraso de polling em 1/10/50/200 veículos, com os caminhos reais e perdas controladas. Somente após essas provas, revisão de permissões e aprovação separada se pode discutir habilitação prospectiva. A 2B.1 não executa nenhuma instrução deste plano.
