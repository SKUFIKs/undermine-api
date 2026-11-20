# Безопасность репозитория и деплоя

Репозиторий публичный. Реальные IP, домены, пароли, токены и приватные ключи
не должны попадать в git — ни в текущий коммит, ни в историю.

## Что не коммитим

Эти файлы уже перечислены в `.gitignore`:

- `ansible/inventory.ini` — реальные хосты;
- `ansible/group_vars/all/main.yml` — Vault URL/пути;
- `ansible/vault.legacy.yml` — старый ansible-vault файл (источник миграции);
- `ansible/vault.config.json` — готовый config для импорта в Vault;
- `deploy/.env.production`, `deploy/.env.staging` — реальные env;
- `ansible/**/vault.yml`, `ansible/**/*.key`, `ansible/**/*.pem`, `ansible/**/*.p12`;
- vault-пароли и локальные артефакты Ansible.

Проверить перед коммитом:

```bash
git check-ignore -v \
  ansible/inventory.ini \
  ansible/group_vars/all/main.yml \
  ansible/vault.legacy.yml \
  ansible/vault.config.json \
  deploy/.env.production
```

Все команды должны вывести правило из `.gitignore`.

## HashiCorp Vault KV v2

Все переменные и секреты читаются через `community.hashi_vault.vault_kv2_get`.
Используются четыре секрета:

```text
kv/undermine-api/config    -> vars + hosts (без секретов)
kv/undermine-api/tokens    -> ghcr_token, cloudflare_token, garage_rpc_secret
kv/undermine-api/database  -> app_pass, repl_pass, staging_pass
kv/undermine-api/app       -> env приложения: JWT, admin, S3, Build, лимиты
```

Установка коллекции:

```bash
pip install hvac
ansible-galaxy collection install -r ansible/requirements.yml
```

Если Galaxy API падает с `KeyError: 'results'`, поставить коллекцию из GitHub:

```bash
git clone --depth 1 --branch 7.1.0 \
  https://github.com/ansible-collections/community.hashi_vault.git \
  ~/.ansible/collections/ansible_collections/community/hashi_vault
```

Подключение задаётся в `group_vars/all/main.yml`:

```yaml
hashicorp_vault_url: "https://vault.example.com"
hashicorp_vault_token: "{{ lookup('env', 'VAULT_TOKEN') }}"
hashicorp_vault_namespace: ""
hashicorp_vault_mount_point: kv
hashicorp_vault_config_path: undermine-api/config
hashicorp_vault_tokens_path: undermine-api/tokens
hashicorp_vault_database_path: undermine-api/database
hashicorp_vault_app_path: undermine-api/app
```

Токен берётся из окружения:

```bash
export VAULT_ADDR=https://vault.example.com
export VAULT_TOKEN=...
# export VAULT_NAMESPACE=...  # если используется namespace
```

Не коммитить токен. Не передавать его через `-e` без необходимости: он попадёт
в историю shell.

### Структура config

Пример: `ansible/hashicorp-vault.config.example.json`.

```json
{
  "vars": {
    "api_url": "...",
    "garage_rpc_public_addrs": { "prod-1-se": "..." },
    "...": "..."
  },
  "hosts": {
    "prod-1-se": {
      "garage_zone": "se",
      "app_compose_file": "docker-compose.prod.yml",
      "...": "..."
    }
  }
}
```

- `vars` — глобальные несекретные переменные;
- `hosts.<inventory_hostname>` — зона и настройки compose для хоста.

Секретов в `config` быть не должно.

### Структура tokens

Пример: `ansible/hashicorp-vault.tokens.example.json`.

```json
{
  "ghcr_token": "",
  "cloudflare_token": "CHANGE_ME",
  "garage_rpc_secret": "CHANGE_ME_64_HEX_CHARS",
  "staging_basic_auth": "CHANGE_ME_STAGING_PASSWORD"
}
```

`staging_basic_auth` — это пароль для nginx basic auth на staging, не готовый
htpasswd-хэш. Роль `nginx` сама генерирует `admin:<apr1-hash>` и пишет
`/etc/nginx/.htpasswd-staging`.

### Структура database

Пример: `ansible/hashicorp-vault.database.example.json`.

```json
{
  "app_pass": "CHANGE_ME",
  "repl_pass": "CHANGE_ME",
  "staging_pass": "CHANGE_ME"
}
```

### Структура app

Пример: `ansible/hashicorp-vault.app.example.json`.

Это плоский объект переменных окружения API:

```json
{
  "Jwt__Key": "CHANGE_ME_MIN_32_CHARS",
  "Jwt__Issuer": "UnderMine",
  "Jwt__Audience": "UnderMine",
  "S3__Endpoint": "http://127.0.0.1:3900",
  "S3__AccessKey": "CHANGE_ME_GARAGE_KEY_ID",
  "S3__SecretKey": "CHANGE_ME_GARAGE_SECRET",
  "S3__CdnBaseUrl": "https://cdn.example.com",
  "...": "..."
}
```

`app_env_vars` в `config` больше не нужен: роль читает его из
`kv/undermine-api/app`.

### Перенос текущих значений

В репозитории сгенерирован только `ansible/vault.config.json` — несекретные
переменные и настройки хостов. Файл git-ignored.

Если секрета `kv/undermine-api/config` ещё нет, загрузите его:

```bash
vault kv put kv/undermine-api/config @ansible/vault.config.json
```

Секреты `tokens`, `database` и `app` **не перезаписывайте**: у вас там уже
значения приложения. Проверить ключи можно так:

```bash
vault kv get -format=json kv/undermine-api/tokens   >/dev/null
vault kv get -format=json kv/undermine-api/database >/dev/null
vault kv get -format=json kv/undermine-api/app      >/dev/null
```

После проверки `ansible/vault.config.json` можно удалить. Старый
`ansible/vault.legacy.yml` тоже больше не нужен.

### Inventory

`inventory.ini` остаётся локальным и git-ignored. В нём только имена хостов,
группы и параметры подключения (`ansible_host`, `ansible_port`, `ansible_user`).
Ansible должен знать, куда подключаться, ещё до чтения Vault. Все остальные
переменные — в Vault.

### Резервный ansible-vault

Старый `ansible/vault.legacy.yml` остаётся только как источник миграции.
Playbook его больше не читает. `ansible/vault.example.yml` — устаревший шаблон
для справки.

## Проверка текущего дерева

Список файлов, которые попадут в следующий коммит:

```bash
git ls-files -co --exclude-standard
```

Искать в них реальные значения:

```bash
git ls-files -co --exclude-standard -z \
  | xargs -0 grep -nE \
    '([0-9]{1,3}\.){3}[0-9]{1,3}|[A-Za-z0-9_-]+\.(ru|com|click)|BEGIN .*PRIVATE KEY|Password=|Jwt__Key='
```

Если команда что-то нашла — сначала убедиться, что это placeholder
(`example.com`, `203.0.113.x`, `CHANGE_ME`), и только потом коммитить.

## История git

В истории этого репозитория уже были коммиты с реальными данными
(старые строки подключения к PostgreSQL, IP-адреса и пароли). Считать эти
данные скомпрометированными.

Порядок действий:

1. Сменить все пароли и ключи, которые могли попасть в историю:
   - PostgreSQL: приложение, репликация, staging и старые пользователи БД;
   - администратор из примеров в README (если такой аккаунт существует — удалить/сбросить);
   - JWT-ключ;
   - пароль сертификата (`DOTNET_CERT_PASSWORD`);
   - Cloudflare token, если он когда-либо был вне vault;
   - ключи MinIO/Garage, если они попадали в логи или файлы вне git.
2. Только после ротации — очистить историю (`git filter-repo` или BFG).
3. Сделать force-push и попросить всех, у кого есть клон, переклонировать.
4. Включить secret scanning в GitHub и блокировку push с секретами.

Проверить, какие коммиты содержат старые строки подключения:

```bash
git log --all --oneline -S 'Password='
git log --all --oneline -S 'ConnectionStrings__PostgreConnection='
git log --all --oneline -S 'BootstrapAdminPassword='
```

Удаление секрета из текущего файла не делает его безопасным: пока он есть в
истории, он считается утёкшим.
