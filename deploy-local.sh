#!/usr/bin/env bash
set -euo pipefail

GHCR_OWNER="skufiks"
IMAGE_NAME="ghcr.io/${GHCR_OWNER}/undermine-api"

GIT_SHA=$(git rev-parse HEAD)
API_IMAGE="${IMAGE_NAME}:sha-${GIT_SHA}"

echo "==> 1. Create image: ${API_IMAGE}"
docker build -t "${API_IMAGE}" .

echo "==> 2. Push image in GHCR"
docker push "${API_IMAGE}"

echo "==> 3. Start Ansible in image ${API_IMAGE}"
cd ansible
ansible-playbook -i inventory.ini playbook.yml -e "api_image=${API_IMAGE}" "$@"