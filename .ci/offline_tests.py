"""Allowlist offline e validação TRX; somente biblioteca padrão, sem rede."""
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

MANIFEST = Path(__file__).with_name("offline-tests.json")
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def selection():
    data = json.loads(MANIFEST.read_text(encoding="utf-8"))
    classes, excluded = data["classes"], data["excluded_methods"]
    if not classes or len(set(classes)) != len(classes):
        raise ValueError("Allowlist vazia ou duplicada")
    if not all(re.fullmatch(r"[A-Za-z_][A-Za-z_0-9.]*", x) for x in classes + excluded):
        raise ValueError("Identificador inválido na allowlist")
    if any(x.rsplit(".", 1)[0] not in classes for x in excluded):
        raise ValueError("Exclusão fora da allowlist")
    return classes, excluded


def test_filter():
    classes, excluded = selection()
    # Ponto após a classe evita selecionar FooTestsIntegration por substring.
    included = "|".join(f"FullyQualifiedName~{x}." for x in classes)
    return "(" + included + ")" + "".join(f"&FullyQualifiedName!={x}" for x in excluded)


def verify(path):
    classes, excluded = selection()
    root = ET.parse(path).getroot()
    counters = root.find("t:ResultSummary/t:Counters", NS)
    results = root.findall("t:Results/t:UnitTestResult", NS)
    if counters is None or not results:
        raise ValueError("TRX sem resultados")
    total = int(counters.get("total", "0"))
    if total != len(results) or int(counters.get("passed", "0")) != total:
        raise ValueError("TRX contém falhas, ignorados ou contagem inconsistente")
    definitions = {
        test.get("id"): test.find("t:TestMethod", NS)
        for test in root.findall("t:TestDefinitions/t:UnitTest", NS)
    }
    seen = set()
    for result in results:
        method = definitions.get(result.get("testId"))
        if result.get("outcome") != "Passed" or method is None:
            raise ValueError("Resultado não aprovado ou sem definição")
        # Alguns adapters incluem a assembly depois do nome da classe.
        class_name = method.get("className", "").split(",", 1)[0]
        full_name = class_name + "." + method.get("name", "")
        if class_name not in classes or full_name in excluded:
            raise ValueError("Teste fora da seleção offline")
        seen.add(class_name)
    if seen != set(classes):
        raise ValueError("Uma ou mais classes offline não executaram")
    return total


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["filter"]:
            print(test_filter())
        elif len(sys.argv) == 3 and sys.argv[1] == "verify":
            print(f"CI offline: {verify(sys.argv[2])} testes aprovados, todas as classes presentes.")
        else:
            raise ValueError("Uso: offline_tests.py filter | verify arquivo.trx")
    except (ValueError, OSError, ET.ParseError, KeyError) as error:
        # Nunca imprime conteúdo do TRX nem payloads dos testes.
        print(f"Validação offline recusada: {error}", file=sys.stderr)
        sys.exit(1)
