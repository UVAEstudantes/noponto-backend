# Regras permanentes — NoPonto

1. Antes de modificar código, consultar este `AGENTS.md` e `NoPonto/HISTORICO.md`, além das instruções aplicáveis aos diretórios envolvidos. Direcionar a consulta ao histórico à tarefa: ler o cabeçalho, as entradas recentes e buscar termos relevantes; ampliar a leitura apenas quando necessário.
2. Verificar implementações e dependências existentes para evitar funcionalidades duplicadas e regressões.
3. Realizar alterações pequenas, isoladas e justificadas, dentro do escopo autorizado.
4. Não modificar infraestrutura, Docker, produção, redes, bancos reais ou credenciais sem autorização explícita. Push/merge na `main` pode disparar deploy conforme a configuração do workflow e exige autorização operacional explícita.
5. Não executar migrations no banco de produção.
6. Preservar compatibilidade com funcionalidades existentes.
7. Criar ou atualizar testes relevantes para cada correção. Utilizar os mecanismos de seleção e verificação de testes offline existentes em `.ci`. Antes de qualquer teste conectado, inspecionar suas fixtures. Testes que possam executar migrations, `EnsureDeleted`, limpeza de dados ou outras operações destrutivas exigem verificação do destino real da conexão e autorização explícita; `localhost` não comprova ambiente descartável. A proibição da regra 5 permanece absoluta.
8. Registrar toda alteração efetiva em `NoPonto/HISTORICO.md`, acrescentando entradas sem sobrescrever as anteriores.
9. Informar claramente testes executados, não executados e bloqueios encontrados.
10. Não assumir que comportamentos suspeitos são bugs sem analisar regras e dependências.
11. Não remover funcionalidades ou código legado sem verificar referências e uso real. Verificar `.csproj`, registros de DI e consumidores antes de tratar implementações legadas como código ativo.
12. Nunca incluir senhas, tokens ou dados sensíveis no histórico, logs, comandos, respostas ou ferramentas externas.
