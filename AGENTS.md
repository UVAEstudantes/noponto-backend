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
