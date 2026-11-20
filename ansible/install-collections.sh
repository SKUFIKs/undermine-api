#!/usr/bin/env bash
# Installs Ansible collections. Falls back to a GitHub clone for
# community.hashi_vault when galaxy.ansible.com returns KeyError: 'results'.
set -euo pipefail

cd "$(dirname "$0")"

HASHI_VAULT_VERSION="${HASHI_VAULT_VERSION:-7.1.0}"
HASHI_VAULT_DIR="${HOME}/.ansible/collections/ansible_collections/community/hashi_vault"

if ansible-galaxy collection install -r requirements.yml; then
  exit 0
fi

echo
echo "Galaxy install failed. Installing community.hashi_vault ${HASHI_VAULT_VERSION} from GitHub..."
mkdir -p "$(dirname "$HASHI_VAULT_DIR")"
rm -rf "$HASHI_VAULT_DIR"
git clone --depth 1 --branch "$HASHI_VAULT_VERSION" \
  https://github.com/ansible-collections/community.hashi_vault.git \
  "$HASHI_VAULT_DIR"

ansible-galaxy collection list | grep -i hashi_vault
