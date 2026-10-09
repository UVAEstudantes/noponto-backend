#!/bin/sh
set -eu

# Sem capacidades DAC, root não pode escrever em volumes pertencentes ao runner.
# O CI deve usar o mesmo UID/GID não privilegiado que criou esses diretórios.
if [ "$(id -u)" -eq 0 ]; then
  echo 'CI exige o UID/GID não privilegiado do runner.' >&2
  exit 1
fi

if [ "$#" -eq 0 ]; then
  echo 'Informe os diretórios de CI que precisam permitir escrita.' >&2
  exit 1
fi

for directory do
  if [ ! -d "$directory" ] || [ ! -w "$directory" ]; then
    echo "Diretório de CI sem permissão de escrita: $directory" >&2
    exit 1
  fi
  # Testa escrita efetiva (inclusive mounts somente leitura), sem alterar fontes.
  probe=$(mktemp "$directory/.noponto-ci-write.XXXXXX")
  rm -f -- "$probe"
done
