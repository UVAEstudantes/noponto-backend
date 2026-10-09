#!/bin/sh
set -eu

# Falha antes da descoberta dos testes se o container possuir outra interface.
# O loopback é necessário para a comunicação local VSTest/testhost.
for interface in /sys/class/net/*; do
  if [ "${interface##*/}" != lo ]; then
    echo 'CI offline exige container com --network none.' >&2
    exit 1
  fi
done

: "${NOPONTO_TEST_FILTER:?Seleção offline obrigatória}"
: "${1:?Diretório de resultados obrigatório}"
# Executa a assembly já compilada; não reavalia targets MSBuild nem faz restore.
dotnet vstest NoPonto/bin/Release/net9.0/NoPonto.dll \
  "--TestCaseFilter:$NOPONTO_TEST_FILTER" "--ResultsDirectory:$1" \
  '--Logger:trx;LogFileName=offline.trx'
