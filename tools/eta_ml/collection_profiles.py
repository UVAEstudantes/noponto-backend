"""Official allowlist shared with the exporter's embedded resource."""
import json
from datetime import datetime
from pathlib import Path

CATALOG = Path(__file__).resolve().parents[1] / "collection_profiles.json"
PROFILES = json.loads(CATALOG.read_text(encoding="utf-8"))
HISTORICAL = "historical-pre-fix"
CURRENT = "official-post-fix"
SYNTHETIC = "synthetic-fixture-3b-v1"


def instant(value):
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.tzinfo is None:
        raise ValueError("Cutoff sem timezone")
    return result


def match(profile):
    for name, allowed in PROFILES.items():
        candidate = dict(profile)
        if instant(candidate["cutoff"]) != instant(allowed["cutoff"]):
            continue
        candidate["cutoff"] = allowed["cutoff"]
        if json.dumps(candidate, sort_keys=True) == json.dumps(allowed, sort_keys=True):
            return name
    raise ValueError("Perfil de coleta oficial incompatÃ­vel ou misturado")


def from_audit(audit):
    return {"cutoff": audit["CollectionStartedAtUtc"], "backend_commit": audit["BackendCommit"],
            "image": audit["Image"], "migration": audit["Migration"],
            "dataset_contract": audit["DatasetVersion"], "sampling": audit["Sampling"]}


def dataset_profile(manifest, config):
    if __package__:
        from .validation_policies import policy
    else:
        from validation_policies import policy
    policy(manifest, config)
    collection = manifest.get("collection")
    if collection is not None:
        name = match(from_audit(collection))
        profile = PROFILES[name]
        if (instant(manifest["cutoff_utc"]) != instant(profile["cutoff"])
                or manifest["dataset_version"] != profile["dataset_contract"]
                or collection.get("DataKind", "real") != manifest["data_kind"]):
            raise ValueError("Manifesto diverge do perfil de coleta")
        # Legacy exporter manifests remain identifiable by the FULL tuple.
        if manifest.get("collection_profile", name) != name:
            raise ValueError("Identificador de perfil divergente")
        if collection.get("CollectionProfile") not in (None, name):
            raise ValueError("Identificador de perfil da auditoria divergente")
        if manifest["data_kind"] == "real":
            trips = collection.get("Trips", [])
            if not trips or any(t.get("Qualidade") != "AuditadaSemProtecao"
                                or not t.get("ReferenciaAuditoria", "").strip() for t in trips):
                raise ValueError("ExecuÃ§Ãµes reais sem auditoria de procedÃªncia")
            actual = {t["viagem_id"]: (instant(t["inicio"]), instant(t["fim"])) for t in manifest["trips"]}
            audited = {t["ViagemId"]: (instant(t["Inicio"]), instant(t["Fim"])) for t in trips}
            if len(audited) != len(trips) or actual != audited:
                raise ValueError("Viagens do manifesto divergem da auditoria")
    else:
        # Legacy generated fixture is a synthetic format, never a release.
        name, profile = SYNTHETIC, PROFILES[HISTORICAL]
        if (manifest["data_kind"] != "synthetic" or manifest.get("fixture_seed") != 20261007
                or instant(manifest["cutoff_utc"]) != instant(profile["cutoff"])
                or manifest.get("collection_profile", SYNTHETIC) != SYNTHETIC):
            raise ValueError("Dataset sem perfil oficial ou fixture sintÃ©tica reconhecida")
    # Missing ID in old configs means only the historical/legacy fixture context.
    expected = HISTORICAL if name == SYNTHETIC else name
    if config.get("collection_profile", HISTORICAL) != expected:
        raise ValueError("Config e manifesto pertencem a perfis diferentes")
    return name, profile
