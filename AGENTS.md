# Regras permanentes — NoPonto

1. Antes de modificar código, consultar este `AGENTS.md` e `NoPonto/HISTORICO.md`, além das instruções aplicáveis aos diretórios envolvidos.
2. Verificar implementações e dependências existentes para evitar funcionalidades duplicadas e regressões.
3. Realizar alterações pequenas, isoladas e justificadas, dentro do escopo autorizado.
4. Não modificar infraestrutura, Docker, produção, redes, bancos reais ou credenciais sem autorização explícita.
5. Não executar migrations no banco de produção.
6. Preservar compatibilidade com funcionalidades existentes.
7. Criar ou atualizar testes relevantes para cada correção.
8. Registrar toda alteração efetiva em `NoPonto/HISTORICO.md`, acrescentando entradas sem sobrescrever as anteriores.
9. Informar claramente testes executados, não executados e bloqueios encontrados.
10. Não assumir que comportamentos suspeitos são bugs sem analisar regras e dependências.
11. Não remover funcionalidades ou código legado sem verificar referências e uso real.
12. Nunca incluir senhas, tokens ou dados sensíveis no histórico.

13. Centralizar preferencialmente os relatórios técnicos `.md` produzidos pelo Codex em `noponto-docs`, organizados por data e área.

## Política obrigatória de diretórios e branches

1. Utilizar exclusivamente os diretórios oficiais `noponto-frontend`, `noponto-backend` e `noponto-docs` para desenvolvimento.
2. **É proibido criar Git worktrees, clones adicionais ou pastas separadas para branches**, salvo autorização explícita do usuário.
3. Toda branch nova deve ser criada e utilizada dentro do diretório original de seu repositório.
4. Antes de trocar de branch, verificar alterações locais e preservar arquivos não commitados.
5. Nunca descartar arquivos locais, ignorados ou não rastreados sem autorização.
6. Preservar `.env`, dependências, configurações de Android, Firebase, arquivos locais e demais recursos ignorados pelo Git.
7. Nunca fazer commits diretamente na `main` do frontend.
8. Criar commits progressivos nas branches de trabalho sempre que uma correção ou etapa verificável for concluída.
9. Antes de qualquer operação Git que possa sobrescrever arquivos locais, conferir seu impacto e evitar perda de trabalho.
10. Não criar estruturas alternativas de desenvolvimento por iniciativa própria.
