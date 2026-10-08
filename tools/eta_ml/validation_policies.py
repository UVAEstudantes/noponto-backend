"""Validation policy is independent of collection release and feature contract."""
LEGACY = 'eta-direct-route-comparison-3a-v1'
CURRENT = 'eta-route-versioned-3g2-v2'


def policy(manifest, config):
    actual = manifest.get('validation_policy', LEGACY)
    expected = config.get('validation_policy', LEGACY)
    if actual not in (LEGACY, CURRENT) or actual != expected:
        raise ValueError('Dataset/config validation policies differ or are unknown')
    exporter = manifest.get('exporter_version')
    if exporter is not None and exporter != ('eta-export-3g2-v2' if actual == CURRENT else 'eta-export-3b-v1'):
        raise ValueError('Exporter/validation policy mismatch')
    return actual
