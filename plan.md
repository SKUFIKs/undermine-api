
# Инфраструктура и CI/CD (рабочий план)

> Цель: поднять сервис на 3 серверах в разных локациях с полным циклом CI/CD.
> Основной режим — **active-passive**: Швеция принимает трафик, Германия — warm standby,
> РФ — cold DR/последний резерв.
>
> **Архитектура:** nginx, PostgreSQL и Garage — **нативно на хосте** (systemd/пакеты).
> Docker используется **только для API-контейнера**. Kubernetes, managed PostgreSQL,
> Tailscale и active-active не используем.

## Текущий статус (20.11.2026)

| Этап | Статус | Комментарий |
|---|---|---|
| **OPS-01** — репозиторий, env, GHCR | ✅ | Первый `sha-`образ опубликован и задеплоен; workflow собирает `sha-<commit>` и `latest` |
| **OPS-02** — Ansible: базовая настройка | ✅ | `common`/`docker`/`nginx`/`postgres`/`garage`/`app` прогнаны; Docker есть на всех трёх |
| **OPS-03** — PostgreSQL primary + replica + бэкапы | ✅ | PG18: primary prod-1, streaming replica prod-2, staging prod-2, pgBackRest на prod-3; остался тест восстановления DR |
| **OPS-04** — nginx + TLS | ✅ | vhost и TLS на prod-1/prod-2; `/builds/` проксируется в Garage; API и админка открываются через nginx |
| **OPS-05** — Garage (3-node кластер) | ✅ | 3 ноды в layout (`se`/`de`/`rf`), region `us-east-1`, ключ/bucket/website и загрузка сборок работают |
| **OPS-06** — compose + роль app | ✅ | prod-1 API работает; staging на prod-2 развёрнут, `/health/ready` = 200, bucket и nginx vhost готовы, basic auth включён |
| **OPS-07** — флаги multi-node в коде | ✅ | `WorkerEnabled`, `MigrateOnStartup`, `ManageBucket` + one-shot `--migrate-only`; тесты проходят |
| **PRE-CI** — ворота перед GitHub Actions | 🔄 | staging полностью готов (bucket, nginx basic auth, failover); остались GitHub Environments/секреты, branch protection, безопасность, проверка отката |
| **OPS-08–10** — CI/CD | 🔄 | `CI`, `deploy-staging.yml` и `deploy-production.yml` готовы; нужны Environments/секреты и первый прогон |
| **OPS-11–16** — мониторинг, runbook, безопасность | ⏳ | После CI/CD |

**Текущая точка:** prod-1 принимает трафик, API здоров, админка и загрузка сборок
работают. staging на prod-2 развёрнут, миграции применены, `/health/ready` = 200,
basic auth и реальные IP в логах проверены. prod-2/prod-3 содержат подготовленный
standby/DR образ, но production API там остановлен.
Следующий этап — **GitHub Environments, branch protection и deploy workflows**.

## Принятые решения

- **prod-1 (Швеция, `<prod-1-ip>`):** production; nginx на хосте + API-контейнер +
  build worker + PostgreSQL primary + нода Garage (zone `se`).
- **prod-2 (Германия, `<prod-2-ip>`):** warm standby; nginx на хосте + API standby
  (обычно остановлен) + PostgreSQL streaming replica + нода Garage (zone `de`).
  Здесь же staging: отдельный compose-проект API, отдельный нативный кластер PostgreSQL
  и отдельный bucket `undermine-staging-builds` на том же кластере Garage.
- **prod-3 (РФ, `<prod-3-ip>`):** cold DR / последний резерв; nginx + образ API
  (остановлен), PostgreSQL восстанавливается из бэкапа, нода Garage (zone `rf`),
  Uptime Kuma и Sentry/GlitchTip.
- **PostgreSQL:** self-hosted, нативный пакет. Primary в Швеции, асинхронная реплика
  в Германии, cold DR в РФ. Синхронную репликацию между площадками не включаем.
- **Файлы сборок:** один кластер **Garage** из трёх нод (Швеция, Германия, РФ),
  по одной на каждом сервере. `replication_factor=2`, `consistency_mode="consistent"`.
  MinIO и Cloudflare R2 в production не используем.
- **Reverse proxy:** nginx на хосте. На prod-1/prod-2 уже работает stream/SNI-шлюз
  из `deploy_ansible` (`roles/doh`). TLS — Let's Encrypt DNS-01, Cloudflare — **только DNS**.
- **Docker:** только API. Никаких контейнеров nginx/postgres/garage.
- **CI/CD:** GitHub Actions + GHCR + Ansible + Docker Compose (только для API).
- **SSH:** ключи, fail2ban, UFW. Cloudflare Tunnel/Access не используем.
- **Миграции БД:** отдельный one-shot шаг, не при старте каждой API-реплики.
- **Build worker:** только на prod-1. При failover включается на prod-2; на prod-3 —
  только если недоступны оба первых сервера.

## Правила работы

1. `plan.md` — единственный источник истины. Если архитектура меняется, сначала
   правится план, потом код/серверы.
2. Реальные IP, домены, пароли и токены не попадают в git. Они живут в
   `ansible/inventory.ini`, `ansible/group_vars/all/main.yml` и `deploy/.env.*` —
   все эти файлы в `.gitignore`.
3. `deploy_ansible` владеет `base` (SSH/UFW/fail2ban), `doh` (nginx.conf + stream/SNI)
   и `sites` (vhost'ы). Этот репозиторий владеет `common`, `docker`, `postgres`,
   `garage`, `app`. `common` идемпотентен и прогоняется на всех трёх хостах.
4. Docker — только API. Всё остальное ставится пакетами/бинарниками и управляется systemd.

## Итоговая схема

```text
                         Cloudflare DNS (без проксирования)
                                     |
                 +-------------------+-------------------+
                 |                                       |
        prod-1 (Швеция)                         prod-2 (Германия)
   nginx :443 (stream/SNI)                 nginx :443 (stream/SNI)
        |        \                              |
        |         +--> Garage s3_web :3902 <-----+ (loopback)
        |              zone se                     zone de
        |
   API-контейнер :5000
   PostgreSQL primary :5432
        |
        +---- async streaming replica ----> prod-2 PostgreSQL replica :5432
        |
        +---- Garage RPC :3901 <----------> prod-2 Garage (zone de)
                                   \-------> prod-3 Garage (zone rf)
                                     (один кластер, RF=2)

        prod-3 (РФ) — cold DR
   nginx + API image (остановлен)
   PostgreSQL restore из бэкапа (pgBackRest repo)
   Garage node zone rf + nginx для cdn
   Uptime Kuma + Sentry/GlitchTip
```

DNS-имена (пример; реальные — в Vault `kv/undermine-api/config → vars`):

- `api.example.com` — production, A на prod-1, DNS-only.
- `staging.example.com` — staging на prod-2, A на prod-2, DNS-only.
- `cdn.example.com` — nginx → локальный Garage `s3_web`; A на prod-1, при failover
  переключается на prod-2/prod-3, DNS-only.
- `status.example.com` — Uptime Kuma на prod-3, DNS-only + basic auth.

Отдельный `cdn-ru.example.com` пока не используется: данные и так лежат в РФ
на ноде Garage, а DNS при необходимости переключается на prod-3.

---

## OPS-01 · Подготовка репозитория и окружений

- [x] Создать в репозитории каталоги:

  ```text
  deploy/
    docker-compose.prod.yml
    docker-compose.staging.yml
    nginx/
      nginx.conf
      conf.d/
    .env.production.example
    .env.staging.example
  ansible/
    inventory.ini
    playbook.yml
    garage-bootstrap.yml
    roles/
      common/
      docker/
      nginx/
      postgres/
      app/
      garage/
  ```

- [x] Создать GitHub Environments:
  - `staging` — без обязательного ревью, секреты staging.
  - `production` — required reviewer, секреты production.
- [x] Создать пакет GHCR: `ghcr.io/<owner>/undermine-api`.
  - Пакет создан первым `docker push` (тег `local-test`).
  - Для релизов использовать `sha-<commit>`, `latest` — только для удобства.
- [x] Добавить `.env.production.example` с обязательными ключами:
  - `ConnectionStrings__PostgreConnection` (или `POSTGRES_*`, из которых compose её собирает);
  - `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience`;
  - `S3__Endpoint`, `S3__AccessKey`, `S3__SecretKey`, `S3__Bucket`,
    `S3__UseSsl=false`, `S3__CdnBaseUrl`, `S3__PublicRead=false`, `S3__ManageBucket=false`;
  - `S3__FallbackCdnBaseUrl`;
  - `Build__RootPath=/app/builds`, `Build__WorkerEnabled`, `Database__MigrateOnStartup`;
  - `JavaRuntime__RootPath=/app/java`;
  - `ForwardedHeaders__*`;
  - `Access__BanCacheSeconds`, `CORS__*`, `RateLimiting__*`, `Registration__*`.
- [x] Убедиться, что `.env` и `deploy/.env*` не попадают в git.

**Готово, когда:** структура каталогов создана, окружения GitHub настроены, секреты не в
репозитории.

---

## OPS-02 · Ansible: базовая настройка серверов

> **Разделение с `deploy_ansible`.** На prod-1/prod-2 уже работают роли `base`
> (SSH/UFW/fail2ban), `doh` (nginx.conf + stream/SNI + сертификаты) и `sites` (vhost'ы).
> Поэтому:
> - `common` из этого репозитория отвечает за SSH-hardening и UFW на всех трёх хостах
>   (`000-undermine-hardening.conf` должен сортироваться раньше провайдерских файлов);
> - `docker`, `postgres`, `garage`, `app` — зона ответственности этого репозитория.

- [x] Описать `ansible/inventory.ini` (реальный файл в git не попадает; в репозитории
  только `inventory.example.ini`):

  ```ini
  [prod_primary]
  prod-1-se ansible_host=<prod-1-ip> ansible_port=2222 ansible_user=deploy

  [prod_standby]
  prod-2-de ansible_host=<prod-2-ip> ansible_port=2222 ansible_user=deploy

  [prod_dr]
  prod-3-rf ansible_host=<prod-3-ip> ansible_port=2222 ansible_user=deploy
  ```

  Группы именуются по роли: `prod_primary` (Швеция), `prod_standby` (Германия),
  `prod_dr` (РФ). SSH-пользователь задаётся в inventory; роль `common` создаёт `deploy` с sudo.

- [x] Роль `common`:
  - SSH-ключи, запрет root-логина и входа по паролю;
  - `ufw`: SSH-порт, 80, 443;
  - `fail2ban`;
  - `unattended-upgrades`;
  - timezone UTC;
  - **прогнана на всех трёх хостах** (`permitrootlogin no`, `passwordauthentication no`,
    порт 2222 проверены).
- [x] Роль `docker`:
  - Docker Engine + Compose plugin;
  - `/etc/docker/daemon.json` с ограничением логов (`max-size`, `max-file`);
  - каталоги `/srv/undermine/{builds,java,nginx,certs}`.
  - Каталоги `postgres` и `garage` этой ролью **не управляются** — у них свои владельцы.
- [x] Роль `nginx`: установка nginx/certbot, vhost API на `127.0.0.1:8443`
  (`ssl proxy_protocol`), отдельный `/root/.secrets/cloudflare-api.ini`.
- [x] Роль `postgres`: установка пакета, перенос данных в `/srv/undermine/postgres`,
  пользователи, `pg_hba`, репликация (детали в OPS-03).
- [x] Роль `garage`: systemd-сервис, бинарник, `garage.toml`, loopback S3/s3_web,
  RPC только между нодами, автоматическое соединение через `bootstrap_peers`
  (детали в OPS-05). Одноразовый layout/key/bucket — в `garage-bootstrap.yml`.
- [ ] Роль `app`: копирование compose и `.env`, `docker compose pull/up` (детали в OPS-06).
- [ ] Проверить: `ansible-playbook ... --check`, затем боевой прогон:
  - docker-роль на `prod-2`, `prod-1`, `prod-3`;
  - `nginx`-роль на `prod-1` (на prod-2 уже проверена);
  - `garage`-роль уже прогнана на всех трёх; bootstrap — после открытия 3901.

**Готово, когда:** на всех трёх серверах есть Docker, каталоги, SSH по ключам;
`common`/`postgres`/`garage` применены на всех хостах, `nginx` — на prod-1/prod-2.

---

## OPS-03 · PostgreSQL: primary + replica + бэкапы

**Выбрано: нативный PostgreSQL (пакет), не Docker.**

> **Фактическое состояние (18.11.2026):** PostgreSQL **18**. Primary на prod-1,
> streaming replica на prod-2 (через SSH-туннель `127.0.0.1:15432`), staging-кластер
> на prod-2, pgBackRest 2.60.1 с full backup и WAL archive на prod-3.
> Осталось: тест восстановления DR.

### prod-1: primary

- [x] Установить PostgreSQL пакетом; данные — `/srv/undermine/postgres`.
- [x] Настроить `postgresql.conf`:
  - `listen_addresses = 'localhost, <prod-1-ip>'`;
  - `wal_level = replica`;
  - `max_wal_senders = 10`;
  - `max_replication_slots = 10`;
  - `wal_keep_size = 512MB`;
  - `ssl = on`;
  - `archive_mode = on` + `archive_command` для pgBackRest.
- [x] Пользователи:
  - `undermine_app` — только БД приложения;
  - `undermine_repl` — только репликация.
- [x] `pg_hba.conf`:
  - приложение — только `127.0.0.1/32` (API-контейнер с `network_mode: host`);
  - реплика — только `127.0.0.1/32` (через SSH-туннель).
- [x] UFW: публичный 5432 не нужен, репликация идёт через SSH-туннель.
- [x] Проверить: `sudo -u postgres psql`, `\du`, `\l`.

> **Репликация через SSH-туннель.** Провайдер сбрасывает PostgreSQL-трафик
> на публичном `5432` (UFW пакеты пропускает, но соединение закрывается до
> обработки сервером). Поэтому на prod-2 работает systemd-сервис `pg-tunnel`:
> `127.0.0.1:15432` → `prod-1:127.0.0.1:5432`. Публичный 5432 можно закрыть в UFW.

### prod-2: streaming replica

- [x] Установить PostgreSQL 18 пакетом; остановить кластер.
- [x] Сделать базовый backup через туннель:

  ```bash
  pg_basebackup -h 127.0.0.1 -p 15432 -U undermine_repl \
    -D /srv/undermine/postgres -Fp -Xs -P -R
  ```

- [x] Проверить `primary_conninfo`, `standby.signal`, `hot_standby=on`, запуск.
- [x] Проверить отставание:

  ```sql
  SELECT client_addr, state, sent_lsn, replay_lsn,
         pg_wal_lsn_diff(sent_lsn, replay_lsn) AS lag_bytes
  FROM pg_stat_replication;
  ```

- [x] Репликация **асинхронная**. Синхронную между Швецией и Германией не включать.
- [ ] При падении prod-1: `pg_ctl promote` на prod-2. После promote API на prod-2
  переключается на локальную БД.

### prod-3: cold DR

- [x] Постоянная реплика на первом этапе не нужна.
- [x] Хранить на prod-3 актуальные pgBackRest-бэкапы (`/srv/undermine/backups`).
- [ ] При катастрофе: восстановить БД из бэкапа, поднять API и nginx, переключить DNS.
- [ ] Позже при желании добавить третью асинхронную реплику.

### Staging (нативно, на prod-2)

- [x] Создать второй кластер PostgreSQL 18 на prod-2:

  ```bash
  sudo pg_createcluster 18 staging --port 5433 \
    --datadir /srv/undermine/postgres-staging
  ```

- [x] Отдельная БД `undermine_staging` и пользователь `undermine_staging_app`.
- [x] Лимиты CPU/RAM через systemd (`CPUQuota`, `MemoryMax`) для `postgresql@18-staging`.
- [x] `pg_hba`: только `127.0.0.1/32`.
- [ ] На время failover staging останавливается.
- [x] Production-БД со staging не связывать.

### Бэкапы

- [x] Установить pgBackRest 2.60.1.
- [x] Ежедневный full/diff backup + WAL archive на prod-3 (`/srv/undermine/backups`).
- [ ] При желании — дополнительная копия в R2/другое offsite-хранилище.
- [x] Хранить 7–14 дней (или больше по месту).
- [ ] Раз в месяц делать тестовое восстановление в отдельную БД.
- [ ] Дополнительно `pg_dump` перед каждым production-деплоем.

**Готово, когда:** replica догоняет primary, `pg_stat_replication` показывает `streaming`,
бэкап восстанавливается на тестовом инстансе.

---

## OPS-04 · nginx + TLS

> **Фактическая схема (проект `deploy_ansible`).** На prod-1/prod-2 уже работает
> stream/SNI-шлюз (`roles/doh/templates/nginx.conf.j2`):
> `stream { listen 443; }` по SNI отправляет `*.<zone>` на
> `127.0.0.1:10443` → `127.0.0.1:8443` с PROXY protocol.
> Поэтому vhost'ы API обязаны слушать `127.0.0.1:8443 ssl proxy_protocol`, а не 443.
> Cloudflare — только DNS; TLS — Let's Encrypt DNS-01, без Origin Certificate.
> Роль `nginx` не трогает `/etc/nginx/nginx.conf` — им владеет `doh`.
> Для API-сертификата используется отдельный `/root/.secrets/cloudflare-api.ini`,
> чтобы не перезаписывать рабочий `/root/.secrets/cloudflare.ini` (`docs.example.com`).

- [x] На prod-2: vhost API, сертификат `api.example.com` (SAN: staging, cdn),
  `nginx -t` проходит.
- [x] На prod-1: тот же vhost, сертификат, `nginx -t`.
- [x] На prod-2: отдельный staging vhost (`staging.conf.j2`), `/health` и
  `/builds/` публичные, `/api/` и `/admin/` закрыты до включения basic auth.
- [ ] nginx проксирует:
  - `/api/` → API;
  - `/admin/` → API (статика админки отдаётся API);
  - `/health` → API;
  - `/builds/` → локальный Garage `s3_web` (`proxy_pass http://127.0.0.1:3902/`)
    с `Host: <bucket>.cdn.example.com`.
- [ ] `client_max_body_size` — не меньше лимита загрузки сборок/модпаков.
- [ ] `proxy_read_timeout` / `proxy_send_timeout` — 600s для долгих операций.
- [ ] Передавать `X-Forwarded-For`, `X-Forwarded-Proto`, `Host`.
- [ ] Реальный IP:
  - `real_ip_header proxy_protocol`;
  - `set_real_ip_from 127.0.0.1`;
  - в API `ForwardedHeaders__KnownNetworks=127.0.0.1/32`.
- [ ] TLS:
  - Let's Encrypt DNS-01 через Cloudflare API;
  - сертификат на `api_url` с SAN `staging_url`, `cdn_url`;
  - `certbot renew --dry-run` проходит.
- [ ] DNS-only:
  - `api.example.com` — A на prod-1;
  - `staging.example.com` — A на prod-2;
  - `cdn.example.com` — A на prod-1; при failover переключается на prod-2/prod-3;
  - Cloudflare Access **не используем** (нет проксирования). Админку и staging
    защищаем на nginx: basic auth или `allow`/`deny` по IP.
- [ ] Защита `/admin/`:
  - staging — обязательно basic auth или IP allowlist;
  - production — basic auth/IP allowlist до появления отдельного VPN.
- [ ] Проверить: `curl -I https://api.example.com`, `/health/ready`, вход в `/admin/`.

**Готово, когда:** HTTPS работает на prod-1/prod-2, `nginx -t` и `certbot renew --dry-run`
проходят, `/admin` закрыт basic auth/IP allowlist, в логах реальные IP.

---

## OPS-05 · Garage: 3-node кластер объектного хранилища

MinIO не используем. Файлы сборок хранит распределённый кластер **Garage** из трёх
нод (Швеция, Германия, РФ), по одной на каждом сервере. Это не per-site репликация,
а один кластер с `replication_factor = 2` и `consistency_mode = "consistent"`.

### Архитектура

- 3 ноды: `prod-1` (zone `se`), `prod-2` (zone `de`), `prod-3` (zone `rf`).
- `replication_factor = 2`: каждый объект лежит на двух нодах из трёх.
- S3 API — `127.0.0.1:3900`, только loopback (API-контейнер).
- `s3_web` — `127.0.0.1:3902`, только loopback (nginx).
- RPC — `*:3901`, доступен только с IP двух других нод (UFW).
- Наружу 3900/3902 не открываются. Публичная отдача — только через nginx.
- Данные: `/srv/undermine/garage/data`, метаданные: `/var/lib/garage/meta`.

### Роль `garage`

- [x] Системный пользователь `garage`, каталоги, бинарник v2.5.0, systemd-сервис.
- [x] `garage.toml` из шаблона: `replication_factor`, `consistency_mode`,
  S3/s3_web на loopback, `rpc_public_addr` из group_vars.
- [x] `bootstrap_peers` собирается из node ID остальных нод; ноды соединяются сами.
- [x] UFW: 3901 только с IP двух других нод.
- [x] Открыть 3901 на стороне провайдера для RU-сервера (выполнено).

### Bootstrap (один раз)

Отдельный `garage-bootstrap.yml`, не часть обычного деплоя:

```bash
ansible-playbook -i inventory.ini garage-bootstrap.yml
```

- проверяет, что `garage status` видит все 3 ноды;
- `layout assign` по зонам `se`/`de`/`rf` и `layout apply --version 1`;
- создаёт ключ `undermine-api`, если его ещё нет;
- создаёт bucket `undermine-builds`, выдаёт read/write, включает website;
- сохраняет ключ в `/etc/garage/undermine-api.key` (root-only, 0600).

`garage key create` не идемпотентен: секрет показывается один раз. Файл ключа нельзя
терять; при потере — удалить ключ, создать новый и заново выдать права на bucket.

### Bucket и публичный доступ

- Bucket `undermine-builds`.
- Публичная отдача — Garage `s3_web` + `garage bucket website --allow`.
- nginx проксирует `cdn.example.com` на `127.0.0.1:3902` и подставляет
  `Host: undermine-builds.cdn.example.com` (bucket host).
- Отдельный `cdn-ru.example.com` пока не нужен: кластер уже хранит данные в РФ,
  а DNS при failover переключается на prod-2/prod-3.
- Staging: отдельный bucket `undermine-staging-builds` на том же кластере,
  без второго инстанса Garage.

### Env для API на каждом сервере

```env
S3__Endpoint=http://127.0.0.1:3900
S3__UseSsl=false
S3__Bucket=undermine-builds
S3__AccessKey=<Key ID из /etc/garage/undermine-api.key>
S3__SecretKey=<Secret key из /etc/garage/undermine-api.key>
S3__CdnBaseUrl=https://cdn.example.com
S3__FallbackCdnBaseUrl=
S3__PublicRead=false
S3__ManageBucket=false
```

`PublicRead=false` и `ManageBucket=false`: bucket, права и website создаёт
`garage-bootstrap.yml`, API только читает/пишет объекты. Публичный доступ даёт
`s3_web`, а не bucket policy.

### Проверка

- [x] `garage status` на prod-1 показывает 3 ноды, `layout show` — `se`/`de`/`rf`.
- [x] smoke-загрузка объекта через S3 API на prod-1;
- [x] объект доступен на всех нодах (повторное чтение через кластер);
- [x] `https://cdn.example.com/builds/...` открывается без авторизации;
- [ ] лаунчер скачивает и проверяет SHA-256.

**Готово, когда:** 3 ноды в layout, `replication_factor=2`, ключ и bucket созданы,
`cdn.example.com` отдаёт файлы через nginx, R2 и MinIO не используются.

---

## OPS-06 · Docker Compose только для API + роль app

- [x] Создать `deploy/docker-compose.prod.yml`:
  - единственный сервис `api`;
  - образ из GHCR (`API_IMAGE=ghcr.io/<owner>/undermine-api:sha-<commit>`);
  - `env_file: .env.production`;
  - `network_mode: host`;
  - `ASPNETCORE_URLS=http://127.0.0.1:5000`;
  - `ForwardedHeaders__KnownNetworks=127.0.0.1/32`;
  - volumes `/srv/undermine/builds:/app/builds`, `/srv/undermine/java:/app/java`;
  - healthcheck `/health/ready`;
  - `restart: unless-stopped`;
  - логи `max-size=10m`, `max-file=3`.
  - **Никаких** сервисов nginx/postgres/garage.
- [x] На prod-2 (warm standby):
  - тот же образ;
  - `Build__WorkerEnabled=false`;
  - `Database__MigrateOnStartup=false`;
  - API остановлен в обычном режиме, включается при failover.
- [ ] Staging на prod-2:
  - отдельный compose-проект `undermine-staging`;
  - отдельный `.env.staging`;
  - API-контейнер `network_mode: host`, `127.0.0.1:5000` (standby в это время остановлен);
  - PostgreSQL `127.0.0.1:5433`;
  - bucket `undermine-staging-builds` на том же кластере Garage;
  - лимиты CPU/RAM.
- [x] На prod-3 (cold DR):
  - nginx + образ API подготовлены, API остановлен;
  - `Build__WorkerEnabled=false`, `Database__MigrateOnStartup=false`;
  - локальная нода Garage (zone `rf`) + nginx для `cdn`.
- [x] Роль `app`:
  - проверить `api_image` (обязательно `sha-` или digest);
  - скопировать compose и `.env.production` в `/srv/undermine/` (env 0600);
  - `docker pull` под deploy-пользователем;
  - one-shot миграции: `docker run ... --migrate-only`;
  - `docker compose up -d --remove-orphans` на prod-1;
  - на standby/DR — только подготовка и `down`, без запуска;
  - проверка `/health/ready`.
- [x] Проверить `docker compose -f deploy/docker-compose.prod.yml config`.

**Готово, когда:** `docker compose up -d` на prod-1 поднимает API, `/health/ready` зелёный,
админка открывается через nginx. Осталось прогнать роль с реальным GHCR-образом.

---

## OPS-07 · Изменения в коде для multi-node

> Флаги `Build__WorkerEnabled`, `Database__MigrateOnStartup` и `S3__ManageBucket`
> реализованы и читаются кодом. Миграции можно запускать отдельно командой
> `--migrate-only`, не поднимая API.

- [x] **Build worker flag**
  - `Build:WorkerEnabled` (default `true`);
  - в `Program.cs` регистрировать `BuildWorkerService` только если включено;
  - на prod-1 `true`, на prod-2 `false`, на prod-3 `false` (включается при DR).
- [x] **Миграции отдельным шагом**
  - `Database:MigrateOnStartup` (default `true`);
  - на API-репликах `false`;
  - one-shot запуск: `dotnet UnderMineApi.dll --migrate-only`
    (или `docker compose run --rm api --migrate-only`) перед деплоем.
- [x] **S3 bucket management**
  - `S3:ManageBucket` (default `false`);
  - bucket, права и website создаёт `garage-bootstrap.yml`;
  - в API всегда `false`, автосоздание bucket в Garage не нужно.
- [ ] **Sentry**
  - добавить `Sentry.AspNetCore`;
  - DSN из env;
  - включать только в Production;
  - не отправлять секреты (scrub).
- [ ] **Health/readiness**
  - `/health/ready` проверяет БД;
  - `/health/live` не зависит от внешних сервисов;
  - проверить S3 health.

**Готово, когда:** на prod-2/prod-3 API не запускает worker и не делает миграции;
миграции выполняются ровно один раз на prod-1.

---

## PRE-CI · Ворота перед GitHub Actions

> Цель: к моменту написания workflow деплой уже должен один раз работать
> **вручную**. Иначе GitHub Actions будет автоматизировать непроверенный процесс.

### 1. Пароль PostgreSQL и Vault

- [x] `POSTGRES_PASSWORD` в `deploy/.env.production` совпадает с `app_pass`
      из `kv/undermine-api/database` — API успешно подключается к БД.
- [x] Пароли применены; репликация работает.
- [x] `pg_stat_replication` на prod-1 показывает `streaming`, лаг в норме.
- [x] `vault_app_pass`, `vault_repl_pass`, `vault_staging_pass`,
      `cloudflare_token`, `garage_rpc_secret` используются из Vault.
- [ ] Для CI создан отдельный read-only Vault token с политикой
      `undermine-api` (не личный и не root).

### 2. Первый immutable-образ

- [x] Workflow `CI` собирает `ghcr.io/<owner>/undermine-api:sha-<full-sha>`
      и `latest` (PR — без push; `master` — с push).
- [x] Первый образ `sha-9c6119b` запушен в GHCR и задеплоен.
- [x] Пакет GHCR доступен deploy-пользователю.
- [ ] Тег и digest первого образа записаны для отката.

### 3. Первый ручной деплой prod-1

- [x] Выполнен `ansible-playbook ... --tags app -e api_image=...`.
- [x] На prod-1 `docker compose ps` показывает `api` запущенным.
- [x] `/health/live` и `/health/ready` возвращают 200.
- [x] В `docker logs undermine-api-prod` нет ошибок миграций, БД и worker.
- [x] Админка открывается через nginx, вход работает.
- [x] Загружена тестовая сборка, файлы выгружаются в Garage.
- [x] На prod-2 и prod-3 образ скачан, но API остановлен.
- [ ] Проверены команды stop/start, повторный `--tags app` идемпотентен.

### 4. Staging на prod-2

- [x] Добавить в inventory отдельный host-alias `staging-1`, указывающий на
      prod-2 (`ansible_host` = IP prod-2). Это нужно, чтобы факты staging
      не пересекались с фактами warm standby на том же сервере.
- [x] В `kv/undermine-api/config` → `hosts.staging-1` добавить:
      - `garage_zone: de` (или не использовать Garage-роль для staging);
      - `app_dir: /srv/undermine/staging`;
      - `app_compose_file: docker-compose.staging.yml`;
      - `app_env_file: .env.staging`;
      - `app_env_name: .env.staging`;
      - `app_project_name: undermine-staging`;
      - `app_compose_profiles: []`;
      - `app_start: true`;
      - `app_run_migrations: true`;
      - `app_worker_enabled: true`;
      - `app_health_url: http://127.0.0.1:5000/health/ready`;
      - `app_db_name: undermine_staging`;
      - `app_user: undermine_staging_app`;
      - `pg_host: 127.0.0.1`;
      - `pg_port: 5433`.
- [x] В `playbook.yml` добавить play `hosts: staging`, `tags: [staging]`,
      `roles: [app]`.
- [x] Заполнить `deploy/.env.staging`:
      - `POSTGRES_HOST=127.0.0.1`, `POSTGRES_PORT=5433`;
      - отдельная БД/пользователь staging;
      - JWT, admin, S3 `undermine-staging-builds`, CDN staging;
      - `Kestrel__Endpoints__Http__Url=http://127.0.0.1:5000`.
- [x] Создать staging bucket и выдать ключу права (тот же Garage-кластер):
      `ansible-playbook -i inventory.ini garage-staging-bucket.yml`.
      Проверено: bucket `undermine-staging-builds`, website access = true,
      ключ `undermine-api` имеет RWO.
- [x] Добавить nginx vhost для staging на prod-2:
      `ansible/roles/nginx/templates/staging.conf.j2`.
      `/health` и `/builds/` публичные, `/api/` и `/admin/` по умолчанию
      возвращают 403.
- [x] Включить basic auth для staging.
- [x] Выполнить ручной деплой:
      `ansible-playbook -i inventory.ini playbook.yml --tags staging -e api_image=ghcr.io/<owner>/undermine-api:sha-<commit>`.
- [x] Проверить `/health/ready` = 200.
- [x] Проверить вход в staging-админку напрямую (`127.0.0.1:5000`):
      логин `<staging-admin>`, ответ 200, JWT выдан.
      Через nginx с basic auth `/admin/` отдаёт 200.
- [x] При failover staging останавливается, чтобы не занимать `127.0.0.1:5000`:
      play `Stop staging before activating the standby API` в `playbook.yml`.
      Запускается при `app_start=true` для prod-2.

> Пароль staging-БД (`staging_pass`) берётся из `kv/undermine-api/database`.
> Роль `vault_secrets` автоматически подставляет его в `app_db_password` для
> группы `staging`, поэтому дублировать пароль в `hosts.staging-1` не нужно.
> Ключ `hosts.staging-1.app_db_password` можно удалить: там лежит литерал
> `{{ vault_staging_pass }}`, который Ansible не темплейтит повторно.
> Если staging-пользователь уже существовал со старым паролем, применить его
> можно так: `ansible-playbook -i inventory.ini playbook.yml --tags postgres -l prod_standby`.

> Basic auth для staging:
> 1. В Vault `kv/undermine-api/tokens` → `staging_basic_auth` лежит **пароль**
>    (не htpasswd-строка).
> 2. В `kv/undermine-api/config` → `vars` выставлено
>    `staging_basic_auth_enabled: true`.
> 3. Роль `nginx` сама генерирует `admin:<apr1-hash>` через `openssl passwd`
>    и пишет `/etc/nginx/.htpasswd-staging`.
> 4. Применить: `ansible-playbook -i inventory.ini playbook.yml --tags nginx -l prod_standby`.
> 5. Проверить: `curl -u admin https://staging.example.com/admin/`.
>
> Basic auth стоит **только на `/admin/`**. На `/api/` его нельзя ставить:
> SPA отправляет `Authorization: Bearer <JWT>`, и этот заголовок перекрывает
> Basic-креды браузера. Иначе получается бесконечный запрос логина/пароля.
> `/api/` защищён JWT приложения; при необходимости его можно дополнительно
> ограничить через `vars.staging_ip_allowlist` (например, `["203.0.113.10/32"]`).

> PROXY protocol починен: nginx access log показывает реальные IP клиентов.
> IP allowlist для staging можно включить через `vars.staging_ip_allowlist`;
> он применяется к `/api/` и как альтернатива Basic для `/admin/`.

> `hosts.staging-1.app_env_overrides.S3__CdnBaseUrl` исправлен на
> `https://staging.example.com`.

> Важно: staging и standby используют один порт `5000`. Штатно работает staging,
> при failover staging останавливается и запускается standby.

### 5. GitHub Environments и секреты

- [ ] Созданы GitHub Environments `staging` и `production`.
- [ ] В `production` включён required reviewer.
- [ ] Добавлены секреты:
      - `SSH_PRIVATE_KEY` — ключ deploy-пользователя;
      - `ANSIBLE_INVENTORY` — полный реальный inventory;
      - `VAULT_ADDR`, `VAULT_TOKEN` — read-only токен на `kv/data/undermine-api/*`;
      - при необходимости `TELEGRAM_*` для уведомлений.
- [ ] `ENV_PRODUCTION`/`ENV_STAGING` **не нужны**: Ansible рендерит
      `.env.production` и `.env.staging` из Vault.
- [ ] `GHCR_TOKEN` для деплоя **не нужен**: сервер тянет образ с токеном
      из `kv/undermine-api/tokens → ghcr_token`.
- [ ] Решено, откуда запускать Ansible:
      - GitHub-hosted runner по SSH (нужен доступ к 2222 с динамических IP), или
      - self-hosted runner на prod-3 (меньше сетевых исключений).
- [ ] Workflow генерирует `ansible/inventory.ini` и
      `ansible/group_vars/all/main.yml` из секретов; эти файлы не коммитятся.
- [ ] В `master` включена branch protection: обязательный CI, запрет прямого push.

> Полный список секретов, шаги workflow и blueprint деплоя:
> `.github/workflows/README.md`.

### 6. Безопасность

- [ ] Старые утёкшие пароли/ключи из истории git ротированы.
- [ ] История очищена (`git filter-repo`/BFG) или репозиторий пересоздан.
- [x] Добавлены workflow `secrets.yml` (Gitleaks) и `codeql.yml` (CodeQL C#).
- [x] CodeQL findings исправлены: 0 результатов в `csharp-queries` и
      `csharp-security-extended`.
- [ ] В GitHub включены secret scanning и push protection
      (Settings → Code security and analysis).
- [ ] `ansible/vault.legacy.yml`, `vault.config.json`, `deploy/.env.*` и
      `ansible/inventory.ini` отсутствуют в индексе git.

### 7. Критерий готовности к OPS-08

- [ ] Ручной деплой prod-1 повторяем и заканчивается зелёным `/health/ready`.
- [ ] Ручной деплой staging повторяем.
- [ ] Ручной откат на предыдущий `api_image` проверен.
- [ ] Все секреты доступны через GitHub Environments.
- [ ] Зафиксированы точные команды: deploy, migrate, rollback, stop/start.

**CI уже написан. Дальше — `deploy-staging.yml` и `deploy-production.yml`
по blueprint'у из `.github/workflows/README.md`.**

---

## OPS-08 · CI (GitHub Actions)

> Начинать после **PRE-CI**: образ уже должен собираться и деплоиться вручную.

- [x] Workflow `CI` на `pull_request`, `push` в `master` и `workflow_dispatch`:
  - PR: `dotnet restore/build/test`, `node --check`, Docker build без push;
  - `master`: push `sha-<full-sha>` и `latest` в GHCR;
  - `GITHUB_TOKEN` с `packages: write` только в docker-job.
- [x] Разделить CI на `pull_request` и `push` в `master`.
- [x] NuGet-кэш через `actions/setup-dotnet` и Docker cache `type=gha`.
- [x] Не пушить образы из PR.
- [x] Проверить, что PR не проходит при падении тестов (78 тестов локально).
- [ ] Добавить branch protection: CI обязателен для merge.

**Готово, когда:** PR зелёный, в GHCR появился образ с SHA-тегом,
а `master` защищён обязательным CI.

> Blueprint деплой-воркфлоу и список секретов:
> `.github/workflows/README.md`.

---

## OPS-09 · CD staging

> Запускать после **PRE-CI** (staging уже развёрнут вручную) и **OPS-08**.

- [x] `.github/workflows/deploy-staging.yml`:
  - запуск после успешного CI в `master` (`workflow_run`) или вручную;
  - GitHub Environment `staging`;
  - деплой на prod-2 через Ansible/SSH, в отдельный compose-проект.
- [x] Шаги:
  1. checkout коммита из `workflow_run` / `git_ref` / выбранной ветки;
  2. установить Ansible + коллекции;
  3. записать `inventory.ini` и `group_vars/all/main.yml` из секретов;
  4. `ansible-playbook -i inventory.ini playbook.yml --tags staging -e api_image=...`;
  5. healthcheck `vars.STAGING_URL/health/ready`.
- [x] Staging БД — отдельный нативный кластер на prod-2 (`127.0.0.1:5433`).
- [x] Staging закрыт basic auth на nginx (`/api/`, `/admin/`);
      `/health` и `/builds/` публичные.
- [x] На время failover staging останавливается, чтобы не мешать standby.
- [x] Импорт CurseForge-модпака на staging (1.21.1, NeoForge 21.1.262):
      сборка завершена, файлы загружены в Garage.
- [x] Исправлен обрезанный `S3__SecretKey` в `kv/undermine-api/app-staging`:
      было 59 символов вместо 64, из-за чего Garage отвечал
      `403 Forbidden: Invalid signature`. После исправления в staging-бакете
      4127 объектов, сборка `bd93166e...` завершилась успешно.

**Готово, когда:** merge в `master` автоматически обновляет staging,
`/health/ready` зелёный.

---

## OPS-10 · CD production

> Запускать после **PRE-CI** (ручной откат проверен) и **OPS-09**.

- [x] `.github/workflows/deploy-production.yml`:
  - только вручную;
  - GitHub Environment `production` с required reviewer;
  - обязательный `confirm=deploy`;
  - секреты production.
- [x] Порядок:
  1. `ansible-playbook -i inventory.ini backup.yml`
     (pgBackRest diff backup на prod-3, от имени postgres);
  2. `ansible-playbook -i inventory.ini playbook.yml --tags app -e api_image=...`;
  3. app-роль сама делает one-shot миграции на prod-1 и
     `docker compose up -d --force-recreate` с healthcheck;
  4. на prod-2/prod-3 образ обновляется, API остаётся остановленным;
  5. smoke-тест `vars.API_URL/health/ready`.
- [ ] Откат: вернуть предыдущий `API_IMAGE`; БД — из бэкапа, если миграция
      несовместима.
- [ ] Уведомление в Telegram/Discord об успехе/ошибке.
- [ ] Хранить историю тегов и релизов.

**Готово, когда:** тестовое изменение проходит staging → production,
откат проверен.

---

## OPS-11 · Мониторинг и ошибки

- [ ] **Uptime Kuma** на prod-3 (РФ), чтобы мониторинг пережил падение prod-1/prod-2:
  - мониторинг `https://api.example.com/health/ready`;
  - мониторинг `/health/live`;
  - мониторинг админки;
  - TCP-мониторинг PostgreSQL (через локальный/туннельный адрес);
  - уведомления в Telegram.
- [ ] **Sentry** (SaaS) или **GlitchTip** на prod-3:
  - DSN в env;
  - релиз = git SHA;
  - алерты на email/Telegram.
- [ ] Логи:
  - Serilog уже пишет файлы;
  - добавить ротацию/retention;
  - позже — Grafana Loki/Grafana Cloud.
- [ ] Проверить: остановить API → приходит алерт; сгенерировать тестовую ошибку →
  событие в Sentry.

**Готово, когда:** падение API и ошибка приложения видны без ручного просмотра логов.

---

## OPS-12 · Failover runbook (active-passive)

- [ ] Обнаружение: Uptime Kuma / Sentry / ручное.
- [ ] Проверить prod-1 (сеть, диск, API, БД).
- [ ] Если prod-1 недоступен:
  1. promote реплику на prod-2:
     ```bash
     pg_ctl promote -D /srv/undermine/postgres
     ```
  2. проверить, что БД принимает записи;
  3. остановить staging на prod-2;
  4. включить `Build__WorkerEnabled=true` и `Database__MigrateOnStartup=false` на prod-2;
  5. запустить API на prod-2;
  6. переключить DNS на prod-2;
  7. проверить `/health/ready`, вход в админку, запуск клиента.
- [ ] Если **prod-2 тоже недоступен** (последний резерв, prod-3 РФ):
  1. восстановить PostgreSQL на prod-3 из свежайшего бэкапа;
  2. поднять API и nginx на prod-3;
  3. включить worker;
  4. переключить DNS на prod-3;
  5. проверить `/health/ready`, админку, скачивание файлов из Garage через nginx.
- [ ] Если prod-1 вернулся:
  - не включать сразу в трафик;
  - пересобрать как реплику с текущего primary;
  - проверить lag;
  - вернуть трафик, когда всё стабильно.
- [ ] Провести учения на staging: имитировать падение prod-1, затем prod-2.
- [ ] Записать RTO/RPO.

**Готово, когда:** есть письменный runbook и хотя бы одно успешное учение.

---

## OPS-13 · Безопасность (чек-лист)

- [ ] SSH только по ключам, root login запрещён.
- [ ] fail2ban + UFW.
- [ ] Наружу только SSH, 80, 443. 5000/5432/3900/3902 — только loopback;
      3901 — только между нодами Garage.
- [ ] `/admin` и staging закрыты basic auth/IP allowlist на nginx.
- [ ] Секреты только в env/GitHub Environments/HashiCorp Vault, не в git.
- [x] Все переменные деплоя и секреты вынесены в HashiCorp Vault KV v2.
- [x] Inventory содержит только имена хостов, группы и параметры подключения.
- [ ] В истории git нет реальных секретов; старые утёкшие пароли/ключи ротированы.
- [ ] JWT key ≥ 32 символа, ротация.
- [ ] Отдельные DB-пользователи для приложения и репликации.
- [ ] Garage key с минимальными правами (read/write только на bucket);
      admin/root-ключ API не используется.
- [ ] Docker log rotation.
- [ ] Автоматические security updates.
- [ ] Бэкапы БД и данных Garage/offsite, проверка восстановления.
- [ ] Sentry не получает секреты/токены (scrub).

---

## OPS-14 · Критерии готовности

- [ ] CI на PR: build + tests + JS check.
- [ ] Образ в GHCR с immutable SHA-тегом.
- [ ] Staging обновляется автоматически.
- [ ] Production деплоится с ручным подтверждением.
- [ ] `api.example.com` работает: DNS-only → nginx → TLS → API.
- [ ] Админка доступна, файлы сборок отдаются из Garage через nginx.
- [ ] PostgreSQL primary (Швеция) + async replica (Германия), бэкапы локально/offsite.
- [ ] Cold DR на prod-3 восстановлен из локального бэкапа хотя бы один раз.
- [ ] Garage: 3 ноды в layout (`se`/`de`/`rf`), `replication_factor=2`;
      объект, записанный на prod-1, доступен на prod-2 и prod-3.
- [ ] Миграции выполняются один раз.
- [ ] Build worker только на prod-1; на prod-2 включается при failover.
- [ ] Uptime Kuma и Sentry присылают алерты.
- [ ] Есть runbook failover (Швеция → Германия) и DR (Германия → РФ).
- [ ] Тестовое восстановление БД успешно.
- [ ] Docker используется только для API.

---

## OPS-15 · Что можно делать позже (не сейчас)

- [ ] Cloudflare Load Balancer вместо ручного переключения DNS.
- [ ] Active-active API на 3 серверах.
- [ ] Patroni/etcd для автоматического failover PostgreSQL.
- [ ] Grafana + Prometheus + Loki для метрик и логов.
- [ ] Kubernetes/k3s + Argo CD, если появится реальная потребность.
- [ ] Перенос staging на отдельный домен/кластер.
- [ ] Выделенный Garage-кластер для staging, если понадобится изоляция.

---

## OPS-16 · Доставка файлов в РФ без зависимости от Cloudflare

Файлы сборок не идут через Cloudflare: DNS-only, nginx на хосте, объекты в Garage.
Кластер Garage держит по две реплики каждого объекта, включая ноду в РФ,
поэтому отдельный `cdn-ru` на первом этапе не нужен.

- [ ] **Не отдавать файлы сборок через Cloudflare.**
- [ ] `cdn.example.com` — DNS-only, A на prod-1; nginx → локальный Garage `s3_web`.
- [ ] При failover DNS переключается на prod-2 или prod-3; nginx там уже настроен.
- [ ] TLS на nginx (Let's Encrypt DNS-01), кэш и `Range` для больших файлов.
- [ ] `replication_factor=2` гарантирует объект на двух нодах из трёх. Если нода,
  на которую указывает DNS, недоступна — DNS переключается на живую.
- [ ] В манифест добавить fallback-URL (опционально):

  ```json
  {
    "path": "mods/example.jar",
    "url": "https://cdn.example.com/builds/<id>/mods/example.jar",
    "fallbackUrl": null
  }
  ```

  - `BuildManifestService` заполняет `url` из `S3:CdnBaseUrl`;
  - `fallbackUrl` — из `S3:FallbackCdnBaseUrl`, если задан;
  - лаунчер при ошибке/таймауте пробует `fallbackUrl`;
  - SHA-256 проверяется независимо от источника.
- [ ] Если понадобится отдельный домен для РФ — добавить `cdn-ru.example.com`
  (DNS-only на prod-3) и заполнить `S3:FallbackCdnBaseUrl`. Сейчас не делаем.
- [ ] Проверить из РФ: скачивание с `cdn.example.com`, скорость и стабильность.
- [ ] Проверить API: если Cloudflare блокируется, login/манифест тоже недоступны.
  При необходимости — `api-ru.example.com` (DNS-only на prod-3)
  и fallback API URL в лаунчере.
- [ ] Мониторить CDN-домен и состояние кластера Garage отдельно.

**Готово, когда:** клиент в РФ скачивает файлы с `cdn.example.com` через nginx и
Garage без Cloudflare, лаунчер проверяет SHA-256.

---

## Рекомендуемый порядок выполнения

1. **PRE-CI** — синхронизировать пароль PostgreSQL, собрать первый `sha-`образ,
   вручную задеплоить API на prod-1, проверить health/админку/CDN.
2. **PRE-CI (staging)** — заполнить `.env.staging`, вручную поднять staging,
   закрыть его basic auth/IP allowlist.
3. **PRE-CI (GitHub)** — создать Environments `staging`/`production`, секреты,
   выбрать runner и подготовить генерацию `.env.*` в workflow.
4. **PRE-CI (security)** — ротировать старые утёкшие секреты, очистить историю,
   включить secret scanning.
5. **OPS-08** — PR-CI и защита `master`.
6. **OPS-09** — CD staging.
7. **OPS-10** — CD production + откат.
8. **OPS-11** — мониторинг.
9. **OPS-12** — failover Швеция → Германия.
10. **OPS-03 (cold DR)** + учение DR (Германия → РФ).
11. **OPS-13 / OPS-14** — безопасность и приёмка.

Начинать с GitHub Actions не нужно: сначала ручной деплой должен быть скучным и
повторяемым. Workflow лишь автоматизирует уже проверенные команды.
