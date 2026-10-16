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
- [Разработка](#разработка)
- [Известные ограничения](#известные-ограничения)

---

## Возможности

### Аккаунты и аутентификация

- Регистрация игровых аккаунтов с лимитами (2 аккаунта на устройство, 4 на IP).
- Вход с выдачей **JWT** (HS256, срок 30 дней) и проверкой бана перед выдачей токена.
- Пароли хранятся только в виде **BCrypt**-хешей.
- Роли `admin` / `user`. Роль попадает в токен и проверяется на админ-эндпоинтах.
- Хранение статистики игрока: время игры, убийства, смерти, offline-UUID.

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

- Веб-панель (SPA) на `/admin/`: дашборд, серверы, сборки, пресеты, пользователи, настройки.
- Управление пользователями: бан/разбан (временный и постоянный), смена роли и пароля.
- Режим технических работ с блокировкой публичного API.
- Аудит действий администраторов в отдельной таблице.

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

| Сервис | Порт | Назначение |
|---|---|---|
| API | `5000` | REST API и админ-панель |
| nginx (CDN) | `8080` | Раздача файлов сборок |
| MinIO API | `9000` | S3-совместимое хранилище |
| MinIO Console | `9001` | Веб-консоль MinIO |
| PostgreSQL | `5432` | База данных |

Проверка:

```bash
curl http://localhost:5000/health     # Healthy
curl http://localhost:8080/health     # OK  (nginx)
```

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

При первом старте приложение само применит миграции (`Database.Migrate()`), создаст
таблицы и заведёт админ-аккаунт (см. следующий раздел).

---

## Первый вход в админку

На свежей базе, где **нет ни одного администратора**, API при старте сам создаёт
админ-аккаунт со **случайным логином и паролем** и печатает их в лог:

```
======================== DEFAULT ADMIN CREATED ========================
 Login:    admin_1c035dea
 Password: PAgQf_Jr+vDrxhwSaH6pex+*
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

Дальше — `http://localhost:5000/admin/` и вход этими кредыми.

### Поведение

| Ситуация | Что произойдёт |
|---|---|
| Админов в базе нет | Создаётся `admin_<8 hex>` со случайным паролем из 24 символов; креды печатаются в лог |
| Админ уже есть | Ничего не создаётся, в лог: `Admin account already exists, skipping bootstrap` |
| Заданы `BootstrapAdminUsername` **и** `BootstrapAdminPassword` | Создаётся этот аккаунт; если он уже есть — его пароль и роль **перезаписываются** |
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
BootstrapAdminUsername=admin_1c035dea
BootstrapAdminPassword=новый-пароль
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
| `S3__CdnBaseUrl` | Публичный базовый URL файлов (то, что попадёт в манифест) | `http://localhost:8080` |
| `S3__PublicRead` | Публичное чтение объектов (бакет делается read-only для всех) | `true` |
| `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD` | Учётные данные сервиса MinIO | `minioadmin` |

### Сборки и модпаки

| Параметр | Описание | По умолчанию |
|---|---|---|
| `Build__RootPath` | Рабочая директория сборок внутри контейнера | `/app/builds` |
| `CurseForgeApiKey` | Ключ CurseForge API (см. [импорт модпаков](#импорт-модпаков)) | — |
| `CurseForge__ApiKey` | То же, если задаётся как переменная окружения приложения | — |

### Первый админ

| Параметр | Описание | По умолчанию |
|---|---|---|
| `BootstrapAdminEnabled` | Создавать админа при старте, если админов нет | `true` |
| `BootstrapAdminUsername` | Фиксированный логин (перезаписывает пароль и роль) | — |
| `BootstrapAdminPassword` | Фиксированный пароль | — |

### Прочее

| Параметр | Описание | По умолчанию |
|---|---|---|
| `Redis__Host` / `Redis__Port` / `Redis__Username` / `Redis__Password` / `Redis__Ssl` | Подключение к Redis для распределённого кеша | не задан |
| `CORS__AllowedOrigins` | Список разрешённых источников через запятую | любые |
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Production` | `Production` |
| `ASPNETCORE_HTTP_PORTS` / `ASPNETCORE_HTTPS_PORTS` | Порты приложения | `8080` / `8081` |
| `Logging__LogLevel__Default` | Уровень логирования | `Information` |

---

## Структура проекта

```
apiundermine/
├── docker-compose.yml              # postgres + minio + nginx + api
├── nginx.conf                      # CDN-прокси на MinIO с кешем и CORS
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
│   │   ├── AdminController.cs              # пользователи, серверы, maintenance
│   │   ├── AdminModsController.cs          # моды пресетов, поиск в Modrinth
│   │   ├── AdminPresetsController.cs       # CRUD пресетов сборок
│   │   ├── AdminModpackController.cs       # импорт модпаков (.mrpack / CurseForge)
│   │   └── DebugController.cs              # диагностика IP/заголовков (admin)
│   │
│   ├── Services/                   # бизнес-логика
│   │   ├── AdminBootstrapService.cs       # создание первого админа
│   │   ├── JwtService.cs                  # выпуск и проверка JWT
│   │   ├── UserService.cs / ServerService.cs
│   │   ├── ConfigCacheService.cs          # кеш серверов и флага maintenance
│   │   ├── ConfigUpdateService.cs         # фоновое обновление кеша
│   │   ├── ApplicationStatusService.cs    # статус API для панели
│   │   ├── AdminAuditService.cs           # аудит действий админов
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
│   │   └── ApiLockMiddleware.cs           # блокировка API при maintenance
│   │
│   ├── Domain/Entities/             # EF-сущности доменной модели
│   │   ├── UserAccount.cs                   # user_accounts (+ BuildFile)
│   │   ├── BuildInstance.cs                 # build_instances + build_files
│   │   ├── BuildLog.cs                      # build_logs
│   │   └── BuildPreset.cs                   # build_presets + build_preset_mods
│   │
│   ├── Models/                      # модели предметной области
│   │   ├── UserData.cs                      # user_data (статистика)
│   │   ├── UserBans.cs                      # user_bans
│   │   ├── ServerManifest.cs                # server_manifests
│   │   ├── ServerConfigDetails.cs           # server_configs
│   │   ├── GlobalSettings.cs                # global_settings
│   │   ├── AdminAudit.cs                    # admin_audits
│   │   └── NewsPart.cs
│   │
│   ├── DTOs/                        # контракты запросов/ответов
│   │   ├── AuthDtos.cs               # регистрация, вход, валидация токена
│   │   ├── AdminDto.cs               # запросы админ-панели (бан, серверы)
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
│   │   ├── AppDbContext.cs                  # DbContext и конфигурация модели
│   │   └── IdentitySequence.cs              # перемотка sequences после удалений
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
| Базовый URL (docker) | `http://localhost:5000` |
| Префикс | `/api/v1.0` |
| Аутентификация | `Authorization: Bearer <jwt>` |
| Формат | JSON, `camelCase` |

### Аутентификация

Токен содержит `sub` (id пользователя), `UserId`, имя пользователя, роль (`admin`/`user`)
и `jti`. Срок жизни — **30 дней**. Проверяются подпись, издатель, аудитория и срок;
допустимое отклонение часов — ноль.

### Ограничение частоты запросов

| Область | Лимит |
|---|---|
| Пути, начинающиеся с `/api` | **15 запросов / 10 секунд** на IP, очередь не принимается |
| Статика и прочее | 1000 запросов / 10 секунд (общий ключ) |

При превышении — **HTTP 429** с пустым телом.

### Режим технических работ

Если в `global_settings` включён `Maintenance`, все запросы к `/api` кроме
админ-авторизованных возвращают **HTTP 503**:

```json
{ "error": "MAINTENANCE", "message": "Server is under maintenance. Please try again later.", "request_id": "…" }
```

Администраторы блокировку обходят — иначе нельзя было бы её выключить. Статика админ-панели
и `/health` не затрагиваются. Флаг применяется сразу: его изменение обновляет кеш
конфигурации, который читает этот middleware.

### Ошибки

Необработанные исключения превращаются в:

```json
{ "status": 500, "request_id": "…", "message": "Internal server error. Please try again later." }
```

У конкретных эндпоинтов формат исторически различается — при интеграции учитывайте это:

| Стиль | Где | Пример |
|---|---|---|
| `{ "Error": "CODE", "Message": "…" }` | Админ-эндпоинты, сборки | `{"Error":"BUILD_NOT_FOUND"}` |
| `{ "status": 401, "message": "…" }` | Аутентификация, пользователь, глобальный обработчик | `{"status":401,"message":"Invalid username or password!"}` |
| `{ "error": "CODE", "message": "…" }` | `/servers`, техработы | `{"error":"SERVER_NOT_FOUND"}` |
| Обычный текст | `AdminModsController` (моды пресетов) | `Preset not found` |
| Пустое тело | Часть `AdminPresetsController`, `ServersController` | — |

`request_id` есть в теле глобальной ошибки и в заголовках логов — по нему можно найти
причину в логах приложения.

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
{ "login": "player", "password": "secret", "deviceID": "device-123" }
```

* `login` — сохраняется в нижнем регистре; оригинал остаётся как `realname`.
* Лимиты: 2 аккаунта на `deviceID`, 4 аккаунта с одного IP.
* Ответы: `200` `{success, message, userId, login}`, `409` — логин занят,
  `429` — превышен лимит, `400` — некорректные данные.

**`POST /login`**

```json
{ "login": "player", "password": "secret" }
```

* Ответ `200`: `{success, userId, login, token, expiresIn}` (`expiresIn` = 2 592 000).
* `401` — неверный логин/пароль; для забаненного — `401` с сообщением о бане.
* При успехе обновляются `ip` и `lastlogin`.

### Пользователь

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `GET` | `/api/v1.0/user_data` | авторизован | Имя и UUID текущего пользователя |
| `GET` | `/api/v1.0/user_data/statistics` | авторизован | Статистика текущего пользователя |
| `GET` | `/api/v1.0/{id}/public-key` | авторизован | Публичный ключ пользователя по id |

```json
// GET /user_data
{ "username": "player", "realName": "Player", "uuid": "…" }

// GET /user_data/statistics
{ "realName": "Player", "uuid": "…", "role": "user",
  "statistics": { "playTime": 0, "murders": 0, "deaths": 0 } }
```

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
| `GET` | `/api/v1.0/versions/minecraft?loader=fabric&includePrerelease=false` | admin | Версии Minecraft для загрузчика |
| `GET` | `/api/v1.0/versions/loader?loader=forge&mcVersion=1.21.1` | admin | Версии загрузчика для версии игры |

Известные загрузчики: `vanilla`, `fabric`, `forge`, `neoforge`.

### Сборки

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `POST` | `/api/v1.0/builds` | admin | Поставить сборку в очередь |
| `GET` | `/api/v1.0/builds` | admin | Все сборки с прогрессом |
| `GET` | `/api/v1.0/builds/{id}` | admin | Статус одной сборки (id — число) |
| `GET` | `/api/v1.0/builds/{id}/logs?afterId=0&limit=200` | admin | Журнал обработки |
| `DELETE` | `/api/v1.0/builds/{id}` | admin | Удалить сборку, файлы и объекты S3 |
| `GET` | `/api/v1.0/builds/{buildId}/manifest` | **анонимно** | Манифест готовой сборки (buildId — строка) |

> Обратите внимание: `/builds/{id}` — это числовой PK в БД, а `/builds/{buildId}/manifest` —
> строковый `buildId` из 32 hex-символов. Это разные идентификаторы.

**`POST /builds`**

```json
{ "name": "Tech pack", "mcVersion": "1.21.1",
  "loader": "neoforge", "loaderVersion": "21.1.256", "buildPresetId": null }
```

`loader` допускает только `vanilla`, `fabric`, `forge`, `neoforge`.

**`DELETE /builds/{id}`** удаляет строки БД, локальную папку сборки, загруженный архив
модпака и объекты S3. Активную сборку удалить нельзя — `409 BUILD_IN_PROGRESS`.

### Администрирование: пользователи

| Метод | Путь | Описание |
|---|---|---|
| `GET` | `/api/v1.0/admin/users?page=1&pageSize=20` | Список с пагинацией (`pageSize` максимум 100) |
| `GET` | `/api/v1.0/admin/users/search?query=abc` | Поиск, максимум 10 результатов |
| `POST` | `/api/v1.0/admin/users/{id}/ban` | Бан: `{reason, durationDays}` (`null` — навсегда) |
| `POST` | `/api/v1.0/admin/users/{id}/unban` | Снять все баны |
| `PATCH` | `/api/v1.0/admin/users/{id}/role` | `{role: "admin"\|"user"}` |
| `PATCH` | `/api/v1.0/admin/users/{id}/change-password` | `{newPassword}` (минимум 6 символов) |

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
`custom-artifacts/{sha256}{ext}` (дедупликация по хешу).

### Администрирование: импорт модпаков

| Метод | Путь | Описание |
|---|---|---|
| `POST` | `/api/v1.0/admin/builds/import-modpack/modrinth` | Импорт по slug/ID с Modrinth |
| `POST` | `/api/v1.0/admin/builds/import-modpack/file` | Импорт загруженного `.mrpack` или `.zip` |

Подробно — в разделе [Импорт модпаков](#импорт-модпаков).

### Диагностика и системные

| Метод | Путь | Доступ | Описание |
|---|---|---|---|
| `GET` | `/api/v1.0/ip` | admin | IP клиента и все заголовки (отладка прокси) |
| `GET` | `/health` | анонимно | Проверка состояния сервиса **и соединения с БД** (Npgsql) |

В ответе `/health` есть заголовок `api-supported-versions`. Эндпоинт не попадает под
техработы и под лимит частоты запросов для `/api`.

---

## Сервисы

| Сервис | Зона ответственности |
|---|---|
| `AdminBootstrapService` | Создаёт первого админа при старте, если админов нет |
| `JwtService` | Выпуск и проверка JWT |
| `UserService` | Управление пользователями: пагинация, поиск, бан, роль, пароль, аудит |
| `ServerService` | CRUD серверов, привязка сборок, обновление кеша, аудит |
| `ConfigCacheService` | Кеш активных серверов и флага maintenance (+ зеркало в Redis) |
| `ConfigUpdateService` | Фоновое обновление кеша раз в минуту и рассылка в Redis |
| `ApplicationStatusService` | Фасад над кешем для панели |
| `AdminAuditService` | Запись действий админов; ошибки аудита не ломают запрос |
| `VersionCatalogService` | Версии Minecraft и загрузчиков из Mojang/Fabric/Forge/NeoForge |
| `BuildWorkerService` | Фоновая очередь сборок, статусы, ретраи |
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
`/health` → `ForwardedHeaders` → CORS → аутентификация → rate limiter →
`ApiLockMiddleware` → авторизация → контроллеры → статика.

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
     `java -jar <installer> --installClient <buildDir>`. Перед запуском создаётся
     `launcher_profiles.json`, иначе установщик отказывается работать. Java ищется в
     `JAVA_HOME`, затем в `PATH`, затем в стандартных каталогах (`Program Files`,
     `Eclipse Adoptium`, `Microsoft`) или `/usr/lib/jvm/*`.
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
старте. Скачивание идемпотентно: уже скачанные файлы с верным хешем пропускаются,
частичные пишутся в `*.part`.

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
# в .env рядом с docker-compose.yml
CurseForgeApiKey=$2a$10$...
```

> **Важно:** символ `$` в значении нужно экранировать как `$$`, иначе docker compose
> воспримет его как начало подстановки переменной и обрежет значение. Либо передавайте
> ключ через `CurseForge__ApiKey` как переменную окружения.

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
   пропускается. Для CurseForge каждая пара `projectID`/`fileID` резолвится через
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

Файлы лежат в MinIO по ключу `builds/{buildId}/{путь}` и раздаются через nginx
(`http://localhost:8080/builds/...`) с кешированием, поддержкой `Range` и CORS-заголовками
для лаунчера. URL собираются на сервере из `S3__CdnBaseUrl`.

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
| **Users** | Список, поиск, бан/разбан, смена роли и пароля |
| **Settings** | Переключение режима технических работ |

Вход в панель использует тот же `/api/v1.0/login`, что и игра; токен хранится в
`localStorage` под ключом `admin_token`.

---

## База данных

| Таблица | Назначение | Ключевые поля |
|---|---|---|
| `user_accounts` | Аккаунты | `id`, `player_uuid`, `username` (unique, lower-case), `realname`, `hash_password`, `user_role` (`admin`/`user`, по умолчанию `user`), `regdate`, `lastlogin`, `ip`, `regip`, `email`, `device_id`, `PublicKey` |
| `user_data` | Статистика игроков | `user_id` (PK и FK), `play_time`, `murders`, `deaths` |
| `user_bans` | Баны | `user_id` (FK), `reason`, `banned_by`, `created_at`, `expires_at` (NULL — навсегда), `is_active` |
| `server_manifests` | Игровые серверы | `id`, `name` (уникально), `type` (загрузчик), `app_version` (версия игры), `ip_address`, `is_active`, `build_manifest` (text), `build_id` (привязка сборки, максимум 64) |
| `server_configs` | Настройки сервера | `server_id` (PK и FK), `type_version` (версия загрузчика), `config_hash_sum` |
| `global_settings` | Глобальные настройки | `setting_key` (PK), `setting_value` (в том числе `Maintenance`) |
| `admin_audits` | Аудит действий админов | `actor`, `action`, `target_type`, `target_id`, `details`, `created_at` |
| `build_instances` | Сборки | `id`, `name`, `mc_version`, `loader_type`, `loader_version`, `status`, `build_id` (unique, максимум 100), `manifest_json` (text), `error_message` (`попытка\|ошибка`), `build_preset_id`, `modpack_import` + колонки живого прогресса |
| `build_files` | Файлы сборки | `build_id` (FK), `path` (максимум 500), `sha256` (индексирован — дедупликация), `size`, `policy`, `s3_key` |
| `build_logs` | Журналы сборок | `build_instance_id` (FK, индексирован), `level`, `message`, `created_at` |
| `build_presets` | Пресеты сборок | `name`, `game_version`, `loader_type`, `loader_version`, `created_at`, `updated_at` |
| `build_preset_mods` | Моды пресетов | `id` (Guid), `build_preset_id` (FK), `source_type` (`Modrinth`/`CustomUpload`), `target_folder`, `modrinth_slug_or_id`, `custom_file_s3_key`, `sha256_hash`, `file_size`, `is_enabled` |

### Связи

```
user_accounts  1─1  user_data            (каскадное удаление)
user_accounts  1─N  user_bans            (каскадное удаление)
server_manifests 1─1 server_configs      (каскадное удаление)
build_presets  1─N  build_preset_mods    (каскадное удаление)
build_instances 1─N  build_files          (каскадное удаление)
build_instances 1─N  build_logs           (каскадное удаление)
build_instances N─1  build_presets        (SetNull: сборка переживает удаление пресета)
server_manifests ──build_id──► build_instances.build_id   (мягкая ссылка, без FK)
```

`global_settings` и `admin_audits` самостоятельны.

### Обслуживание идентификаторов

PostgreSQL-последовательности умеют только расти, поэтому после удаления последней строки
номера продолжали бы расти бесконечно. `IdentitySequence.RewindAsync` перематывает
последовательность обратно к текущему максимуму (для таблиц сборок, логов, пресетов и
серверов). Ошибка перемотки никогда не должна ломать само удаление, поэтому вызовы обёрнуты
в `try/catch`.

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
| Пароли | Только BCrypt-хеши |
| Токены | JWT HS256, проверка подписи, издателя, аудитоции и срока |
| Ключ JWT | Приложение отказывается стартовать с ключом короче 32 символов |
| Авторизация | Роль `admin` проверяется на всех административных эндпоинтах |
| Rate limiting | 15 запросов / 10 секунд на IP для `/api` |
| Техработы | Блокировка публичного API, админы остаются доступны |
| Ошибки | Наружу не отдаются внутренние детали, попадает `request_id` |
| Аудит | Действия админов пишутся в `admin_audits` |
| Регистрация | Лимиты на устройство и IP, хеширование пароля |
| Обновление пароля админом | Требует минимум 6 символов |

### Рекомендации для продакшена

1. **Только HTTPS** через обратный прокси с сертификатом.
2. **Ограничить CORS** конкретными доменами в `CORS__AllowedOrigins`.
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

---

## Docker

### Сервисы

| Сервис | Образ | Порт (host) | Роль |
|---|---|---|---|
| `api` | собирается из `Dockerfile` | `5000` → 8080 | REST API и админ-панель |
| `postgres` | `postgres:16` | `5432` | База данных |
| `minio` | `dhi.io/minio:latest` | `9000`, `9001` | S3-хранилище файлов |
| `nginx` | `nginx:alpine` | `8080` → 80 | CDN, кеш, CORS |

Тома: `postgres_data`, `minio_data`, `builds_data`.

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

## Разработка

### Проекты в решении

| Проект | Назначение |
|---|---|
| `UnderMineApi` | Основное веб-приложение |
| `ManifestGenerator` | CLI-утилита: строит манифест из готовой папки сборки |

Отдельного проекта с автотестами в решении нет; проверка изменений выполняется сборкой
(`dotnet build`) и интеграционными проверками через запущенный стек.

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

- **Swagger/OpenAPI не настроен.** Документация ведётся в этом файле; при необходимости
  добавьте `Swashbuckle`/`NSwag` в `Program.cs`.
- **Форматы ошибок различаются** между эндпоинтами: `{Error, Message}`, `{status, message}`
  или просто текст. Это исторически сложилось и не унифицировано.
- **Файлы модов пресетов** ограничены лимитом Kestrel (~128 МБ) по умолчанию, хотя
  сервис допускает до 200 МБ — при необходимости поднимите `MaxRequestBodySize`.
- **Сборки больших модпаков** (несколько тысяч файлов) чувствительны к длительности этапа
  выгрузки в S3; при сбое фазы S3 настоящая ошибка записывается в лог сборки.
- **Наследие Forge/NeoForge** (например, Minecraft 1.12.2) может требовать Java 8 на этапе
  установщика, тогда как в контейнере стоит Temurin 21 — такие сборки стоит проверять
  отдельно.
- **Импорт по ссылке CurseForge** (по `projectID`/`slug`) не реализован — только загрузка
  готового архива.