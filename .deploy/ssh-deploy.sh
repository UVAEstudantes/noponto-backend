#!/usr/bin/env bash
set -euo pipefail
umask 077
[[ ${IMAGE_DIGEST:-} =~ ^sha256:[a-f0-9]{64}$ ]] || exit 2
[[ ${RELEASE_SEQUENCE:-} =~ ^[1-9][0-9]{0,19}$ ]] || exit 2
[[ ${SERVER_TAILSCALE_IP:-} =~ ^[a-zA-Z0-9][a-zA-Z0-9.-]*$ ]] || exit 2
[[ ${SERVER_USER:-} =~ ^[a-z_][a-z0-9_-]*$ ]] || exit 2
[[ ${RELEASE_COMMIT:-} =~ ^[a-f0-9]{40}$ ]] || exit 2
[[ ${NOPONTO_DEPLOY_STATE_DIR:-} =~ ^/[a-zA-Z0-9_./-]+$ ]] || exit 2
[[ -n ${SERVER_SSH_KEY:-} && -n ${SERVER_SSH_KNOWN_HOSTS:-} ]] || exit 2
private=$(mktemp -d)
trap 'rm -rf -- "$private"' EXIT
printf '%s\n' "$SERVER_SSH_KEY" > "$private/key"
printf '%s\n' "$SERVER_SSH_KNOWN_HOSTS" > "$private/known_hosts"
ssh -i "$private/key" -o BatchMode=yes -o IdentitiesOnly=yes \
    -o StrictHostKeyChecking=yes -o "UserKnownHostsFile=$private/known_hosts" \
    -o ConnectTimeout=20 -o ServerAliveInterval=15 -o ServerAliveCountMax=3 \
    "$SERVER_USER@$SERVER_TAILSCALE_IP" \
    "python3 -I - ci --image ghcr.io/guilhermedesales/noponto-api@$IMAGE_DIGEST --sequence $RELEASE_SEQUENCE --state $NOPONTO_DEPLOY_STATE_DIR --commit $RELEASE_COMMIT" \
    < <(printf '__file__="/dev/stdin"\n'; cat .deploy/eta-hybrid-release.py)
