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

`.github/workflows/deploy-staging.yml`:

- запускается после успешного `CI` в `master` или вручную;
- environment `staging`;
- использует `vars.STAGING_URL` для healthcheck;
- принимает `image_tag` и необязательный `git_ref` для ручного запуска/отката.

`.github/workflows/deploy-production.yml`:

- только вручную;
- environment `production` с required reviewer;
- требует `confirm=deploy`;
- делает pgBackRest diff backup перед деплоем;
- использует `vars.API_URL` для healthcheck;
- принимает `image_tag` и необязательный `git_ref`.

Деплой-воркфлоу должны использовать **полный** `sha-<github.sha>`, а не
короткий SHA. `deploy-local.sh` тоже переведён на полный SHA.

## Security workflows

- `.github/workflows/secrets.yml` — Gitleaks на PR, push в `master` и вручную.
  Check: `Secret Scan / Secret Scan (Gitleaks)`.
- `.github/workflows/codeql.yml` — CodeQL для C# на PR, push в `master`,
  еженедельно и вручную. Check: `CodeQL / Analyze (C#)`.
- `Dependabot alerts`, `Secret scanning` и `Push protection` включаются в
  **Settings → Code security and analysis**. Это не check runs, их нельзя
  добавлять в required status checks.

Добавляй `Secret Scan (Gitleaks)` и `Analyze (C#)` в required checks только
после первого успешного запуска — GitHub показывает в списке только checks,
которые уже запускались.

Первый полный прогон Gitleaks может найти старые секреты в истории. Правильное
лечение — ротация и очистка истории, а не allowlist реальных секретов.

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

### Переменные environment

Это не секреты, а environment variables:

| Переменная | Environment | Назначение |
|---|---|---|
| `STAGING_URL` | `staging` | базовый URL для healthcheck, например `https://staging.example.com` |
| `API_URL` | `production` | базовый URL для healthcheck, например `https://api.example.com` |

Healthcheck в workflow использует `vars.STAGING_URL` / `vars.API_URL`,
поэтому реальные домены не попадают в публичный репозиторий.

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

- run: pip install "ansible-core~=2.19.0" hvac
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

Реализован в `.github/workflows/deploy-staging.yml`.

Триггер:

- после успешного `CI` в `master` (`workflow_run`);
- вручную (`workflow_dispatch`) с обязательным `image_tag`.

Ручной запуск принимает:

- `image_tag` — только `sha-...`;
- `git_ref` — необязательный SHA/ref для checkout Ansible-файлов
  (удобно для отката на старый образ).

Что делает:

1. checkout коммита из `workflow_run` / `git_ref` / выбранной ветки;
2. проверяет, что тег начинается с `sha-`;
3. ставит Ansible, пишет SSH-ключ, inventory и `group_vars/all/main.yml`;
4. выполняет:
   ```bash
   ansible-playbook -i inventory.ini playbook.yml \
     --tags staging \
     -e "api_image=ghcr.io/skufiks/undermine-api:${IMAGE_TAG}"
   ```
5. проверяет `vars.STAGING_URL/health/ready`.

Environment: `staging`.

## Production

Реализован в `.github/workflows/deploy-production.yml`.

Только вручную, с reviewer. Дополнительно требуется ввести `confirm=deploy`.

Ручной запуск принимает:

- `image_tag` — только `sha-...`;
- `git_ref` — необязательный SHA/ref для checkout Ansible-файлов;
- `confirm` — строка `deploy`.

Что делает:

1. checkout `git_ref` / выбранной ветки;
2. проверяет `confirm` и `image_tag`;
3. ставит Ansible, пишет SSH-ключ, inventory и `group_vars/all/main.yml`;
4. делает `ansible-playbook -i inventory.ini backup.yml`
   (pgBackRest diff backup на prod-3, от имени postgres);
5. выполняет:
   ```bash
   ansible-playbook -i inventory.ini playbook.yml \
     --tags app \
     -e "api_image=ghcr.io/skufiks/undermine-api:${IMAGE_TAG}"
   ```
6. проверяет `vars.API_URL/health/ready`.

Environment: `production`.

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
- Require status checks:
  `Build and test`, `Docker image`, `Secret Scan (Gitleaks)`, `Analyze (C#)`;
- Require branches to be up to date;
- Block force pushes;
- Restrict direct pushes.

## Pre-flight checklist

- [ ] В GitHub созданы environments `staging` и `production`.
- [ ] В `production` включён required reviewer.
- [ ] Секреты `SSH_PRIVATE_KEY`, `ANSIBLE_INVENTORY`, `VAULT_ADDR`,
      `VAULT_TOKEN` добавлены в оба environment.
- [ ] В `staging` добавлена переменная `STAGING_URL`, в `production` — `API_URL`.
- [ ] Vault-токен read-only и не имеет доступа к записи.
- [ ] Проверено, что staging-деплой по SSH проходит с GitHub-hosted runner.
- [ ] В `master` включена branch protection.
- [ ] Проверен ручной откат на предыдущий `sha-`тег.
