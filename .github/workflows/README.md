# CI/CD на GitHub Actions

## Что уже работает

`.github/workflows/ci.yml`:

- запускается на `pull_request`, `push` в `master` и вручную;
- на PR: `dotnet restore/build/test`, `node --check`, Docker build **без push**;
- на `master`: то же плюс push образа в GHCR;
- теги образа:
  - `ghcr.io/skufiks/undermine-api:sha-<full-git-sha>`
  - `ghcr.io/skufiks/undermine-api:latest`
- NuGet-кэш и Docker layer cache (`type=gha`);
- образы из PR никогда не публикуются.

Деплой-воркфлоу должны использовать **полный** `sha-<github.sha>`, а не
короткий SHA. `deploy-local.sh` тоже переведён на полный SHA.

## GitHub Environments

Создать два environment:

| Environment | Настройки |
|---|---|
| `staging` | без reviewer или с optional reviewer |
| `production` | required reviewer, deployment branches: `master` |

Секреты лучше хранить в environment, а не в repository secrets.

### Обязательные секреты

Одинаковые имена в `staging` и `production`, разные значения:

| Секрет | Назначение |
|---|---|
| `SSH_PRIVATE_KEY` | приватный ключ deploy-пользователя |
| `ANSIBLE_INVENTORY` | полный `ansible/inventory.ini` с реальными хостами |
| `VAULT_ADDR` | `https://vault.example.com` |
| `VAULT_TOKEN` | read-only токен на `kv/data/undermine-api/*` |

`VAULT_TOKEN` должен уметь только читать:

```text
kv/data/undermine-api/config
kv/data/undermine-api/tokens
kv/data/undermine-api/database
kv/data/undermine-api/app
kv/data/undermine-api/app-staging
```

### Что НЕ нужно

- `ENV_PRODUCTION`, `ENV_STAGING` — Ansible сам рендерит `.env.production`
  и `.env.staging` из Vault.
- `GHCR_TOKEN` для деплоя — образ на сервере тянется с токеном из
  `kv/undermine-api/tokens → ghcr_token`.
- `ANSIBLE_GROUP_VARS` — файл генерируется в workflow из `VAULT_ADDR`
  и статических путей.

## Формат `ANSIBLE_INVENTORY`

Секрет должен содержать полноценный inventory. Минимально:

```ini
[prod_primary]
prod-1-se ansible_host=<prod-1-ip> ansible_port=2222 ansible_user=deploy

[prod_standby]
prod-2-de ansible_host=<prod-2-ip> ansible_port=2222 ansible_user=deploy

[prod_dr]
prod-3-rf ansible_host=<prod-3-ip> ansible_port=2222 ansible_user=deploy

[all:vars]
ansible_python_interpreter=/usr/bin/python3
ansible_ssh_common_args=-o StrictHostKeyChecking=accept-new

[postgres_cluster:children]
prod_primary
prod_standby
prod_dr

[garage:children]
prod_primary
prod_standby
prod_dr

[staging]
staging-1 ansible_host=<prod-2-ip> ansible_port=2222 ansible_user=deploy

[staging:vars]
hashicorp_vault_app_path=undermine-api/app-staging
```

## Общие шаги деплой-воркфлоу

```yaml
- uses: actions/checkout@v4
  with:
    ref: ${{ github.event.workflow_run.head_sha || github.sha }}

- uses: actions/setup-python@v5
  with:
    python-version: "3.12"

- run: pip install ansible-core hvac
- run: ansible-galaxy collection install -r ansible/requirements.yml

- name: Write SSH key
  run: |
    mkdir -p ~/.ssh
    printf '%s\n' "${{ secrets.SSH_PRIVATE_KEY }}" > ~/.ssh/id_ed25519
    chmod 600 ~/.ssh/id_ed25519

- name: Write inventory
  run: |
    printf '%s\n' "${{ secrets.ANSIBLE_INVENTORY }}" > ansible/inventory.ini
    chmod 600 ansible/inventory.ini

- name: Write Vault group vars
  run: |
    mkdir -p ansible/group_vars/all
    cat > ansible/group_vars/all/main.yml <<EOF
    hashicorp_vault_url: "${{ secrets.VAULT_ADDR }}"
    hashicorp_vault_token: "{{ lookup('env', 'VAULT_TOKEN') }}"
    hashicorp_vault_mount_point: kv
    hashicorp_vault_config_path: undermine-api/config
    hashicorp_vault_tokens_path: undermine-api/tokens
    hashicorp_vault_database_path: undermine-api/database
    hashicorp_vault_app_path: undermine-api/app
    EOF
```

`ansible/group_vars/all/main.yml` и `ansible/inventory.ini` есть в
`.gitignore`; они не должны попасть в коммит.

> Если `galaxy.ansible.com` падает с `KeyError: 'results'`, поставьте
> `community.hashi_vault` из GitHub по инструкции в `SECURITY.md`.

## Staging

Триггер:

- после успешного `CI` в `master` (`workflow_run`);
- вручную (`workflow_dispatch`) с обязательным `image_tag`.

```yaml
name: Deploy staging

on:
  workflow_run:
    workflows: ["CI"]
    types: [completed]
    branches: [master]
  workflow_dispatch:
    inputs:
      image_tag:
        description: "Immutable image tag, e.g. sha-<full-sha>"
        required: true
        type: string

concurrency:
  group: deploy-staging
  cancel-in-progress: true

permissions:
  contents: read

jobs:
  deploy:
    if: >
      github.event_name == 'workflow_dispatch' ||
      github.event.workflow_run.conclusion == 'success'
    runs-on: ubuntu-latest
    timeout-minutes: 20
    environment: staging
    env:
      IMAGE_TAG: >
        ${{ github.event_name == 'workflow_run'
            && format('sha-{0}', github.event.workflow_run.head_sha)
            || inputs.image_tag }}
    steps:
      # общие шаги из раздела выше
      - name: Deploy
        working-directory: ansible
        env:
          VAULT_TOKEN: ${{ secrets.VAULT_TOKEN }}
        run: |
          ansible-playbook -i inventory.ini playbook.yml \
            --tags staging \
            -e "api_image=ghcr.io/skufiks/undermine-api:${IMAGE_TAG}"

      - name: Health check
        run: curl -fsS https://staging.example.com/health/ready
```

## Production

Только вручную, с reviewer:

```yaml
name: Deploy production

on:
  workflow_dispatch:
    inputs:
      image_tag:
        description: "Immutable image tag, e.g. sha-<full-sha>"
        required: true
        type: string

concurrency:
  group: deploy-production
  cancel-in-progress: false

permissions:
  contents: read

jobs:
  deploy:
    runs-on: ubuntu-latest
    timeout-minutes: 30
    environment: production
    env:
      IMAGE_TAG: ${{ inputs.image_tag }}
    steps:
      # общие шаги из раздела выше

      - name: Backup PostgreSQL
        working-directory: ansible
        env:
          VAULT_TOKEN: ${{ secrets.VAULT_TOKEN }}
        run: |
          ansible prod_primary -b -m command \
            -a "pgbackrest --type=diff --stanza=undermine backup"

      - name: Deploy
        working-directory: ansible
        env:
          VAULT_TOKEN: ${{ secrets.VAULT_TOKEN }}
        run: |
          ansible-playbook -i inventory.ini playbook.yml \
            --tags app \
            -e "api_image=ghcr.io/skufiks/undermine-api:${IMAGE_TAG}"

      - name: Health check
        run: |
          curl -fsS https://api.example.com/health/ready
          curl -fsS https://staging.example.com/health/ready
```

`--tags app` без `-l`:

- обновляет и запускает prod-1;
- на prod-2/prod-3 скачивает образ и оставляет API остановленным
  (`app_start=false` из Vault);
- если prod-2 переведён в failover (`app_start=true`), сначала
  останавливает staging, затем запускает standby.

## Откат

1. Взять предыдущий immutable-тег `sha-<old-sha>`.
2. Запустить тот же deploy workflow с этим тегом.
3. Если миграция несовместима — восстановить БД из pgBackRest
   и только потом откатывать образ.

## Branch protection

Для `master`:

- Require a pull request before merging;
- Require status checks: `Build and test`, `Docker image`;
- Require branches to be up to date;
- Block force pushes;
- Restrict direct pushes.

## Pre-flight checklist

- [ ] В GitHub созданы environments `staging` и `production`.
- [ ] В `production` включён required reviewer.
- [ ] Секреты `SSH_PRIVATE_KEY`, `ANSIBLE_INVENTORY`, `VAULT_ADDR`,
      `VAULT_TOKEN` добавлены в оба environment.
- [ ] Vault-токен read-only и не имеет доступа к записи.
- [ ] Проверено, что staging-деплой по SSH проходит с GitHub-hosted runner.
- [ ] В `master` включена branch protection.
- [ ] Проверен ручной откат на предыдущий `sha-`тег.
