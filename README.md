# UnderMine API

Бэкенд игровой платформы **UnderMine**: аутентификация игроков, каталог игровых серверов,
админ-панель и **система автоматической сборки клиентов Minecraft** с раздачей файлов
через S3/MinIO/CDN.

Помимо обычного CRUD, проект умеет **сам собирать готовый игровой клиент**: скачать
ванильный Minecraft, поставить Forge/NeoForge/Fabric, наложить пресет с модами или импортировать
целый модпак (Modrinth `.mrpack` / CurseForge `.zip`), захешировать всё, разложить в S3 и
выдать лаунчеру манифест для синхронизации.

---

## Содержание

- [Возможности](#возможности)
- [Архитектура](#архитектура)
- [Технологии](#технологии)
- [Требования](#требования)
- [Быстрый старт](#быстрый-старт)
- [Первый вход в админку](#первый-вход-в-админку)
- [Конфигурация](#конфигурация)
- [Структура проекта](#структура-проекта)
- [API: общие правила](#api-общие-правила)
- [API: эндпоинты](#api-эндпоинты)
- [Сервисы](#сервисы)
- [Система сборок](#система-сборок)
- [Импорт модпаков](#импорт-модпаков)
- [Манифест и синхронизация с клиентом](#манифест-и-синхронизация-с-клиентом)
- [Админ-панель](#админ-панель)
- [База данных](#база-данных)
- [Логирование](#логирование)
- [Безопасность](#безопасность)
- [Миграции](#миграции)
- [Docker](#docker)
- [CI/CD](#cicd)
- [Разработка](#разработка)
- [Тесты](#тесты)
- [Известные ограничения](#известные-ограничения)

---

## Возможности

### Аккаунты и аутентификация

- Регистрация игровых аккаунтов с настраиваемыми лимитами на устройство и подсеть (по умолчанию 2 и 4).
- Вход с выдачей **JWT** (HS256, срок 30 дней) и проверкой бана перед выдачей токена.
- Пароли хранятся только в виде **BCrypt**-хешей.
- Роли `admin` / `moderator` / `user` и настраиваемые права для неадминистративных ролей.
  Роль попадает в токен, проверяется на админ-эндпоинтах и инвалидирует старый JWT при смене.
- Хранение статистики игрока: время игры, убийства, смерти, offline-UUID.
- История IP-адресов регистрации/входов (до 50 адресов на пользователя).

### Каталог серверов

- Регистрация серверов с версией игры, загрузчиком и версией загрузчика.
- Публичный манифест клиента для каждого сервера (см. [привязку сборок](#привязка-сборки-ксерверу)).
- Кеширование каталога в памяти и (опционально) в Redis с автоматическим обновлением.

### Система сборок

- Создание сборки: ванильный клиент + загрузчик (Vanilla / Fabric / Forge / NeoForge).
- **Пресеты сборок** — переиспользуемые наборы модов/шейдеров/ресурспаков из Modrinth или
  загруженных вручную файлов.
- **Импорт модпаков** форматов Modrinth (`.mrpack`) и CurseForge (`.zip`).
- Фоновая обработка очередью с живым прогрессом, логами и автоповторами.
- Хеширование SHA-256, дедупликация и выгрузка в S3/MinIO, генерация клиентского манифеста.
- Привязка готовой сборки к серверу: автоматически (по версиям) или жёстко (по `buildId`).

### Администрирование

- Веб-панель (SPA) на `/admin/`: дашборд, серверы, сборки, пресеты, пользователи,
  **Activity** и настройки.
- Роли и права: `admin` всегда имеет все права; для `moderator` и `user` набор из 9 прав
  настраивается в **Settings → Roles & access**. Интерфейс скрывает недоступные разделы
  и действия.
- Управление пользователями: бан/разбан, смена роли и пароля, удаление аккаунта.
- Бан требует письменную причину и доказательства (1–5 файлов/скриншотов), поддерживает
  дату и время окончания, быстрые пресеты и постоянную блокировку. Доказательства
  хранятся в PostgreSQL и доступны только через авторизованный API.
- История банов с причинами, сроками и вложениями показывается в статистике пользователя.
- **Reports** — краш-репорты лаунчера и Minecraft: фильтры по игроку/устройству/статусу,
  просмотр метаданных, скачивание логов, статусы (`new` / `investigating` / `resolved`)
  и заметки администратора. Отчёт принимается только с согласия игрока.
- **Activity** — журнал действий администраторов: фильтры по администратору, действию,
  периоду и тексту, пагинация, сводка и топ действий/администраторов.
- Режим технических работ с блокировкой публичного API.
- Аудит действий администраторов в отдельной таблице `admin_audits`.

---

## Архитектура

```
                          ┌──────────────────────────────────┐
   Лаунчер / Игрок ──────▶│  nginx  :8080  (CDN + CORS)     │
       (файлы)           │  проксирует /builds/* → MinIO   │
                          └────────────────┬─────────────────┘
                                           │ s3://minecraft-builds/builds/{buildId}/...
                                           ▼
                          ┌──────────────────────────────────┐
                          │  MinIO  :9000  (S3-совместимое) │
                          └──────────────────────────────────┘

   Клиент ──HTTP──▶ ┌────────────────────────────────────────────┐
   Админка ──HTTP──▶│  API  ASP.NET Core 9   :5000               │
                    │                                            │
                    │  Controllers → Middleware → Services        │
                    │       │                        │            │
                    │       │                        ▼            │
                    │       │             BuildWorkerService      │
                    │       │             (BackgroundService)     │
                    │       ▼                        │            │
                    │  EF Core 9 ──▶ PostgreSQL :5432 ◀─┘        │
                    │                                            │
                    │  Внешние: Mojang · Modrinth · CurseForge    │
                    └────────────────────────────────────────────┘
```

**Путь запроса:** `SerilogRequestLogging` → `RequestLoggingMiddleware` →
`ExceptionHandler` → `/health` → `ForwardedHeaders` → `CORS` → `Authentication` →
`RateLimiter` → `ApiLockMiddleware` (maintenance) → `Authorization` → `Controllers` →
статика (`/admin/`).

Поток сборки: админ создаёт `build_instances` со статусом `pending` → фоновый
`BuildWorkerService` забирает её → готовит движок и контент → хеширует и выкладывает в
MinIO → сохраняет `manifest_json` и переводит статус в `completed`.

---

## Технологии

| Компонент | Технология |
|---|---|
| Фреймворк | **.NET 9 / ASP.NET Core 9** |
| ORM | **Entity Framework Core 9** + Npgsql (PostgreSQL) |
| БД | **PostgreSQL** |
| Файловое хранилище | **MinIO** (S3-совместимое) через `Minio` 6.0.3 |
| CDN | **nginx** (кеш, CORS, стриминг `Range`) |
| Аутентификация | **JWT Bearer** (HS256), BCrypt.Net-Next |
| Кеш | **Redis** (опционально, StackExchange.Redis) |
| Логирование | **Serilog** (консоль + файл) |
| Версионирование API | `Asp.Versioning.Mvc` |
| Конфигурация | `DotNetEnv` (`.env`) + переменные окружения |
| Health checks | `AspNetCore.HealthChecks.NpgSql` |

---

## Требования

- **.NET 9 SDK** — для локальной разработки: <https://dotnet.microsoft.com/download/dotnet/9.0>
- **PostgreSQL 14+**
- **Docker + Docker Compose** — для запуска полного стека
- **Java (JRE) внутри контейнера API** — уже ставится в образ (`temurin-21-jre`) для
  headless-установщиков Forge/NeoForge. Локально нужен свой JDK, если собираете сборки.

---

## Быстрый старт

### Вариант 1 — Docker Compose (рекомендуется)

```bash
git clone https://github.com/SKUFIKs/apiundermine.git
cd apiundermine

# Создать .env из шаблона и заполнить значения
copy .env.example .env      # Windows
# cp .env.example .env      # Linux/macOS

docker compose up -d --build
```

Открытые порты:

| Сервис | Порт | Доступ | Назначение |
|---|---|---|---|
| nginx (CDN + API) | `8080` | публичный | REST API, админ-панель, файлы сборок |
| API | `5000` | только `127.0.0.1` | прямой доступ для отладки |
| MinIO API | `9000` | только `127.0.0.1` | S3-совместимое хранилище |
| MinIO Console | `9001` | только `127.0.0.1` | Веб-консоль MinIO |
| PostgreSQL | `5432` | только `127.0.0.1` | База данных |

Проверка:

```bash
curl http://localhost:8080/health          # OK  (nginx)
curl http://localhost:5000/health/ready    # Healthy (API + PostgreSQL, только с хоста)
curl http://localhost:5000/health          # Healthy (API + PostgreSQL + MinIO)
curl http://localhost:8080/api/v1.0/servers   # API через nginx (нужен Bearer-токен)
```

> **Прокси и IP-адреса клиентов.** Наружу смотрит только nginx. Он добавляет
> `X-Forwarded-For`/`X-Forwarded-Proto`, а API доверяет этим заголовкам **только** из подсети
> `PROXY_SUBNET` (по умолчанию `172.28.0.0/24`, сеть `frontend` в compose). Поэтому подделать
> IP через заголовок нельзя, и лимиты регистрации/рейт-лимит считаются по реальному адресу.
> Если подсеть занята на хосте — поменяйте `PROXY_SUBNET` в `.env`.
>
> Для bare-metal запуска без nginx оставьте `ForwardedHeaders__KnownNetworks` и
> `ForwardedHeaders__KnownProxies` пустыми — тогда используются безопасные значения по
> умолчанию (только loopback).

> **Важно про `POSTGRES_HOST`.** Внутри docker-compose сервис БД доступен по DNS-имени
> `postgres`, а не `localhost` (внутри контейнера `localhost` — это сам контейнер).
> Поэтому в `.env` должно быть `POSTGRES_HOST = postgres`. Значение `localhost` подходит
> только если вы запускаете API локально через `dotnet run`.

### Вариант 2 — Локально

```bash
# 1. Поднять только инфраструктуру
docker compose up -d postgres minio nginx

# 2. Собрать и запустить API
dotnet restore
dotnet build
dotnet run --project UnderMineApi/UnderMineApi.csproj
```

В Development доступен Swagger UI: `http://localhost:5000/swagger` (в Production он не
подключается).

При первом старте приложение само применит миграции (`Database.Migrate()`), создаст
таблицы и заведёт админ-аккаунт (см. следующий раздел).

---

## Первый вход в админку

На свежей базе, где **нет ни одного администратора**, API при старте сам создаёт
админ-аккаунт со **случайным логином и паролем** и печатает их в лог:

```
======================== DEFAULT ADMIN CREATED ========================
 Login:    admin_<8 hex>
 Password: <generated-password>
 Sign in to the admin panel with these credentials. They are shown only
 once - store them now. Lost? set BootstrapAdminUsername and
 BootstrapAdminPassword in .env and restart to reset the account.
=====================================================================
```

Как посмотреть:

```bash
docker compose logs api
# или
docker logs undermine-api-api-1
```

Дальше — `http://localhost:8080/admin/` (публичный вход через nginx) или
`http://localhost:5000/admin/` (напрямую с хоста) и вход этими кредыми.

### Поведение

| Ситуация | Что произойдёт |
|---|---|
| Админов в базе нет | Создаётся `admin_<8 hex>` со случайным паролем из 24 символов; креды печатаются в лог |
| Админ уже есть | Ничего не создаётся, в лог: `Admin account already exists, skipping bootstrap` |
| Заданы `BootstrapAdminUsername` **и** `BootstrapAdminPassword` | Создаётся этот аккаунт; если он уже есть — его пароль и роль **перезаписываются**. Пароль должен быть 8–72 байта |
| Задан только один из двух параметров | Предупреждение в лог, работает случайная генерация |
| `BootstrapAdminEnabled=false` | Ничего не делается |

Пароль генерируется криптостойким ГПСЧ (`RandomNumberGenerator`), гарантированно содержит
строчную и заглавную буквы, цифру и спецсимвол, перемешан алгоритмом Фишера–Йетса.
Алфавит намеренно **не содержит** `$` (символ интерполяции docker compose), кавычки,
обратный слэш и неоднозначные `l`/`1`/`O`/`0`, чтобы пароль можно было без проблем
скопировать из лога или вписать в `.env`.

**Потеряли пароль?** Задайте в `.env` оба параметра и перезапустите стек — аккаунт
пересоздастся:

```env
BootstrapAdminUsername=admin_1a2b3c4d
BootstrapAdminPassword=CHANGE_ME_MIN_8_CHARS
```

> Случайный режим никогда не трогает уже существующие аккаунты: сервис срабатывает
> только когда администраторов нет вообще.

---

## Конфигурация

Настройки задаются через `.env` в корне репозитория (шаблон — `.env.example`),
через переменные окружения или `appsettings.json`. Приоритет:
**`.env` → переменные окружения → `appsettings.json`**.

> В `.env` используются **имена для docker compose** (`POSTGRES_HOST`, `S3__Endpoint`,
> `CurseForgeApiKey`), а в контейнер они пробрасываются уже как имена конфигурации ASP.NET
> (`ConnectionStrings__PostgreConnection`, `S3__Endpoint`, `CurseForge__ApiKey`).
> Не путайте эти два уровня.

### База данных

| В `.env` | В конфигурации приложения | Описание | По умолчанию |
|---|---|---|---|
| `POSTGRES_HOST` | — | Хост БД (в Docker — `postgres`) | — |
| `POSTGRES_PORT` | — | Порт БД | — |
| `POSTGRES_DB` | — | Имя базы | — |
| `POSTGRES_USER` | — | Пользователь | — |
| `Database__MigrateRetries` | `Database:MigrateRetries` | Попыток применить миграции при старте | `10` |
| `Database__MigrateRetryDelaySeconds` | `Database:MigrateRetryDelaySeconds` | Пауза между попытками (сек) | `2` |
| `POSTGRES_PASSWORD` | — | Пароль | — |
| — | `ConnectionStrings__PostgreConnection` | Строка подключения (собирается из перечисленных) | — |

### JWT

| Параметр | Описание | По умолчанию |
|---|---|---|
| `Jwt__Key` | Секрет подписи HMAC-SHA256. **Минимум 32 символа**, иначе приложение не стартует | — |
| `Jwt__Issuer` | Издатель токена | `ApiUnderMine` |
| `Jwt__Audience` | Аудитория токена | `ApiUnderMine` |

### Хранилище файлов (S3 / MinIO)

| В `.env` | Описание | Пример |
|---|---|---|
| `S3__Endpoint` | Endpoint S3-совместимого хранилища | `minio:9000` |
| `S3__AccessKey` | Ключ доступа | `minioadmin` |
| `S3__SecretKey` | Секрет доступа | `minioadmin` |
| `S3__Bucket` | Имя бакета | `minecraft-builds` |
| `S3__UseSsl` | Использовать HTTPS к хранилищу | `false` |
| `S3__CdnBaseUrl` | Публичный базовый URL CDN **без имени бакета** (URL файлов: `<cdn>/builds/<buildId>/...`) | `http://localhost:8080` |
| `S3__PublicRead` | Публичное чтение объектов (бакет делается read-only для всех) | `true` |
| `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD` | Учётные данные сервиса MinIO | `minioadmin` |

### Сборки и модпаки

| Параметр | Описание | По умолчанию |
|---|---|---|
| `Build__RootPath` | Рабочая директория сборок внутри контейнера | `/app/builds` |
| `Build__LogRetentionDays` | Удалять логи сборок старше N дней (`0` — не удалять) | `30` |
| `Build__LogRetentionIntervalHours` | Интервал фоновой очистки логов | `24` |
| `Build__LogRetentionMaxPerBuild` | Максимум строк лога на сборку (`0` — без лимита) | `0` |
| `CurseForge__ApiKey` | Ключ CurseForge API (см. [импорт модпаков](#импорт-модпаков)) | — |

### Java runtime

| Параметр | Описание | По умолчанию |
|---|---|---|
| `JavaRuntime__RootPath` | Директория кэша JRE (в Docker — `/app/java`) | `java` |
| `JavaRuntime__CacheHours` | Сколько часов считать кэш свежим (`0` — проверять каждый раз) | `24` |
| `JavaRuntime__AdoptiumApiBaseUrl` | База Adoptium API | `https://api.adoptium.net` |
| `JavaRuntime__MinMajor` / `MaxMajor` | Допустимый диапазон версий Java | `8` / `64` |
| `JavaRuntime__MaxArchiveBytes` | Максимальный размер архива JRE | `262144000` |

Кэш JRE вынесен в Docker volume `java_data`; при первом запросе API скачивает Temurin
с Adoptium, проверяет SHA-256 и отдаёт архив лаунчеру.

### Первый админ

| Параметр | Описание | По умолчанию |
|---|---|---|
| `BootstrapAdminEnabled` | Создавать админа при старте, если админов нет | `true` |
| `BootstrapAdminUsername` | Фиксированный логин (перезаписывает пароль и роль) | — |
| `BootstrapAdminPassword` | Фиксированный пароль | — |

### Регистрация

| Параметр | Описание | По умолчанию |
|---|---|---|
| `Registration__MaxAccountsPerDevice` | Аккаунтов на один `deviceID` | `2` |
| `Registration__MaxAccountsPerIp` | Аккаунтов на одну подсеть | `4` |
| `Registration__Ipv4PrefixLength` | Длина префикса IPv4 (`32` — точный адрес, `24` — весь блок) | `32` |
| `Registration__Ipv6PrefixLength` | Длина префикса IPv6 | `64` |
| `Registration__DeviceIdMinLength` / `MaxLength` | Допустимая длина `deviceID` | `16` / `128` |

### Rate limiting

| Параметр | Описание | По умолчанию |
|---|---|---|
| `RateLimiting__Api__PermitLimit` / `WindowSeconds` | Обычные `/api`-запросы | `120` / `60` |
| `RateLimiting__Admin__PermitLimit` / `WindowSeconds` | Админка и poll-запросы | `600` / `60` |
| `RateLimiting__Auth__PermitLimit` / `WindowSeconds` | Login и validate-token | `20` / `60` |
| `RateLimiting__Register__PermitLimit` / `WindowSeconds` | Создание аккаунта | `10` / `600` |
| `RateLimiting__Reports__PermitLimit` / `WindowSeconds` | Загрузка краш-репортов | `10` / `600` |
| `Reports__RetentionDays` | Хранить краш-репорты N дней (`0` — не удалять) | `90` |
| `Reports__RetentionIntervalHours` | Интервал фоновой очистки отчётов | `24` |
| `RateLimiting__Static__PermitLimit` / `WindowSeconds` | Статика и `/health` | `1000` / `10` |

### Прочее

| Параметр | Описание | По умолчанию |
|---|---|---|
| `Redis__Host` / `Redis__Port` / `Redis__Username` / `Redis__Password` / `Redis__Ssl` | Подключение к Redis для распределённого кеша | не задан |
| `CORS__AllowedOrigins` | Разрешённые браузерные origins через запятую. Пусто в Production — same-origin | — / любые в Development |
| `CORS__AllowCredentials` | Разрешать cookies в CORS (Bearer-токенам не нужно) | `false` |
| `Access__BanCacheSeconds` | Сколько секунд кэшировать состояние бана/роли пользователя | `30` |
| `Security__HstsMaxAgeDays` | `max-age` для HSTS при HTTPS | `365` |
| `ForwardedHeaders__Enabled` | Обрабатывать `X-Forwarded-For` / `X-Forwarded-Proto` | `true` |
| `ForwardedHeaders__ForwardLimit` | Сколько хопов прокси доверять | `1` |
| `ForwardedHeaders__KnownProxies` | IP прокси через запятую (если задан — список по умолчанию заменяется) | loopback |
| `ForwardedHeaders__KnownNetworks` | CIDR прокси через запятую (например `172.28.0.0/24`) | loopback |
| `PROXY_SUBNET` | Подсеть `frontend`-сети compose; она же попадает в `KnownNetworks` | `172.28.0.0/24` |
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Production` | `Production` |
| `ASPNETCORE_HTTP_PORTS` / `ASPNETCORE_HTTPS_PORTS` | Порты приложения | `8080` / `8081` |
| `Logging__LogLevel__Default` | Уровень логирования | `Information` |

---

## Структура проекта

```
apiundermine/
├── docker-compose.yml              # postgres + minio + nginx + api
├── docker-compose.tls.yml          # опциональный HTTPS-профиль
├── nginx.conf                      # CDN-прокси на MinIO с кешем и CORS
├── nginx.tls.conf.example          # шаблон nginx с TLS
├── Dockerfile                      # multi-stage образ + temurin-21-jre
├── .env / .env.example             # конфигурация (в .gitignore)
│
├── BUILD_SERVICE.md                # подробно про систему сборок
├── CLIENT_SYNC.md                  # подробно про синхронизацию клиента
├── manifest.example.json           # пример клиентского манифеста
│
├── UnderMineApi/                   # основной проект (ASP.NET Core 9)
│   ├── Program.cs                  # точка входа, DI, middleware-пайплайн
│   ├── UnderMineApi.csproj
│   ├── appsettings.json            # базовая конфигурация без секретов
│   │
│   ├── Controllers/                # HTTP-слой
│   │   ├── AuthController.cs               # register / login / validate-token
│   │   ├── UserController.cs               # профиль, статистика, public-key
│   │   ├── ServersController.cs            # каталог серверов + манифест сервера
│   │   ├── VersionsController.cs           # версии Minecraft и загрузчиков
│   │   ├── BuildsController.cs             # CRUD сборок, логи, манифест сборки
│   │   ├── AdminController.cs              # пользователи, баны, серверы, maintenance
│   │   ├── AdminAccessController.cs        # /admin/me и права ролей
│   │   ├── AdminActivityController.cs      # журнал действий Activity
│   │   ├── AdminBuildEditorController.cs   # файловый редактор сборок
│   │   ├── AdminModsController.cs          # моды пресетов, поиск в Modrinth
│   │   ├── AdminPresetsController.cs       # CRUD пресетов сборок
│   │   ├── AdminModpackController.cs       # импорт модпаков (.mrpack / CurseForge)
│   │   └── DebugController.cs              # диагностика IP/заголовков (admin)
│   │
│   ├── Services/                   # бизнес-логика
│   │   ├── AdminBootstrapService.cs       # создание первого админа
│   │   ├── JwtService.cs                  # выпуск и проверка JWT
│   │   ├── UserService.cs / ServerService.cs
│   │   ├── PermissionService.cs           # права ролей и кеш
│   │   ├── BanEvidenceService.cs          # валидация доказательств бана
│   │   ├── UserAccessService.cs           # снапшот бана/роли для middleware
│   │   ├── BuildEditorService.cs          # редактирование файлов сборки
│   │   ├── ConfigCacheService.cs          # кеш серверов и флага maintenance
│   │   ├── ConfigUpdateService.cs         # фоновое обновление кеша
│   │   ├── AdminAuditService.cs           # аудит действий и лента Activity
│   │   ├── GetUuid.cs                     # offline-UUID по логину
│   │   ├── PasswordHasher/                # BCrypt
│   │   │
│   │   ├── BuildWorkerService.cs          # очередь сборок, ретраи, статусы
│   │   ├── BuildWorkerHelpers.cs          # применение пресета к сборке
│   │   ├── BuildManifestService.cs        # хеширование, S3, манифест
│   │   ├── BuildProgressReporter.cs       # живой прогресс и логи
│   │   ├── EngineDownloadService.cs       # Minecraft + Forge/NeoForge/Fabric
│   │   ├── S3StorageService.cs            # S3/MinIO с дедупликацией
│   │   ├── ModpackImportService.cs        # разбор .mrpack и CurseForge manifest
│   │   ├── ModrinthService.cs             # поиск и скачивание модов
│   │   ├── CurseForgeService.cs           # резолв projectID/fileID через CF API
│   │   ├── CustomArtifactService.cs       # загрузка своих jar/zip
│   │   ├── BuildLinkService.cs            # привязка сборок к серверам
│   │   ├── VersionCatalogService.cs       # версии Minecraft и загрузчиков
│   │   └── Interfaces/                    # IUserService, IServerService
│   │
│   ├── Middleware/
│   │   ├── RequestLoggingMiddleware.cs    # request id, логирование запросов
│   │   ├── GlobalExceptionHandler.cs      # единый формат ошибок 500
│   │   ├── AccountBanMiddleware.cs        # бан/роль/token_version на каждом запросе
│   │   ├── SecurityHeadersMiddleware.cs   # CSP, HSTS, frame/nosniff
│   │   └── ApiLockMiddleware.cs           # блокировка API при maintenance
│   │
│   ├── Domain/Entities/             # EF-сущности доменной модели
│   │   ├── UserAccount.cs                   # user_accounts
│   │   ├── UserIpHistory.cs                 # user_ip_history
│   │   ├── RolePermission.cs                # role_permissions
│   │   ├── BuildInstance.cs                 # build_instances + build_files
│   │   ├── BuildLog.cs                      # build_logs
│   │   └── BuildPreset.cs                   # build_presets + build_preset_mods
│   │
│   ├── Models/                      # модели предметной области
│   │   ├── UserData.cs                      # user_data (статистика)
│   │   ├── UserBans.cs                      # user_bans
│   │   ├── UserBanEvidence.cs               # user_ban_evidence
│   │   ├── ServerManifest.cs                # server_manifests
│   │   ├── ServerConfigDetails.cs           # server_configs
│   │   ├── GlobalSettings.cs                # global_settings
│   │   ├── AdminAudit.cs                    # admin_audits
│   │   └── NewsPart.cs
│   │
│   ├── DTOs/                        # контракты запросов/ответов
│   │   ├── AuthDtos.cs               # регистрация, вход, валидация токена
│   │   ├── AdminDto.cs               # бан, серверы, статистика, роли
│   │   ├── AdminActivityDto.cs       # журнал Activity и сводка
│   │   ├── ServerDTO.cs              # публичный список серверов
│   │   ├── BuildDto.cs               # сборки, логи, клиентский манифест
│   │   ├── BuildManifestDto.cs       # манифест сервера + политики синхронизации
│   │   ├── ModpackDto.cs             # дескрипторы импорта модпаков
│   │   ├── ModrinthDto.cs            # ответы Modrinth API
│   │   ├── ModsDto.cs                # моды пресетов
│   │   ├── PresetDto.cs              # пресеты сборок
│   │   └── VersionDto.cs             # версии Minecraft и загрузчиков
│   │
│   ├── Extensions/
│   │   ├── StructuredLoggerExtensions.cs
│   │   └── ServiceResultExtensions.cs
│   │
│   ├── Data/
│   │   └── AppDbContext.cs                  # DbContext и конфигурация модели
│   │
│   ├── Migrations/                  # миграции EF Core
│   └── wwwroot/admin/               # админ-панель (SPA без сборки)
│       ├── index.html, app.js, styles.css
│
└── ManifestGenerator/               # CLI-утилита: манифест из готовой папки
```

---

## API: общие правила

| Параметр | Значение |
|---|---|
| Базовый URL (docker, публичный) | `http://localhost:8080` |
| Базовый URL (docker, только с хоста) | `http://localhost:5000` |
| Префикс | `/api/v1.0` |
| Аутентификация | `Authorization: Bearer <jwt>` |
| Формат | JSON, `camelCase`; загрузка файлов — `multipart/form-data` |

### Аутентификация

Токен содержит `sub` (id пользователя), `UserId`, имя пользователя, роль
(`admin`/`moderator`/`user`), `token_version` и `jti`. Срок жизни — **30 дней**. Проверяются
подпись, издатель, аудитория и срок; допустимое отклонение часов — ноль.

`token_version` позволяет отзывать сессии: смена или сброс пароля инкрементирует версию
в БД, после чего старые токены получают `401 TOKEN_REVOKED` (нужен повторный вход).
Токены, выданные до появления этого поля, считаются версией `0` и остаются валидными,
пока пароль не менялся.

**Подпись — не единственная проверка.** На каждом запросе к эндпоинту с `[Authorize]`
`AccountBanMiddleware` сверяет состояние аккаунта в БД:

| Ситуация | Ответ |
|---|---|
| Аккаунт забанен | `403 ACCOUNT_BANNED` (+ `reason`, `expires_at`) |
| Аккаунт удалён | `401 USER_NOT_FOUND` |
| Роль в БД не совпадает с ролью в токене | `401 ROLE_CHANGED` (нужен повторный вход) |
| Всё в порядке | запрос передаётся дальше |

Результат кэшируется на `Access__BanCacheSeconds` (по умолчанию 30 секунд), поэтому
бан/разбан и смена роли применяются максимум через полминуты даже к уже выданным токенам.
Публичные эндпоинты (`/login`, `/register`, `[AllowAnonymous]`-манифесты) не блокируются.

### Роли и права

Административная часть работает не только по роли `admin`, но и по отдельным правам.
Роль `admin` неявно имеет все права; для `moderator` и `user` набор хранится в таблице
`role_permissions` и редактируется в **Settings → Roles & access**.

| Право | Что разрешает | По умолчанию у `moderator` |
|---|---|---|
| `servers.view` | Список и детали серверов в админке | да |
| `servers.create` | Создание серверов | да |
| `servers.edit` | Редактирование и удаление серверов | да |
| `builds.upload` | Сборки, импорт модпаков, редактор файлов, логи | да |
| `presets.create` | Пресеты и их моды | да |
| `users.ban` | Список пользователей, бан/разбан, доказательства | да |
| `users.change_password` | Сброс пароля пользователя | нет |
| `users.change_role` | Смена роли пользователя | нет |
| `activity.view` | Журнал Activity | нет |
| `reports.view` | Просмотр краш-репортов и скачивание логов | да |
| `reports.manage` | Смена статуса/заметки и удаление отчётов | да |

Дополнительно:

* неадминистратор не может управлять аккаунтом с более высокой ролью;
* нельзя назначить роль выше собственной (назначать `admin` может только `admin`);
* неадминистратор не может менять собственную роль;
* роль `user` по умолчанию не имеет прав; администратор может выдать ей любые права,
  включая доступ к отдельным разделам админ-панели;
* изменение роли инвалидирует старый JWT (`401 ROLE_CHANGED`), изменение набора прав
  применяется в течение ~15 секунд на каждой реплике API.

Интерфейс админ-панели скрывает разделы и действия, на которые у текущей роли нет прав.

### Ограничение частоты запросов

Политики разделены по типу нагрузки. Авторизованные запросы считаются по `user id`,
анонимные — по нормализованной подсети (IPv6 группируется по `/64`).

| Политика | Где применяется | По умолчанию | Ключ |
|---|---|---|---|
| `api` | обычные `/api`-эндпоинты (пользователь, серверы, публичные манифесты) | 120 / 60 с | user id, иначе подсеть |
| `admin` | админка, сборки, версии, пресеты, импорт модпаков | 600 / 60 с | user id |
| `auth` | `login`, `validate-token` | 20 / 60 с | подсеть |
| `register` | `POST /register` | 10 / 600 с | подсеть |
| `static` | статика админки, `/health` и прочее вне `/api` | 1000 / 10 с | общий ключ |
| `reports` | загрузка краш-репортов (`POST /api/v1.0/reports`) | 10 / 600 с | user id, иначе подсеть |

При превышении — **HTTP 429**, заголовок `Retry-After` и JSON:

```json
{ "status": 429, "error": "RATE_LIMITED", "message": "Too many requests. Please retry later.",
  "retry_after": 12, "request_id": "…" }
```

Лимиты настраиваются через `RateLimiting__*` (см. раздел «Конфигурация»).

### Режим технических работ

Если в `global_settings` включён `Maintenance`, все запросы к `/api` кроме
админ-авторизованных возвращают **HTTP 503**:

```json
{ "status": 503, "error": "MAINTENANCE",
  "message": "Server is under maintenance. Please try again later.", "request_id": "…" }
```

Администраторы блокировку обходят — иначе нельзя было бы её выключить. Статика админ-панели
и `/health` не затрагиваются. Флаг применяется сразу: его изменение обновляет кеш
конфигурации, который читает этот middleware.

### Ошибки

Все ошибки приводятся к одному конверту (MVC result filter + JWT challenge/forbid +
maintenance + глобальный обработчик):

```json
{ "status": 400, "error": "CODE", "message": "…", "request_id": "…", "errors": { } }
```

* `errors` присутствует только для ошибок валидации (`VALIDATION_ERROR`);
* если эндпоинт не задал явный код, используется `HTTP_<status>`;
* `401 UNAUTHORIZED` — нет/невалидный токен, `403 FORBIDDEN` — не хватает права или роли;
* `500 INTERNAL_ERROR` — необработанное исключение;
* `503 MAINTENANCE` — техработы;
* `AccountBanMiddleware` дополнительно отдаёт `reason` и `expires_at`.

`request_id` есть в теле и в заголовках логов — по нему можно найти причину в логах.

Блокировки на уровне аккаунта (`AccountBanMiddleware`) отвечают отдельным конвертом:

```json
{ "status": 403, "error": "ACCOUNT_BANNED", "message": "…", "reason": "…",
  "expires_at": 1790000000, "request_id": "…" }
```

---

## API: эндпоинты

### Аутентификация

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `POST` | `/api/v1.0/register` | анонимно | Регистрация аккаунта |
| `POST` | `/api/v1.0/login` | анонимно | Вход, выдача JWT |
| `POST` | `/api/v1.0/validate-token` | анонимно | Проверка Bearer-токена |

**`POST /register`**

```json
{ "login": "player", "password": "secret123", "deviceID": "0123456789abcdef0123456789abcdef" }
```

* `login` — сохраняется в нижнем регистре; оригинал остаётся как `realname`.
* `deviceID` — обязателен, 16–128 символов, только `A-Za-z0-9._:-`. Слишком короткий,
  длинный или содержащий посторонние символы идентификатор отклоняется с `400 INVALID_DEVICE_ID`.
* Пароль — 8–72 байта в UTF-8; слишком короткий/длинный отклоняется с `400 WEAK_PASSWORD`.
* Лимиты: по умолчанию 2 аккаунта на устройство и 4 на подсеть. Лимит по IP считается по
  нормализованной подсети: IPv4 — по точному адресу (можно включить `/24` через
  `Registration__Ipv4PrefixLength`), IPv6 — по `/64`, поэтому смена IPv6-адреса не обходит лимит.
* Конкурентные регистрации сериализуются PostgreSQL advisory-блокировками, поэтому
  одновременные запросы не могут превысить лимит.
* Дополнительно действует строгий rate-limit на создание аккаунта
  (`RateLimiting__Register__PermitLimit` за `RateLimiting__Register__WindowSeconds`).
* Ответы: `200` `{success, message, userId, login}`, `409 USERNAME_TAKEN` — логин занят,
  `429 DEVICE_LIMIT` / `429 IP_LIMIT` — превышен лимит, `400 INVALID_DEVICE_ID` — некорректный device ID.

**`POST /login`**

```json
{ "login": "player", "password": "secret123" }
```

* Ответ `200`: `{success, userId, login, token, expiresIn}` (`expiresIn` = 2 592 000).
* `401` — неверный логин/пароль; для забаненного — `403 ACCOUNT_BANNED` с причиной и сроком.
* При успехе обновляются `ip` и `lastlogin`.

### Пользователь

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `GET` | `/api/v1.0/user_data` | авторизован | Имя и UUID текущего пользователя |
| `GET` | `/api/v1.0/user_data/statistics` | авторизован | Статистика текущего пользователя |
| `GET` | `/api/v1.0/{id}/public-key` | авторизован | Публичный ключ пользователя по id |
| `PATCH` | `/api/v1.0/user/password` | авторизован | Смена своего пароля: `{currentPassword, newPassword}`; возвращает новый JWT |

```json
// GET /user_data
{ "username": "player", "realName": "Player", "uuid": "…" }

// GET /user_data/statistics
{ "realName": "Player", "uuid": "…", "role": "user",
  "statistics": { "playTime": 0, "murders": 0, "deaths": 0 } }
```

### Краш-репорты лаунчера

Лаунчер отправляет отчёт только после явного согласия игрока (чекбокс в окне ошибки).
Отчёт привязан к аккаунту, если запрос авторизован; иначе — к `deviceId`
(например, краш на экране входа).

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `POST` | `/api/v1.0/reports` | анонимно / авторизован | Загрузка отчёта (`multipart/form-data`) |
| `GET` | `/api/v1.0/admin/reports` | `reports.view` | Список с фильтрами и пагинацией |
| `GET` | `/api/v1.0/admin/reports/{id}` | `reports.view` | Детали и список файлов |
| `GET` | `/api/v1.0/admin/reports/{id}/files/{fileId}` | `reports.view` | Скачать вложение |
| `PATCH` | `/api/v1.0/admin/reports/{id}` | `reports.manage` | Статус и заметка администратора |
| `DELETE` | `/api/v1.0/admin/reports/{id}` | `reports.manage` | Удалить отчёт |
| `GET` | `/api/v1.0/admin/reports/stats` | `reports.view` | Счётчики для админки |

Поля формы: `kind` (`launcher` / `minecraft`), `title`, `summary`, `launcherVersion`,
`minecraftVersion`, `loader`, `serverName`, `os`, `deviceId`, `consent` (обязательно `true`),
`consentVersion`, и файлы `launcherLog`, `minecraftDebug`, `minecraftCrash`, `minecraftLatest`.

Ограничения: только `.log`/`.txt`, до 10 МБ на файл, до 30 МБ суммарно, не более 4 файлов.
Для `launcher` обязателен `launcherLog`; для `minecraft` — `launcherLog` и хотя бы один
Minecraft-файл. Файлы хранятся в PostgreSQL и доступны только через админский эндпоинт.
Перед отправкой лаунчер вырезает из текста JWT, `--accessToken`, `Bearer`-токены и пароли.

### Серверы

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `GET` | `/api/v1.0/servers` | авторизован | Список **активных** серверов |
| `GET` | `/api/v1.0/servers/{id}` | авторизован | Детали сервера |
| `GET` | `/api/v1.0/servers/{id}/manifest` | **анонимно** | Клиентский манифест сервера |

```json
// GET /servers
[ { "id": "1", "serverName": "main", "gameVersion": "1.21.1",
    "loaderType": "neoforge", "iPAddress": "127.0.0.1", "config": null } ]
```

### Версии (для админ-панели)

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `GET` | `/api/v1.0/versions/minecraft?loader=fabric&includePrerelease=false` | admin area | Версии Minecraft для загрузчика |
| `GET` | `/api/v1.0/versions/loader?loader=forge&mcVersion=1.21.1` | admin area | Версии загрузчика для версии игры |

Известные загрузчики: `vanilla`, `fabric`, `forge`, `neoforge`.

### Сборки

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `POST` | `/api/v1.0/builds` | `builds.upload` | Поставить сборку в очередь |
| `GET` | `/api/v1.0/builds?page=1&pageSize=20&status=failed&query=tech` | `builds.upload` | Постраничный список с фильтрами |
| `GET` | `/api/v1.0/builds/{id}` | `builds.upload` | Статус одной сборки (id — число) |
| `GET` | `/api/v1.0/builds/{id}/logs?afterId=0&limit=200` | `builds.upload` | Журнал обработки |
| `DELETE` | `/api/v1.0/builds/{id}` | `builds.upload` | Удалить сборку, файлы и объекты S3 |
| `GET` | `/api/v1.0/builds/{buildId}/manifest` | **анонимно** | Манифест готовой сборки (buildId — строка) |

> Обратите внимание: `/builds/{id}` — это числовой PK в БД, а `/builds/{buildId}/manifest` —
> строковый `buildId` из 32 hex-символов. Это разные идентификаторы.

`GET /builds` возвращает страницу:

```json
{ "items": [ { "id": 1, "name": "Tech pack", "status": "completed", "...": "..." } ],
  "totalCount": 42, "page": 1, "pageSize": 20, "totalPages": 3 }
```

* `page` — с 1, `pageSize` — 1–100 (по умолчанию 20);
* `status` — `pending`, `downloading`, `hashing`, `completed`, `failed`;
* `query` — поиск по имени без учёта регистра.

**`POST /builds`**

```json
{ "name": "Tech pack", "mcVersion": "1.21.1",
  "loader": "neoforge", "loaderVersion": "21.1.256", "buildPresetId": null }
```

`loader` допускает только `vanilla`, `fabric`, `forge`, `neoforge`. `mcVersion` проверяется
по живому каталогу версий (best-effort): если версия не предлагается выбранным загрузчиком —
`400 UNKNOWN_MC_VERSION`; если каталог временно недоступен, сборка всё равно создаётся.

**`DELETE /builds/{id}`** удаляет строки БД, локальную папку сборки, загруженный архив
модпака и объекты S3. Активную сборку удалить нельзя — `409 BUILD_IN_PROGRESS`.

### Редактор сборок (админка)

В таблице **Builds** кнопка **Edit** открывает редактор завершённой сборки:

* **Settings** — имя, версия загрузчика и пресет; `DisplayName` в манифесте обновляется.
* **Files** — файловый менеджер в стиле Pterodactyl: хлебные крошки, переход по папкам,
  создание каталогов, загрузка в текущую папку, создание файла, действия `…` для
  редактирования/скачивания/переименования/удаления. Текстовый редактор — UTF-8, до 2 МБ;
  загрузка — до 256 МБ; бинарные файлы доступны только для скачивания/замены.
* **Manifest** — просмотр актуального клиентского манифеста и его скачивание.

После каждого изменения API обновляет S3-объект, строку `build_files` и `manifest_json`,
поэтому лаунчер видит правку без полной пересборки. Пустые каталоги хранятся как
S3-маркеры с ключом, оканчивающимся на `/`. Пути проверяются `BuildPathSafety`
(без `..`, абсолютных путей и недопустимых символов). Все эндпоинты редактора требуют
право `builds.upload`.

Большие сборки (4000+ файлов) открываются быстро: сборка и список файлов читаются
двумя отдельными запросами, а S3 листается только на один уровень каталога.

| Метод | Путь | Описание |
|---|---|---|
| `GET` | `/api/v1.0/admin/builds/{id}/editor` | Настройки + полный список файлов |
| `PUT` | `/api/v1.0/admin/builds/{id}/settings` | Сохранить имя/загрузчик/пресет |
| `GET` | `/api/v1.0/admin/builds/{id}/manifest` | Актуальный манифест |
| `GET` | `/api/v1.0/admin/builds/{id}/files?path=...` | Содержимое одного каталога |
| `POST` | `/api/v1.0/admin/builds/{id}/directories` | Создать каталог |
| `DELETE` | `/api/v1.0/admin/builds/{id}/directories?path=...` | Удалить каталог рекурсивно |
| `GET/PUT` | `/api/v1.0/admin/builds/{id}/files/content?path=...` | Прочитать/сохранить текст |
| `POST` | `/api/v1.0/admin/builds/{id}/files` | Загрузить файл (multipart) |
| `GET` | `/api/v1.0/admin/builds/{id}/files/download?path=...` | Скачать файл |
| `DELETE` | `/api/v1.0/admin/builds/{id}/files?path=...` | Удалить файл |
| `POST` | `/api/v1.0/admin/builds/{id}/files/rename` | Переименовать/переместить файл |

### Java runtime (для лаунчера)

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `GET` | `/api/v1.0/java/{major}?os=windows&arch=x64` | авторизован | Метаданные JRE: версия, имя файла, размер, SHA-256, `downloadUrl` |
| `GET` | `/api/v1.0/java/{major}/download?os=windows&arch=x64` | авторизован | Архив JRE; поддерживает `Range` для докачки |

Пример ответа метаданных:

```json
{
  "major": 21,
  "os": "windows",
  "arch": "x64",
  "version": "21.0.13+9.0.LTS",
  "fileName": "OpenJDK21U-jre_x64_windows_hotspot_21.0.13_9.zip",
  "sizeBytes": 49106397,
  "sha256": "2332c0b4cff6bed722da3567c91d5e47568bcba863fa4945d0a8d6fc76158628",
  "downloadUrl": "/api/v1.0/java/21/download?os=windows&arch=x64"
}
```

API скачивает Temurin из Adoptium один раз, проверяет размер и SHA-256 и кэширует архив
в `JavaRuntime:RootPath` (`/app/java`, Docker volume `java_data`). Поддерживаются
`os`: `windows|linux|mac`, `arch`: `x64|aarch64|x32|arm`; версия ограничена
`JavaRuntime:MinMajor..MaxMajor` (по умолчанию 8–64). Лаунчер получает дескриптор,
проверяет хеш перед распаковкой и не обращается к Adoptium/GitHub напрямую.

### Администрирование: доступ и пользователи

| Метод | Путь | Право | Описание |
|---|---|---|---|
| `GET` | `/api/v1.0/admin/me` | любой авторизованный | Роль и эффективные права текущего аккаунта |
| `GET` | `/api/v1.0/admin/permissions` | `admin` | Права и текущие наборы для `moderator`/`user` |
| `PUT` | `/api/v1.0/admin/permissions/{role}` | `admin` | Заменить набор прав роли: `{ "permissions": [...] }` |
| `GET` | `/api/v1.0/admin/activity` | `activity.view` | Журнал действий с фильтрами и сводкой |
| `GET` | `/api/v1.0/admin/users?page=1&pageSize=20` | любое из `users.*` | Список с пагинацией (`pageSize` максимум 100) |
| `GET` | `/api/v1.0/admin/users/search?query=abc` | любое из `users.*` | Поиск, максимум 10 результатов |
| `GET` | `/api/v1.0/admin/users/{id}/statistics` | любое из `users.*` | Статистика, IP-история и история банов с доказательствами |
| `POST` | `/api/v1.0/admin/users/{id}/ban` | `users.ban` | Бан: multipart, см. ниже |
| `GET` | `/api/v1.0/admin/ban-evidence/{id}` | `users.ban` | Скачивание доказательства |
| `POST` | `/api/v1.0/admin/users/{id}/unban` | `users.ban` | Снять все активные баны |
| `PATCH` | `/api/v1.0/admin/users/{id}/role` | `users.change_role` | `{role: "user"\|"moderator"\|"admin"}` |
| `PATCH` | `/api/v1.0/admin/users/{id}/change-password` | `users.change_password` | `{newPassword}` (8–72 байта; старые токены отзываются) |
| `POST` | `/api/v1.0/admin/users/{id}/delete` | `admin` | Безвозвратное удаление с подтверждением username и причиной |

**`POST /admin/users/{id}/ban`** — `multipart/form-data`:

| Поле | Обязательное | Описание |
|---|---|---|
| `reason` | да | Письменная причина, 10–2000 символов |
| `expiresAt` | нет | Unix UTC; если не передан — бан навсегда |
| `evidence` | да | 1–5 файлов: PNG/JPG/WEBP/GIF/BMP, PDF, TXT/LOG, JSON/CSV, ZIP, DOC/DOCX, MP4/WEBM |

Ограничения: до 10 МБ на файл и до 40 МБ суммарно. Доказательства сохраняются в
PostgreSQL (`user_ban_evidence`) и не попадают в публичный S3/CDN. Список банов и
метаданные вложений возвращаются в `/admin/users/{id}/statistics`.

Пример:

```bash
curl -X POST http://localhost:8080/api/v1.0/admin/users/42/ban \
  -H "Authorization: Bearer $TOKEN" \
  -F "reason=Cheating confirmed by screenshots and chat log" \
  -F "expiresAt=1795403874" \
  -F "evidence=@screenshot.png;type=image/png" \
  -F "evidence=@chat.log;type=text/plain"
```

`expiresAt` можно не передавать для постоянной блокировки. В админ-панели дата и время
выбираются через date/time-поля, быстрые кнопки `+1 hour` / `+24 hours` / `+7 days` /
`+30 days` и режим **Permanent**.

### Администрирование: Activity

`GET /api/v1.0/admin/activity` — журнал действий из `admin_audits` (право `activity.view`).

| Параметр | Описание |
|---|---|
| `page`, `pageSize` | Пагинация, `pageSize` 1–100 (по умолчанию 25) |
| `actor` | Точное имя администратора |
| `action` | Точный код действия (`ban_user`, `update_server`, …) |
| `targetType` | Тип цели (`user`, `server`, `build`, `preset`, `role`, …) |
| `query` | Поиск по actor/action/details без учёта регистра |
| `days` | Глубина: `1`, `7`, `30`, `0` — всё время |

Ответ содержит `items`, `totalCount`, `page`, `totalPages`, `summary` и `filterOptions`.
`summary` включает `totalCount`, `todayCount`, `last7DaysCount`, `uniqueActors`,
`actionTypeCount`, `topActions` и `topActors`. `filterOptions` отдаёт доступные значения
для выпадающих списков действий и администраторов.

### Администрирование: серверы и режим

| Метод | Путь | Описание |
|---|---|---|
| `GET` | `/api/v1.0/admin/servers` | Все серверы, включая неактивные |
| `GET` | `/api/v1.0/admin/servers/{id}` | Один сервер (с сырым манифестом) |
| `POST` | `/api/v1.0/admin/servers` | Создать сервер |
| `PUT` | `/api/v1.0/admin/servers/{id}` | Обновить сервер |
| `DELETE` | `/api/v1.0/admin/servers/{id}` | Удалить сервер |
| `POST` | `/api/v1.0/admin/status/maintenance?enabled=true` | Включить/выключить техработы |
| `GET` | `/api/v1.0/admin/status/maintenance` | Текущее состояние |

Доступ: `servers.view` для чтения, `servers.create` для создания, `servers.edit` для
обновления/удаления. Maintenance mode доступен только роли `admin`.

**`POST /admin/servers`**

```json
{ "serverName": "Main", "appVersion": "1.21.1", "loaderType": "neoforge",
  "typeVersion": "21.1.256", "ipAddress": "127.0.0.1", "isActive": true, "buildId": null }
```

Имя сервера уникально (без учёта регистра). Пустое `ipAddress` превращается в `127.0.0.1`.

### Администрирование: пресеты сборок

| Метод | Путь | Описание |
|---|---|---|
| `GET` | `/api/v1.0/admin/presets` | Список пресетов |
| `GET` | `/api/v1.0/admin/presets/{id}` | Один пресет |
| `POST` | `/api/v1.0/admin/presets` | Создать пресет |
| `PUT` | `/api/v1.0/admin/presets/{id}` | Обновить пресет |
| `DELETE` | `/api/v1.0/admin/presets/{id}` | Удалить пресет вместе с модами |

Все эндпоинты пресетов требуют право `presets.create`.

**`POST /admin/presets`**

```json
{ "name": "Tech pack", "gameVersion": "1.21.1",
  "loaderType": "neoforge", "loaderVersion": "21.1.256" }
```

### Администрирование: моды пресетов

| Метод | Путь | Описание |
|---|---|---|
| `GET` | `/api/v1.0/admin/presets/{presetId}/mods` | Моды пресета |
| `GET` | `/api/v1.0/admin/modrinth/search?query=&mcVersion=&loader=` | Поиск модов на Modrinth |
| `POST` | `/api/v1.0/admin/presets/{presetId}/mods/modrinth` | Добавить мод из Modrinth |
| `POST` | `/api/v1.0/admin/presets/{presetId}/mods/custom` | Загрузить свой файл |
| `PATCH` | `/api/v1.0/admin/presets/{presetId}/mods/{modId}/toggle` | Включить/выключить мод |
| `DELETE` | `/api/v1.0/admin/presets/{presetId}/mods/{modId}` | Удалить мод |

Целевые папки: `mods` (`.jar`), `shaderpacks` (`.zip`), `resourcepacks` (`.zip`/`.jar`).
Загружаемые файлы — до 200 МБ; в S3 кладутся по адресу
`custom-artifacts/{sha256}{ext}` (дедупликация по хешу). Требуется право `presets.create`.

### Администрирование: импорт модпаков

| Метод | Путь | Описание |
|---|---|---|
| `POST` | `/api/v1.0/admin/builds/import-modpack/modrinth` | Импорт по slug/ID с Modrinth |
| `POST` | `/api/v1.0/admin/builds/import-modpack/file` | Импорт загруженного `.mrpack` или `.zip` |

Оба эндпоинта требуют право `builds.upload`.

Подробно — в разделе [Импорт модпаков](#импорт-модпаков).

### Диагностика и системные

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `GET` | `/api/v1.0/ip` | admin | IP клиента и все заголовки (отладка прокси) |
| `GET` | `/health/live` | анонимно | Liveness: процесс жив, внешние зависимости не проверяются |
| `GET` | `/health/ready` | анонимно | Readiness: доступен PostgreSQL (используется Docker healthcheck) |
| `GET` | `/health` | анонимно | Полная проверка: PostgreSQL + MinIO/S3 |

В ответе `/health` есть заголовок `api-supported-versions`. Эндпоинты не попадают под
техработы и под лимит частоты запросов для `/api`. Если MinIO недоступен, `/health`
возвращает `503`, а `/health/ready` продолжает отвечать `200` — API может раздавать
манифесты, пока хранилище недоступно.

---

## Сервисы

| Сервис | Зона ответственности |
|---|---|
| `AdminBootstrapService` | Создаёт первого админа при старте, если админов нет |
| `JwtService` | Выпуск и проверка JWT |
| `UserService` | Пользователи: пагинация, поиск, баны с доказательствами, роль, пароль, удаление, статистика, аудит |
| `PermissionService` | Права ролей `moderator`/`user`, неявные права `admin`, короткий кеш |
| `BanEvidenceService` | Валидация и чтение доказательств бана (1–5 файлов, лимиты, MIME-типы) |
| `UserAccessService` | Снапшот бана/роли/версии токена для `AccountBanMiddleware` |
| `BuildEditorService` | Файловый менеджер сборки: чтение, правка, загрузка, переименование, удаление |
| `ServerService` | CRUD серверов, привязка сборок, обновление кеша, аудит |
| `ConfigCacheService` | Кеш активных серверов и флага maintenance (+ зеркало в Redis) |
| `ConfigUpdateService` | Фоновое обновление кеша раз в минуту и рассылка в Redis |
| `AdminAuditService` | Запись действий админов; лента для Activity; ошибки аудита не ломают запрос |
| `VersionCatalogService` | Версии Minecraft и загрузчиков из Mojang/Fabric/Forge/NeoForge |
| `BuildWorkerService` | Фоновая очередь сборок, статусы, ретраи, восстановление зависших сборок |
| `BuildLockService` | Межпроцессная блокировка сборки (PostgreSQL advisory lock) |
| `BuildLogRetentionService` | Фоновая очистка `build_logs` по возрасту и опциональному лимиту на сборку |
| `BuildManifestService` | Хеширование, классификация политик, выгрузка в S3, манифест |
| `EngineDownloadService` | Ванильный клиент + Fabric/Forge/NeoForge |
| `S3StorageService` | S3/MinIO: бакет, политика, дедупликация по SHA-256 |
| `ModpackImportService` | Разбор `.mrpack` и CurseForge-архива, скачивание, overrides |
| `ModrinthService` | Поиск и скачивание модов с проверкой SHA-256 |
| `CurseForgeService` | Резолв `projectID`/`fileID` через CurseForge API |
| `CustomArtifactService` | Загрузка своих jar/zip в S3 с дедупликацией по хешу |
| `BuildLinkService` | Определение сборки, которую отдаёт сервер |
| `GetUuid` | Offline-UUID по логину (MD5 от `OfflinePlayer:<login>`) |
| `BcryptPasswordHasher` | BCrypt-хеширование паролей |

### Middleware

Порядок в пайплайне: Serilog → `RequestLoggingMiddleware` → `GlobalExceptionHandler` →
`/health` → `ForwardedHeaders` → routing → CORS → аутентификация → rate limiter →
`AccountBanMiddleware` → `ApiLockMiddleware` → авторизация → контроллеры → статика.

* **AccountBanMiddleware** — перепроверяет аккаунт по токену: бан, удаление, смена роли.
  Блокирует запрос до `ApiLockMiddleware`/авторизации, поэтому забаненный админ тоже
  не может обойти техработы.
* **RequestLoggingMiddleware** — генерирует `request_id`, пишет `Request started/completed`
  с длительностью и IP; ответы с кодом ≥ 400 логируются как `Warning`.
* **GlobalExceptionHandler** — превращает любое необработанное исключение в
  `500` с `{status, request_id, message}`; внутренние детали наружу не попадают.
* **ApiLockMiddleware** — блокировка `/api` при включённых техработах, с исключением для
  администраторов.

---

## Система сборок

Сборка — это полностью подготовленный клиент Minecraft, готовый к раздаче лаунчеру.

### Статусы

```
pending → downloading → hashing → completed
                               ↘ failed  (до 3 попыток, затем финальный статус)
```

| Статус | Значение | Где выставляется |
|---|---|---|
| `pending` | В очереди | Создание сборки/импорта, ретрай, восстановление после перезапуска, прерывание выключением |
| `downloading` | Идёт подготовка: движок, ассеты, пресет или модпак | Шаг 1 воркера |
| `hashing` | Хеширование и выгрузка в S3 | Шаг 2 воркера |
| `completed` | Готова, манифест сохранён | Конец `FinalizeBuildAsync` |
| `failed` | Ошибка; в `error_message` хранится `попытка\|ошибка` | Обработчик исключений |

Активной считается сборка в статусе `downloading` или `hashing` — такую удалить нельзя
(`409 BUILD_IN_PROGRESS`). Манифест отдаётся только для `completed`.

### Сценарии

Тип сценария определяется автоматически: если в `build_instances.modpack_import` лежит
дескриптор импорта — выполняется `ModpackImport`, иначе `Standard`.

**`Standard`**

1. **Движок.** `EngineDownloadService` скачивает ваниль с
   `piston-meta.mojang.com`: `versions/<mc>/<mc>.jar`, `versions/<mc>/<mc>.json`
   (нужен лаунчеру для `${assetIndex}`), библиотеки и ассеты — параллельно,
   с индикатором прогресса. Затем накладывается загрузчик:
   * **Fabric** — подтягиваются библиотеки с `meta.fabricmc.net`, профиль пишется в
     `versions/<profile>/<profile>.json`;
   * **Forge / NeoForge** — скачивается установщик с Maven и запускается headless:
     `java -Xmx<Build:InstallerMaxHeapMb>m -jar <installer> --installClient <buildDir>`.
     Перед запуском создаётся `launcher_profiles.json`, иначе установщик отказывается
     работать. Java ищется в `JAVA_HOME`, затем в `PATH`, затем в стандартных каталогах
     (`Program Files`, `Eclipse Adoptium`, `Microsoft`) или `/usr/lib/jvm/*`.
     Для 1.21+ маппинги требуют ~1 ГБ heap; на хостах с 2 ГБ RAM нужен swap или
     больше памяти. staging-контейнеру поднят лимит до 1.5 ГБ.
2. **Пресет.** Если задан `buildPresetId`, папки `mods/`, `shaderpacks/`,
   `resourcepacks/` очищаются и заново наполняются включёнными модами. Для модов из
   Modrinth подбирается версия, совместимая с версией игры и загрузчиком; загруженные
   вручную файлы скачиваются из S3.
3. **Финализация.** Сканирование папки, SHA-256, дедуплицированная выгрузка в S3,
   генерация манифеста.

**`ModpackImport`** — тот же пайплайн, но контент берётся из архива модпака
(см. следующий раздел).

Скачивание идемпотентно: если файл уже есть и длина больше нуля, он пропускается;
частичные загрузки пишутся в `*.part` и переименовываются только после успеха.
Каждый файл скачивается с до 3 попытками и экспоненциальной паузой (1 с, 2 с).
При первой ошибке в параллельной фазе отменяются остальные загрузки этой фазы.

### Живой прогресс

Пока идёт сборка, воркер публикует в БД (с троттлингом примерно раз в секунду):
`progressPercent`, `currentStep`, `currentFile`, `bytesDownloaded`, `totalBytes`,
`downloadSpeed`, `downloadedFiles`, `totalFiles`. Отдельные строки пишутся в `build_logs`
(`info` / `warning` / `error`). Админ-панель опрашивает `GET /builds` — WebSocket/SSE не
используются.

Скорость считается как экспоненциальное среднее (окно последних значений), а процент
выбирается так: если фаза идёт по файлам — по числу файлов, иначе по байтам.

```bash
# первые порции (последние строки, в хронологическом порядке)
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/api/v1.0/builds/5/logs?afterId=0"

# инкрементально: только новее последнего полученного id
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/api/v1.0/builds/5/logs?afterId=142"
```

### Ретраи

Упавшая сборка возвращается в очередь, пока попыток меньше трёх. Прерванные перезапуском
сборки в состояниях `downloading`/`hashing` автоматически возвращаются в `pending` при
старте и периодически (раз в 30 секунд, когда очередь простаивает). Скачивание
идемпотентно: уже скачанные файлы с верным хешем пропускаются, частичные пишутся в `*.part`.

**Несколько реплик API.** Каждая сборка обрабатывается только одним воркером: перед
стартом воркер берёт PostgreSQL advisory lock по `build_instances.id` на отдельном
соединении (`Pooling=false`). Если лок занят другой репликой, сборка пропускается.
Восстановление «зависших» сборок тоже сначала пытается взять лок: если он занят, значит
сборка всё ещё живёт в другом процессе, и её нельзя сбрасывать в `pending`. Lock
удерживается heartbeat-запросом раз в 30 секунд и автоматически освобождается при
закрытии соединения или падении процесса.

### Ретеншен логов

`BuildLogRetentionService` раз в `Build__LogRetentionIntervalHours` удаляет строки
`build_logs` старше `Build__LogRetentionDays` дней. При необходимости можно включить
жёсткий лимит строк на одну сборку (`Build__LogRetentionMaxPerBuild`, `0` — выключено).
Очистка выполняется в фоне и не влияет на обработку сборок.

### Дедупликация

Перед выгрузкой каждого файла выполняется `StatObject`; если объект есть и его
метаданные `x-amz-meta-sha256` совпадают — повторная загрузка пропускается.

### Загрузчики и main class

| Загрузчик | mainClass |
|---|---|
| Vanilla | `net.minecraft.client.main.Main` |
| Fabric | `net.fabricmc.loader.impl.launch.knot.KnotClient` |
| Forge | `net.minecraftforge.bootstrap.ForgeBootstrap` |
| NeoForge | `net.neoforged.bootstrap.NeoForgeBootstrap` |

Java-версия берётся из `javaVersion.majorVersion` в версионном JSON, с запасным вариантом
по версии игры (1.20.5+ → 21, 1.18+ → 17, 1.17 → 16, старее → 8).

### Привязка сборки к серверу

Поле `buildId` у сервера задаёт режим:

| Значение | Поведение |
|---|---|
| не задано | **авто** — сервер отдаёт манифест последней `completed`-сборки с совпадающей версией игры и загрузчиком |
| конкретный `buildId` | **закреплено** — всегда манифест этой сборки |
| пустая строка | **нет** — `404` |

Автоматический режим вычисляется на лету при каждом `GET /servers/{id}/manifest`, поэтому
привязка «оживает» сразу после завершения подходящей сборки.

---

## Импорт модпаков

Позволяет залить готовый модпак целиком. Формат определяется **по содержимому архива**,
а не по расширению.

| Формат | Маркер | Что внутри |
|---|---|---|
| Modrinth | `modrinth.index.json` | Индекс с путями, URL и хешами + `overrides/` и `client-overrides/` |
| CurseForge | `manifest.json` | Только пары `projectID`/`fileID` + `overrides/`; самих модов в архиве нет |

### Ключ CurseForge

CurseForge отдаёт файлы только по API-ключу, который бесплатно выдаётся на
<https://console.curseforge.com/>. Задать его можно так:

```env
# в .env рядом с docker-compose.yml или в Vault app / app-staging
CurseForge__ApiKey=$2a$10$...
```

> **Важно:** ключ CurseForge содержит `$`. В Ansible-деплое compose использует
> `env_file: { path: ..., format: raw }`, поэтому значение сохраняется как есть.
> Если запускаешь compose вручную и в `env_file` нет `format: raw`, либо добавь
> его, либо экранируй `$` как `$$` — иначе compose обрежет ключ и CurseForge
> вернёт `403`.

Без ключа импорт `.mrpack` работает как обычно, а CurseForge-архив отклоняется сразу при
загрузке с понятной ошибкой — до запуска воркера.

### Импорт по slug/ID с Modrinth

```
POST /api/v1.0/admin/builds/import-modpack/modrinth
```

```json
{ "name": "Fabulously Optimized",
  "slug": "fabulously-optimized",
  "versionId": null,
  "serverId": null }
```

* `slug` — slug или числовой ID проекта на Modrinth (обязателен).
* `versionId` — конкретная версия; без неё берётся последний релиз.
* `serverId` — закрепить сервер за собранной сборкой.
* Ошибки: `404` — проект/версия/сервер не найдены, `429` — rate limit Modrinth,
  `400` — некорректный индекс.

### Импорт загруженного файла

```
POST /api/v1.0/admin/builds/import-modpack/file
Content-Type: multipart/form-data
```

| Поле | Обязательное | Описание |
|---|---|---|
| `file` | да | `.mrpack` (Modrinth) или `.zip` (CurseForge), до 1 ГБ |
| `name` | нет | имя сборки (по умолчанию — из манифеста) |
| `serverId` | нет | сервер для закрепления |

Ошибки: `400 FILE_REQUIRED`, `400 INVALID_FILE_TYPE` (расширение не `.mrpack`/`.zip`),
`400 MODPACK_IMPORT_FAILED` (архив не распознан, нет ключа CF, пустой список файлов),
`400 MODPACK_INDEX_INVALID`, `404 SERVER_NOT_FOUND`.

### Как это работает

1. **Определение загрузчика.** Для Modrinth берётся `dependencies` (`fabric-loader`,
   `forge`, `neoforge`). Для CurseForge — `minecraft.modLoaders[primary]`, из которого
   отбрасывается префикс с версией игры:
   `forge-1.12.2-14.23.5.2860` → `forge` / `14.23.5.2860`. Неизвестные загрузчики
   (`quilt-loader`, `liteloader`, `rift`) отклоняются с понятной ошибкой.
2. **Файлы.** Скачиваются параллельно с проверкой хешей; уже скачанное с верным хешем
   пропускается. Индекс/манифест ограничен 20 000 файлами, архив — 1 ГБ. Для CurseForge каждая пара `projectID`/`fileID` резолвится через
   `GET /v1/mods/{modId}/files/{fileId}` (с повторами при `429`/`5xx`), файлы кладутся в
   `mods/` и проверяются по `sha1` и размеру. Если CurseForge не вернул `downloadUrl`,
   ссылка собирается из идентификатора файла
   (`mediafilez.forgecdn.net/files/{id/1000}/{id%1000}/{имя}`). Если хеша нет —
   проверяется только размер, о чём пишется в лог. Любой неразрешённый файл **проваливает
   импорт**: молчаливо неполная сборка недопустима.
3. **Overrides.** У `.mrpack` сначала `overrides/`, затем `client-overrides/` поверх.
   У CurseForge — единственная папка из `manifest.overrides`. Все пути проходят проверку
   на выход за пределы каталога (защита от zip-slip); разделители путей нормализуются,
   поэтому архивы, собранные Windows-утилитами, тоже работают.
4. **Финализация.** Обычный пайплайн: хеширование, дедуплицированная выгрузка в S3,
   генерация манифеста.

Загруженный архив хранится в `{Build:RootPath}/imports/{buildId}.mrpack` до успешного
завершения сборки (нужен для повторных попыток), затем удаляется. При старте воркер
подчищает осиротевшие архивы. Удаление сборки удаляет и остаток.

---

## Манифест и синхронизация с клиентом

Манифест — описание готовой сборки, которое лаунчер использует для синхронизации файлов.

```json
{
  "buildId": "3b3a28ee7c2f4c2faa794b8c9331d26e",
  "displayName": "All of Create",
  "gameVersion": "1.21.1",
  "loaderType": "neoforge",
  "loaderVersion": "21.1.256",
  "javaVersion": 21,
  "mainClass": "net.neoforged.bootstrap.NeoForgeBootstrap",
  "jvmArgs": ["-XX:+UseG1GC", "…"],
  "gameArgs": ["--username", "${username}", "…"],
  "baseUrl": "http://localhost:8080",
  "files": [
    { "path": "mods/example.jar",
      "sha256": "…",
      "size": 1234567,
      "policy": "FORCE_SYNC",
      "url": "http://localhost:8080/builds/3b3a…/mods/example.jar" }
  ]
}
```

Плейсхолдеры вида `${username}`, `${gameDir}`, `${assetsDir}` подставляет лаунчер.

### Политики синхронизации

| Политика | Поведение клиента | Кому назначается |
|---|---|---|
| `FORCE_SYNC` | Всегда перезаписывать | Всё, кроме перечисленного ниже |
| `DOWNLOAD_ONCE` | Скачать, только если файла нет | `options.txt`, `launcher_profiles.json` |
| `PRESERVE` | Не удалять, восстановить если отсутствует | `saves`, `screenshots`, `logs`, `crash-reports`, `backups`, `local`, `emblems`, `playerdata`, `stats`, `advancements`, `realms`, `servers.dat`, `usercache.json`, `usernamecache.json` |

### Раздача файлов

Файлы лежат в MinIO по ключу `builds/{buildId}/{путь}`. nginx срезает имя бакета и отдаёт
их как `http://localhost:8080/builds/{buildId}/...` с кешированием, поддержкой `Range` и
CORS-заголовками для лаунчера. URL собираются на сервере из `S3__CdnBaseUrl` **без бакета**
(`<cdnBaseUrl>/builds/...`). Старые манифесты с `/minecraft-builds/builds/...` продолжают
работать через общий `location /` в nginx.

Манифест доступен в двух местах:

* `GET /api/v1.0/builds/{buildId}/manifest` — по конкретной сборке;
* `GET /api/v1.0/servers/{id}/manifest` — то, что сервер отдаёт клиенту (с учётом привязки).

---

## Админ-панель

Веб-панель лежит в `UnderMineApi/wwwroot/admin/` и раздаётся самим API по пути `/admin/`.
Это статический SPA (HTML + JS + CSS) без сборщика и внешних зависимостей. Файлы панели
отдаются с `Cache-Control: no-cache`, чтобы обновление не скрывалось за браузерным кешем.

Вкладки:

| Вкладка | Возможности |
|---|---|
| **Dashboard** | Сводка: пользователи, активные серверы, сборки, режим техработ |
| **Servers** | Создание, редактирование, удаление серверов; привязка сборки; вкл/выкл |
| **Builds** | Создание сборки, импорт модпаков (Modrinth и CurseForge), прогресс-бар, живой журнал, удаление |
| **Presets** | Пресеты сборок и их моды (поиск в Modrinth, загрузка своих файлов) |
| **Users** | Список, поиск, статистика, бан/разбан с причиной и доказательствами, история банов, смена роли и пароля, удаление |
| **Activity** | Журнал действий администраторов: фильтры, сводка, топ действий и администраторов |
| **Settings** | Maintenance mode и **Roles & access** — права ролей `moderator` и `user` |

Вход в панель использует тот же `/api/v1.0/login`, что и игра; токен хранится в
`localStorage` под ключом `admin_token`. После входа панель запрашивает `/admin/me`
и скрывает недоступные вкладки/кнопки.

### Бан пользователя в панели

Форма бана содержит:

* режим **Temporary / Permanent**;
* дату и время окончания (или быстрые кнопки `+1 hour`, `+24 hours`, `+7 days`, `+30 days`);
* обязательную письменную причину (10–2000 символов);
* обязательные доказательства — до 5 скриншотов/файлов (10 МБ каждый, 40 МБ суммарно).

В статистике пользователя отображается история банов. Изображения открываются во
встроенном просмотре, остальные файлы скачиваются; старые баны без вложений помечаются
как legacy.

---

## База данных

| Таблица | Назначение | Ключевые поля |
|---|---|---|
| `user_accounts` | Аккаунты | `id`, `player_uuid`, `username` (unique, lower-case), `realname`, `hash_password`, `user_role` (`admin`/`moderator`/`user`, по умолчанию `user`), `regdate`, `lastlogin`, `ip`, `regip`, `email`, `device_id`, `PublicKey`, `token_version` |
| `user_data` | Статистика игроков | `user_id` (PK и FK), `play_time`, `murders`, `deaths` |
| `user_ip_history` | История IP | `user_id` (FK), `ip`, `first_seen`, `last_seen`, `seen_count`; до 50 адресов на пользователя |
| `user_bans` | Баны | `user_id` (FK), `reason` (10–2000 символов), `banned_by`, `created_at`, `expires_at` (NULL — навсегда), `is_active` |
| `user_ban_evidence` | Доказательства бана | `ban_id` (FK), `file_name`, `content_type`, `size`, `sha256`, `data` (bytea), `created_at` |
| `role_permissions` | Права ролей | `(role, permission)` — PK; для `moderator` и `user` |
| `server_manifests` | Игровые серверы | `id`, `name` (уникально), `type` (загрузчик), `app_version` (версия игры), `ip_address`, `is_active`, `build_manifest` (text), `build_id` (привязка сборки, максимум 64) |
| `server_configs` | Настройки сервера | `server_id` (PK и FK), `type_version` (версия загрузчика), `config_hash_sum` |
| `global_settings` | Глобальные настройки | `setting_key` (PK), `setting_value` (в том числе `Maintenance`) |
| `admin_audits` | Аудит действий админов | `actor`, `action`, `target_type`, `target_id`, `details`, `created_at` (индекс для Activity) |
| `build_instances` | Сборки | `id`, `name`, `mc_version`, `loader_type`, `loader_version`, `status`, `build_id` (unique, максимум 100), `manifest_json` (text), `error_message` (`попытка\|ошибка`), `build_preset_id`, `modpack_import` + колонки живого прогресса |
| `build_files` | Файлы сборки | `build_id` (FK), `path` (максимум 500), `sha256` (индексирован — дедупликация), `size`, `policy`, `s3_key` |
| `build_logs` | Журналы сборок | `build_instance_id` (FK, индексирован), `level`, `message`, `created_at` |
| `build_presets` | Пресеты сборок | `name`, `game_version`, `loader_type`, `loader_version`, `created_at`, `updated_at` |
| `build_preset_mods` | Моды пресетов | `id` (Guid), `build_preset_id` (FK), `source_type` (`Modrinth`/`CustomUpload`), `target_folder`, `modrinth_slug_or_id`, `custom_file_s3_key`, `sha256_hash`, `file_size`, `is_enabled` |

### Связи

```
user_accounts  1─1  user_data            (каскадное удаление)
user_accounts  1─N  user_bans            (каскадное удаление)
user_accounts  1─N  user_ip_history      (каскадное удаление)
user_bans      1─N  user_ban_evidence    (каскадное удаление)
server_manifests 1─1 server_configs      (каскадное удаление)
build_presets  1─N  build_preset_mods    (каскадное удаление)
build_instances 1─N  build_files          (каскадное удаление)
build_instances 1─N  build_logs           (каскадное удаление)
build_instances N─1  build_presets        (SetNull: сборка переживает удаление пресета)
server_manifests ──build_id──► build_instances.build_id   (мягкая ссылка, без FK)
```

`role_permissions`, `global_settings` и `admin_audits` самостоятельны.

### Идентификаторы

ID сборок, файлов, логов, пресетов и серверов **никогда не переиспользуются**. После удаления
запись исчезает, а её числовой ID остаётся «сгоревшим»; следующая запись получает следующий
номер. Это осознанный отказ от перемотки PostgreSQL-sequences: старые ссылки, аудит и
внешние интеграции не должны внезапно указывать на другую сущность.

---

## Логирование

Используется **Serilog**: вывод в консоль и в файл `logs/api-<дата>.log`
(ротация по дням, хранится 14 файлов, ротация при 50 МБ, буферизация).

```
[2026-10-16 07:12:03.115 +00:00 INF] Request completed: GET /api/v1.0/servers [StatusCode: 200] [Duration: 3ms]
```

Сообщения **всегда на английском** — это осознанное решение проекта.

Уровни настраиваются через `.env`:

```env
Logging__LogLevel__Default=Information
Logging__LogLevel__Microsoft.AspNetCore=Warning
```

---

## Безопасность

| Мера | Реализация |
|---|---|
| Пароли | Только BCrypt-хеши; политика 8–72 байта (UTF-8) |
| Перебор/энумерация | Для несуществующего логина выполняется «пустая» BCrypt-проверка (выравнивание времени) |
| Токены | JWT HS256, проверка подписи, издателя, аудитории и срока |
| Отзыв сессий | `token_version` в JWT; смена/сброс пароля инкрементирует версию и инвалидирует старые токены (`401 TOKEN_REVOKED`) |
| Ключ JWT | Приложение отказывается стартовать с ключом короче 32 символов |
| Авторизация | Роль `admin` и permission-политики для `moderator`/`user`; интерфейс и API проверяют одно и то же право |
| Иерархия ролей | Неадминистратор не может управлять ролью выше своей, назначать её или менять собственную роль |
| Состояние аккаунта | Бан/удаление/смена роли/версия токена перепроверяются на каждом защищённом запросе (`AccountBanMiddleware`) |
| Доказательства бана | Хранятся в PostgreSQL, не в публичном S3; скачивание только через `users.ban` с проверкой владельца |
| CORS | Только явный allow-list; в Production без него cross-origin запрещён |
| Security headers | CSP, `X-Frame-Options`, `nosniff`, `Referrer-Policy`, `Permissions-Policy`, HSTS при HTTPS |
| IP за прокси | `X-Forwarded-*` принимаются только от доверенной сети nginx |
| Rate limiting | Раздельные политики `api`/`admin`/`auth`/`register`/`static`, ключ — пользователь или подсеть, `Retry-After` + JSON при 429 |
| Техработы | Блокировка публичного API, админы остаются доступны |
| Ошибки | Наружу не отдаются внутренние детали, попадает `request_id` |
| Аудит | Действия админов пишутся в `admin_audits` и доступны на странице Activity (`activity.view`) |
| Регистрация | Лимиты на устройство и IP, валидация deviceID, хеширование пароля |
| Смена пароля | Пользователь — сам через `PATCH /user/password` (с текущим паролем), админ — через админку; старые сессии отзываются |

### Рекомендации для продакшена

1. **Только HTTPS**: используйте `docker-compose.tls.yml` + `nginx.tls.conf.example` (см. раздел Docker).
2. **CORS**: в Production без `CORS__AllowedOrigins` cross-origin закрыт; при необходимости перечислите домены явно.
3. **Длинный `Jwt__Key`** — 64+ символов, генерировать случайно.
4. **Сменить пароли MinIO** с `minioadmin`.
5. **Регулярные бэкапы** PostgreSQL и бакета MinIO.
6. **Сбор логов** в централизованную систему.
7. **Проверка зависимостей**: `dotnet list package --vulnerable`.

---

## Миграции

Миграции применяются автоматически при старте приложения (`Database.Migrate()`).
Для ручного управления:

```bash
# создать миграцию
dotnet ef migrations add <MigrationName> --project UnderMineApi/UnderMineApi.csproj

# применить
dotnet ef database update --project UnderMineApi/UnderMineApi.csproj

# откатить последнюю
dotnet ef migrations remove --project UnderMineApi/UnderMineApi.csproj

# сгенерировать SQL-скрипт
dotnet ef migrations script --project UnderMineApi/UnderMineApi.csproj
```

> **Важно.** EF отказывается применять миграции, если модель изменилась, но миграция не
> создана: приложение падает с `PendingModelChangesWarning`. После изменения любой
> сущности в `AppDbContext` всегда добавляйте миграцию, иначе свежая установка не
> сможет создать схему.

### История миграций

| Миграция | Описание |
|---|---|
| `20260413113629_InitialCreate` | Создание базовой схемы |
| `20260422155059_SplitServerManifestAndConfig` | Разделение `server_manifests` и `server_configs` |
| `20260621174830_FixServerManifests` | Исправление серверов |
| `20261009093052_RemoveChatAndFriendships` | Удаление чата и друзей |
| `20261012062845_AddBuildManifest` | Манифест сборки |
| `20261012073914_AddBuildService` | Базовая система сборок |
| `20261013104625_AddServerBuildLink` | Привязка сборок к серверам |
| `20261013105147_ChangeBuildManifestToText` | Манифест в текстовый формат |
| `20261013113048_AddBuildProgressAndLogs` | Прогресс и логи сборок |
| `20261013125219_RemoveServerConfigLinks` | Удаление устаревших связей |
| `20261013130948_RemoveServerLinkColumns` | Удаление колонок связей |
| `20261016070725_AddModpackImportToBuildInstances` | Импорт модпаков, пресеты сборок |
| `20261106123739_AddRegistrationLimits` | Лимиты регистрации: `regip_prefix`, индексы |
| `20261106125206_AddAuthHardening` | `token_version` для отзыва JWT |
| `20261106131612_AddBuildQueryIndexes` | Индексы для пагинации сборок и ретеншена логов |
| `20261122140646_AddUserIpHistory` | История IP-адресов пользователей |
| `20261122144821_AddRolePermissions` | Права ролей `moderator`/`user` и дефолты модератора |
| `20261122150102_AddAdminAuditActivityIndex` | Индекс `admin_audits.created_at` для Activity |
| `20261122151511_AddBanEvidence` | Доказательства бана в `user_ban_evidence` |

> Миграции применяются автоматически при старте с ретраями
> (`Database__MigrateRetries` / `Database__MigrateRetryDelaySeconds`). Если все попытки
> неудачны, API завершается с ненулевым кодом, а не работает со старой схемой.

---

## Docker

### Сервисы

| Сервис | Образ | Порт (host) | Роль |
|---|---|---|---|
| `api` | собирается из `Dockerfile` | `127.0.0.1:5000` → 8080 | REST API и админ-панель |
| `postgres` | `${POSTGRES_IMAGE:-postgres:16}` | `127.0.0.1:5432` | База данных |
| `minio` | `${MINIO_IMAGE:-dhi.io/minio:latest}` | `127.0.0.1:9000`, `9001` | S3-хранилище файлов |
| `nginx` | `${NGINX_IMAGE:-nginx:1.27-alpine}` | `8080` → 80 | Публичный вход: API, админка, CDN, кеш, CORS |

Образы вынесены в переменные `POSTGRES_IMAGE` / `MINIO_IMAGE` / `NGINX_IMAGE`. Для продакшена
зафиксируйте MinIO по digest (см. комментарий в `.env.example`).

Тома: `postgres_data`, `minio_data`, `builds_data`.

### Healthchecks

У всех сервисов есть Docker healthcheck, а зависимости поднимаются по `condition: service_healthy`:

| Сервис | Проверка |
|---|---|
| `postgres` | `pg_isready` |
| `minio` | TCP-подключение к `:9000` (в hardened-образе нет `curl`/`mc`) |
| `api` | `curl /health/ready` (PostgreSQL; миграции уже применены) |
| `nginx` | `wget /health` |

API-образ сохраняет `curl` специально для healthcheck. Все сервисы запускаются с
`restart: unless-stopped`, поэтому падение контейнера поднимается автоматически.

### TLS (HTTPS)

В базовом compose nginx слушает только HTTP на `8080`. Для HTTPS есть готовый профиль:

```bash
# 1. Сертификаты (Let's Encrypt / ваши собственные)
mkdir -p certs
cp /etc/letsencrypt/live/example.com/fullchain.pem certs/
cp /etc/letsencrypt/live/example.com/privkey.pem certs/

# 2. Конфиг nginx с TLS (копия из шаблона)
cp nginx.tls.conf.example nginx.tls.conf

# 3. Запуск с override-файлом
docker compose -f docker-compose.yml -f docker-compose.tls.yml up -d
```

В TLS-профиле: HTTP (`80`) редиректит на HTTPS, `443` терминирует TLS, HSTS добавляется
nginx и API, `/api/`, `/admin/` и `/builds/` работают как раньше. Папка `certs/` и
`nginx.tls.conf` в `.gitignore` — ключи не попадут в репозиторий.

### Образ API

Собирается в два этапа: `sdk:9.0` для restore/publish и `aspnet:9.0` для запуска.
В финальный образ дополнительно ставится **temurin-21-jre** — он нужен для headless-запуска
установщиков Forge/NeoForge во время сборок. Статические файлы админ-панели копируются
отдельно, поэтому `dotnet publish` их не теряет.

### Полезные команды

```bash
docker compose up -d --build        # поднять всё
docker compose logs -f api          # логи API
docker compose restart api          # перезапуск (перечитывает образ, но не .env)
docker compose up -d --force-recreate api   # пересоздать (перечитывает .env)
docker compose down                 # остановить
docker compose down -v              # остановить и удалить данные
```

> `docker compose restart` **не** перечитывает `.env` — окружение контейнера берётся при
> создании. Если меняли переменные, нужен `up -d --force-recreate`.

---

## CI/CD

### GitHub Actions

Основной workflow: `.github/workflows/ci.yml`.

- На `pull_request`: `dotnet restore/build/test`, `node --check` и Docker build
  без публикации образа.
- На `push` в `master`: то же плюс публикация в GHCR.
- Теги: `ghcr.io/skufiks/undermine-api:sha-<full-sha>` и `latest`.
- Образы из PR никогда не публикуются.

Дополнительно:

- `.github/workflows/secrets.yml` — Gitleaks secret scan;
- `.github/workflows/codeql.yml` — CodeQL для C#.

Локальная сборка и публикация:

```bash
bash deploy-local.sh --tags staging
```

### Деплой

Ansible-деплой запускается из GitHub Environments `staging` и `production`.
Секреты (`SSH_PRIVATE_KEY`, `ANSIBLE_INVENTORY`, `VAULT_ADDR`, `VAULT_TOKEN`)
хранятся в environment, а `inventory.ini` и `group_vars/all/main.yml`
генерируются в workflow и не коммитятся.

Полный blueprint деплой-воркфлоу, список секретов и порядок отката:
[`.github/workflows/README.md`](.github/workflows/README.md).

---

## Разработка

### Проекты в решении

| Проект | Назначение |
|---|---|
| `UnderMineApi` | Основное веб-приложение |
| `ManifestGenerator` | CLI-утилита: строит манифест из готовой папки сборки |
| `UnderMineApi.Tests` | xUnit-тесты (unit/harness) |

Проверка изменений:

### Тесты

Полный прогон одной командой (restore → build → test, выход с ошибкой при первом сбое):

```powershell
# Windows / PowerShell
.\run-tests.ps1
.\run-tests.ps1 -Configuration Release
.\run-tests.ps1 -Filter "FullyQualifiedName~ClientReport"
```

```bash
# Linux / macOS / CI (нужен .NET 9 SDK)
./run-tests.sh
./run-tests.sh Release
./run-tests.sh Debug "FullyQualifiedName~ClientReport"
```

`-NoRestore` / `NO_RESTORE=1` пропускает `dotnet restore`, если пакеты уже восстановлены.

Ручной вариант:

```bash
dotnet build UnderMineApi.sln
dotnet test UnderMineApi.sln
```

### ManifestGenerator

```bash
dotnet run --project ManifestGenerator -- \
  ./builds/underMine \
  ./builds/underMine/manifest.json \
  --name "UnderMine 1.20.1" \
  --game-version 1.20.1 \
  --loader forge \
  --loader-version 47.2.0 \
  --cdn-base http://localhost:8080/builds/underMine
```

Полезно, когда сборка уже подготовлена на диске и нужно только описать её для лаунчера.

### Команды

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project UnderMineApi/UnderMineApi.csproj
dotnet watch run --project UnderMineApi/UnderMineApi.csproj
dotnet list package --outdated
dotnet list package --vulnerable
```

### Соглашения

- Имена переменных и параметров — `camelCase`, методы/свойства/классы — `PascalCase`.
- Сообщения в логах — **на английском**.
- Публичные методы снабжаются XML-документацией.
- Секреты — только через `.env` / переменные окружения.
- Пользовательские сообщения API и тексты админ-панели — на русском/английском
  в зависимости от части интерфейса.

---

## Известные ограничения

- **Swagger/OpenAPI доступен только в Development** (`/swagger`); в Production документация
  ведётся в этом файле.
- **Доказательства бана хранятся в PostgreSQL** (до 40 МБ на бан). Это защищает скриншоты
  от публичного CDN, но увеличивает размер БД и бэкапов.
- **Форматы ошибок исторически различаются** между эндпоинтами: `{Error, Message}`,
  `{status, message}` или просто текст; новые админские эндпоинты используют единый конверт.
- **Файлы модов пресетов** ограничены лимитом Kestrel (~128 МБ) по умолчанию, хотя
  сервис допускает до 200 МБ — при необходимости поднимите `MaxRequestBodySize`.
- **Сборки больших модпаков** (несколько тысяч файлов) чувствительны к длительности этапа
  выгрузки в S3; при сбое фазы S3 настоящая ошибка записывается в лог сборки.
- **Наследие Forge/NeoForge** (например, Minecraft 1.12.2) может требовать Java 8 на этапе
  установщика, тогда как в контейнере стоит Temurin 21 — такие сборки стоит проверять
  отдельно.
- **Импорт по ссылке CurseForge** (по `projectID`/`slug`) не реализован — только загрузка
  готового архива.