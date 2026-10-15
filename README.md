# ApiUnderMine

Серверная часть (бэкенд) для игровой/социальной платформы **UnderMine**. Предоставляет REST API для аутентификации пользователей, управления аккаунтами, игровыми серверами и статистикой, а также инструменты для администраторов.

---

## Содержание

- [Описание](#описание)
- [Технологии](#технологии)
- [Требования](#требования)
- [Быстрый старт](#быстрый-старт)
- [Конфигурация](#конфигурация)
- [Структура проекта](#структура-проекта)
- [API Эндпоинты](#api-эндпоинты)
- [Логирование](#логирование)
- [Безопасность](#безопасность)
- [Миграции базы данных](#миграции-базы-данных)
- [Docker](#docker)
- [Тестирование](#тестирование)
- [Разработка](#разработка)

---

## Описание

`ApiUnderMine` — это полноценный бэкенд-сервер для игровой платформы, реализованный на **ASP.NET Core 9 (.NET 9)**. Сервис предоставляет следующие возможности:

### Основные функции

- **Аутентификация** — регистрация и вход пользователей с JWT-токенами
- **Проверка токенов** — валидация выданных JWT-токенов
- **Пользовательские данные** — хранение и получение профиля, UUID, статистики игрока
- **Игровые серверы** — список активных серверов с кешированием (Redis опционально)
- **Администрирование** — управление пользователями, серверами, режим технических работ

### Ключевые особенности

- **JWT-аутентификация** — Bearer-токены с проверкой издателя, аудитории и срока действия
- **BCrypt** — безопасное хеширование паролей
- **Rate Limiting** — защита от злоупотреблений (15 запросов / 10 секунд на IP)
- **Health Checks** — мониторинг состояния сервиса (`/health`)
- **Maintenance Mode** — блокировка API во время технических работ
- **Конфигурация через `.env`** — все секреты и настройки вынесены в переменные окружения

---

## Технологии

| Технология | Назначение |
|---|---|
| **.NET 9 / ASP.NET Core** | Основной фреймворк |
| **Entity Framework Core 9 + Npgsql** | ORM для работы с PostgreSQL |
| **JWT (JSON Web Tokens)** | Аутентификация |
| **BCrypt.Net-Next** | Хеширование паролей |
| **PostgreSQL** | Основная база данных |
| **Redis** (опционально) | Распределённый кеш конфигураций |
| **DotNetEnv** | Загрузка конфигурации из `.env` файлов |
| **ASP.NET Core Health Checks** | Мониторинг состояния |
| **Rate Limiting** | Забота от злоупотреблений |

---

## Требования

Для запуска проекта необходимо:

- **.NET 9 SDK** — [скачать](https://dotnet.microsoft.com/download/dotnet/9.0)
- **PostgreSQL 14+** — [скачать](https://www.postgresql.org/download/)
- **Docker** (опционально) — для запуска в контейнерах

---

## Быстрый старт

### 1. Клонирование репозитория

```bash
git clone https://gitlab.api-undermine.ru/SKUF/apiundermine.git
cd apiundermine
```

### 2. Настройка окружения

Создайте файл `.env` в корне проекта (шаблон ниже) или скопируйте из примера:

```bash
# Database
ConnectionStrings__PostgreConnection=Host=localhost;Port=5432;Database=undermine;Username=postgres;Password=your_secure_password

# JWT (minimum 32 characters!)
Jwt__Key=your-super-secret-jwt-key-min-32-chars-long!!
Jwt__Issuer=ApiUnderMine
Jwt__Audience=ApiUnderMine

# Redis (optional)
Redis__Host=localhost
Redis__Port=6379
Redis__Username=
Redis__Password=
Redis__Ssl=false

# CORS (comma-separated list of allowed origins)
# CORS:AllowedOrigins=https://yourdomain.com,https://app.yourdomain.com

# Logging
Logging__LogLevel__Default=Information
Logging__LogLevel__Microsoft.AspNetCore=Warning

# ASP.NET Core
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_HTTP_PORTS=8080
ASPNETCORE_HTTPS_PORTS=8081
```

### 3. Запуск базы данных

**Вариант А — локальный PostgreSQL:**

```bash
docker run -d \
  --name undermine-db \
  -p 5432:5432 \
  -e POSTGRES_PASSWORD=your_secure_password \
  -e POSTGRES_DB=undermine \
  postgres:15
```

**Вариант Б — установка PostgreSQL:**

Установите PostgreSQL вручную и создайте базу данных `undermine`.

### 4. Запуск приложения

```bash
# Восстановление зависимостей
dotnet restore

# Применение миграций базы данных
dotnet ef database update --project UnderMineApi/UnderMineApi.csproj

# Запуск
dotnet run --project UnderMineApi/UnderMineApi.csproj
```

Приложение будет доступно по адресу: `http://localhost:8080`

### 5. Проверка работоспособности

```bash
# Health check
curl http://localhost:8080/health

# Ожидаемый ответ: Healthy
```

---

## Конфигурация

Все настройки проекта задаются через файл `.env` или переменные окружения.

### Приоритет конфигурации

```
.env файл > Переменные окружения > appsettings.json
```

### Параметры конфигурации

| Параметр | Описание | Обязательный | По умолчанию |
|---|---|---|---|
| `ConnectionStrings__PostgreConnection` | Строка подключения к PostgreSQL | Да | — |
| `Jwt__Key` | Секретный ключ для подписи JWT (мин. 32 символа) | Да | — |
| `Jwt__Issuer` | Издатель JWT-токенов | Да | `ApiUnderMine` |
| `Jwt__Audience` | Аудитория JWT-токенов | Да | `ApiUnderMine` |
| `Redis__Host` | Хост Redis | Нет | — |
| `Redis__Port` | Порт Redis | Нет | `6379` |
| `Redis__Username` | Имя пользователя Redis | Нет | — |
| `Redis__Password` | Пароль Redis | Нет | — |
| `Redis__Ssl` | Использовать SSL для Redis | Нет | `false` |
| `CORS__AllowedOrigins` | Разрешённые источники (через запятую) | Нет | AllowAll |
| `Logging__LogLevel__Default` | Уровень логирования по умолчанию | Нет | `Information` |
| `Logging__LogLevel__Microsoft.AspNetCore` | Уровень логирования ASP.NET | Нет | `Warning` |
| `ASPNETCORE_ENVIRONMENT` | Окружение (Development/Production) | Нет | `Production` |
| `ASPNETCORE_HTTP_PORTS` | HTTP порт | Нет | `8080` |
| `ASPNETCORE_HTTPS_PORTS` | HTTPS порт | Нет | `8081` |

---

## Структура проекта

```
apiundermine/
│
├── .env                              # Секреты и настройки окружения
├── .gitignore                        # .env добавлен в игнор
├── docker-compose.yml                # Оркестрация Docker-контейнеров
├── Dockerfile                        # Docker-образ приложения
│
├── UnderMineApi/                    # Основной проект
│   ├── Program.cs                    # Точка входа, конфигурация middleware
│   ├── UnderMineApi.csproj           # Файл проекта
│   │
│   ├── appsettings.json              # Базовая конфигурация (без секретов)
│   ├── appsettings.Development.json  # Конфигурация для разработки
│   │
│   ├── Controllers/                  # Контроллеры API
│   │   ├── AuthController.cs         # Аутентификация (register, login, validate-token)
│   │   ├── UserController.cs         # Данные пользователя
│   │   ├── ServersController.cs      # Список серверов
│   │   ├── AdminController.cs        # Администрирование
│   │   └── DebugController.cs        # Отладка (только для админов)
│   │
│   ├── Services/                     # Бизнес-логика
│   │   ├── JwtService.cs             # Генерация и валидация JWT
│   │   ├── UserService.cs            # Управление пользователями
│   │   ├── ServerService.cs          # Управление серверами
│   │   ├── ConfigCacheService.cs     # Кеширование конфигураций
│   │   ├── ConfigUpdateService.cs    # Фоновое обновление конфигурации
│   │   ├── ApplicationStatusService.cs # Статус приложения
│   │   ├── AdminAuditService.cs      # Аудит действий админов
│   │   ├── GetUuid.cs                # Генерация UUID
│   │   └── PasswordHasher/           # Хеширование паролей
│   │
│   ├── Middleware/                    # Middleware компоненты
│   │   ├── RequestLoggingMiddleware.cs # Логирование запросов
│   │   ├── GlobalExceptionHandler.cs  # Глобальная обработка ошибок
│   │   └── ApiLockMiddleware.cs       # Блокировка API (maintenance mode)
│   │
│   ├── Models/                        # Модели данных
│   │   ├── UserAccount.cs             # Пользователь
│   │   ├── UserData.cs                # Статистика игрока
│   │   ├── UserBans.cs                # Баны
│   │   ├── ServerManifest.cs          # Сервер
│   │   ├── ServerConfigDetails.cs     # Конфигурация сервера
│   │   ├── GlobalSettings.cs          # Глобальные настройки
│   │   └── AdminAudit.cs              # Аудит действий
│   │
│   ├── Domain/                        # Доменные сущности
│   │   └── Entities/
│   │       └── UserAccount.cs          # EF Core сущность пользователя
│   │
│   ├── Data/
│   │   └── AppDbContext.cs            # Контекст базы данных
│   │
│   ├── DTOs/                          # Объекты передачи данных
│   │   ├── AuthDtos.cs                # DTO для аутентификации
│   │   ├── ServerDTO.cs               # DTO для серверов
│   │   └── AdminDto.cs                # DTO для администрирования
│   │
│   ├── Extensions/                    # Расширения
│   │   ├── StructuredLoggerExtensions.cs
│   │   └── ServiceResultExtensions.cs
│   │
│   └── Migrations/                    # Миграции EF Core
│       ├── AppDbContextModelSnapshot.cs
│       ├── 20260413113629_InitialCreate.cs
│       ├── 20260422155059_SplitServerManifestAndConfig.cs
│       ├── 20260621174830_FixServerManifests.cs
│       └── 20261009093052_RemoveChatAndFriendships.cs
│
└── UnderMineApi.Tests/                # Юнит-тесты (xUnit)
    ├── AuthControllerTests.cs
    ├── AdminControllerTests.cs
    ├── ReceivingServerDataTests.cs
    └── DebugControllerTests.cs
```

---

## API Эндпоинты

### Общие сведения

- **Базовый URL:** `http://localhost:8080`
- **Версия API:** `v1.0`
- **Аутентификация:** `Authorization: Bearer <jwt-token>`

### Аутентификация

#### Регистрация

```
POST /api/v1.0/register
```

**Тело запроса:**
```json
{
  "login": "username",
  "password": "password123",
  "deviceID": "device-123"
}
```

**Ограничения:**
- До 2 аккаунтов на устройство
- До 4 аккаунтов с одного IP
- Пароль хешируется через BCrypt

**Ответ:**
```json
{
  "success": true,
  "message": "Registration successful!"
}
```

#### Вход

```
POST /api/v1.0/login
```

**Тело запроса:**
```json
{
  "login": "username",
  "password": "password123"
}
```

**Ответ:**
```json
{
  "success": true,
  "token": "<jwt-token>"
}
```

#### Проверка токена

```
POST /api/v1.0/validate-token
```

**Заголовок:** `Authorization: Bearer <jwt-token>`

**Ответ:**
```json
{
  "valid": true,
  "message": "Token is valid"
}
```

---

### Пользовательские данные

#### Получение данных пользователя

```
GET /api/v1.0/user_data
```

**Ответ:**
```json
{
  "username": "RealName",
  "uuid": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
}
```

#### Получение статистики

```
GET /api/v1.0/user_data/statistics
```

**Ответ:**
```json
{
  "username": "RealName",
  "uuid": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "role": "user",
  "statistics": {
    "playTime": 12345,
    "murders": 678,
    "deaths": 90
  }
}
```

#### Получение публичного ключа

```
GET /api/v1.0/{id}/public-key
```

**Ответ:**
```json
{
  "publicKey": "..."
}
```

---

### Серверы

#### Список серверов

```
GET /api/v1.0/servers
```

**Ответ:**
```json
[
  {
    "id": "1",
    "serverName": "Example Server",
    "gameVersion": "1.0.0",
    "loaderType": "forge",
    "ipAddress": "127.0.0.1",
    "config": null
  }
]
```

#### Детали сервера

```
GET /api/v1.0/servers/{id}
```

**Ответ:**
```json
{
  "id": "1",
  "serverName": "Example Server",
  "gameVersion": "1.0.0",
  "loaderType": "forge",
  "ipAddress": "127.0.0.1",
  "config": {
    "loaderVersion": "1.0"
  }
}
```

---

### Администрирование

> **Примечание:** Все админ-эндпоинты требуют роль `admin` в JWT-токене.

#### Список пользователей

```
GET /api/v1.0/admin/users?page=1&pageSize=20
```

#### Поиск пользователей

```
GET /api/v1.0/admin/users/search?query=name
```

#### Бан пользователя

```
POST /api/v1.0/admin/users/{id}/ban
```

**Тело запроса:**
```json
{
  "reason": "Cheating",
  "durationDays": 7
}
```

#### Разбан пользователя

```
POST /api/v1.0/admin/users/{id}/unban
```

#### Смена роли

```
PATCH /api/v1.0/admin/users/{id}/role
```

**Тело запроса:**
```json
{
  "role": "admin"
}
```

#### Смена пароля

```
PATCH /api/v1.0/admin/users/{id}/change-password
```

**Тело запроса:**
```json
{
  "newPassword": "newPassword123"
}
```

#### Управление серверами

```
POST   /api/v1.0/admin/servers          # Создать сервер
PUT    /api/v1.0/admin/servers/{id}     # Обновить сервер
DELETE /api/v1.0/admin/servers/{id}     # Удалить сервер
GET    /api/v1.0/admin/servers          # Список всех серверов
```

#### Режим технических работ

```
POST /api/v1.0/admin/status/maintenance?enabled=true|false
```

---

### Пресеты сборок

> **Примечание:** Все эндпоинты пресетов требуют роль `admin` в JWT-токене.

#### Список пресетов

```
GET /api/v1.0/admin/presets
```

#### Создание пресета

```
POST /api/v1.0/admin/presets
```

**Тело запроса:**
```json
{
  "name": "My Preset",
  "gameVersion": "1.21.1",
  "loaderType": "fabric",
  "loaderVersion": "0.19.5"
}
```

#### Обновление пресета

```
PUT /api/v1.0/admin/presets/{id}
```

#### Удаление пресета

```
DELETE /api/v1.0/admin/presets/{id}
```

---

### Моды пресетов

> **Примечание:** Все эндпоинты модов требуют роль `admin` в JWT-токене.

#### Список модов пресета

```
GET /api/v1.0/admin/presets/{presetId}/mods
```

#### Поиск модов на Modrinth

```
GET /api/v1.0/admin/modrinth/search?query=sodium&mcVersion=1.21.1&loader=fabric
```

#### Добавление мода из Modrinth

```
POST /api/v1.0/admin/presets/{presetId}/mods/modrinth
```

**Тело запроса:**
```json
{
  "slugOrId": "fabric-api",
  "targetFolder": "mods"
}
```

#### Загрузка кастомного мода

```
POST /api/v1.0/admin/presets/{presetId}/mods/custom
```

**Content-Type:** `multipart/form-data`

**Поля формы:**
- `file` — файл мода (`.jar` для модов, `.zip` для шейдеров)
- `targetFolder` — целевая папка (`mods`, `shaderpacks`, `resourcepacks`)

#### Удаление мода из пресета

```
DELETE /api/v1.0/admin/presets/{presetId}/mods/{modId}
```

#### Переключение статуса мода

```
PATCH /api/v1.0/admin/presets/{presetId}/mods/{modId}/toggle
```

**Тело запроса:**
```json
{
  "enabled": true
}
```

---

### Импорт модпаков (.mrpack и CurseForge)

> **Примечание:** Все эндпоинты требуют роль `admin` в JWT-токене. Импортируется готовый
> модпак в одном из двух форматов, формат определяется по содержимому архива:
>
> - **Modrinth `.mrpack`** — индекс `modrinth.index.json` + папки `overrides/`,
>   `client-overrides/`;
> - **CurseForge** — `manifest.json` + `modlist.html` + `overrides/`. Такой архив не содержит
>   самих модов: файлы перечислены только парами `projectID`/`fileID`, а ссылки на них
>   резолвятся через CurseForge API.
>
> Сервер сам определяет Minecraft-версию и модлоадер, готовит движок, скачивает все клиентские
> файлы с проверкой хешей (параллельно, 8 потоков), применяет `overrides` поверх скачанного и
> формирует обычную сборку (`BuildInstance` со `buildType = "ModpackImport"`).

#### Настройка CurseForge API (только для импорта сборок CurseForge)

CurseForge отдаёт файлы только по ключу, который выдаётся бесплатно на
<https://console.curseforge.com/>. Ключ задаётся одним из способов:

- в `appsettings.json`: `{ "CurseForge": { "ApiKey": "..." } }`
- переменной окружения `CurseForge__ApiKey`
- в `docker-compose.yml` через `.env` рядом с файлом: `CurseForgeApiKey=...`

Без ключа импорт `.mrpack` работает как раньше, а загрузка сборки CurseForge отклоняется сразу
при аплоаде с понятной ошибкой (`MODPACK_IMPORT_FAILED`), не доходя до воркера.

#### Импорт по Slug/ID с Modrinth

```
POST /api/v1.0/admin/builds/import-modpack/modrinth
```

**Тело запроса:**
```json
{
  "name": "Fabulously Optimized",   // опционально, по умолчанию — имя модпака из индекса
  "slug": "fabulously-optimized",   // slug или ID проекта на Modrinth
  "versionId": null,                // опционально, конкретная версия; иначе — последний релиз
  "serverId": null                  // опционально, закрепить сервер за новой сборкой
}
```

**Ответ:** `200` — карточка созданной сборки (`status: "pending"`, `buildType: "ModpackImport"`).
**Ошибки:** `400` — неверный индекс/пустой slug, `404` — проект или версия не найдены,
`429` — rate limit Modrinth API.

#### Импорт по загруженному файлу

```
POST /api/v1.0/admin/builds/import-modpack/file
```

**Content-Type:** `multipart/form-data`

**Поля формы:**
- `file` — файл модпака: `.mrpack` (Modrinth) либо `.zip` (сборка CurseForge), до 1 ГБ.
  Формат определяется по содержимому архива, а не по расширению.
- `name` — опционально, имя сборки
- `serverId` — опционально, ID сервера для закрепления

**Ответ:** `200` — карточка созданной сборки. **Ошибки:** `400` — не `.mrpack`/`.zip`, пустой
файл, архив без `modrinth.index.json` и `manifest.json`, неподдерживаемый загрузчик
(`quilt`/`liteloader`/`rift`), либо не настроенный/отклонённый ключ CurseForge; `404` — сервер
не найден.

#### Сценарий сборки

1. **Движок** — ванильный клиент + точная установка модлоадера: из `dependencies` индекса
   Modrinth (`fabric-loader` / `forge` / `neoforge`) или из `minecraft.modLoaders[primary]`
   манифеста CurseForge (`forge-1.12.2-14.23.5.2860` → forge `14.23.5.2860`; лишний префикс с
   версией игры отбрасывается). Без ключа загрузчика — vanilla, `quilt-loader` и прочие
   неизвестные загрузчики не поддерживаются.
2. **Файлы** — параллельное скачивание с проверкой хешей; уже скачанные файлы с верным хешем
   пропускаются (повторные попытки дёшевы). Для Modrinth пустой `downloads` резолвится по
   `sha1` через `/v2/version_file/{hash}`. Для CurseForge каждый `projectID`/`fileID`
   резолвится через `GET /v1/mods/{modID}/files/{fileID}` (6 параллельных запросов, повторы
   при `429`/`5xx`), полученные файлы кладутся в `mods/` и проверяются по `sha1` и размеру;
   если CurseForge не вернул `downloadUrl`, ссылка собирается из id файла
   (`mediafilez.forgecdn.net/files/{id/1000}/{id%1000}/{имя}`). Хеша нет — проверка идёт
   только по размеру, о чём пишется в лог сборки. Любой неразрешённый файл **проваливает
   импорт** с указанием количества и первой ошибки: молчаливо неполная сборка недопустима.
3. **Overrides** — у `.mrpack`: `overrides/` копируется в корень сборки, затем
   `client-overrides/` поверх него; у CurseForge: единственная папка из
   `manifest.overrides` (обычно `overrides/`). Защита от zip-slip.
4. **Финализация** — обычный пайплайн: хеширование SHA-256, дедуплицированная заливка
   в MinIO, клиентский `manifest.json`. Политики: `config/`, `mods/`, `shaderpacks/`,
   `resourcepacks/` — `FORCE_SYNC`; `options.txt`, `launcher_profiles.json` — `DOWNLOAD_ONCE`.

Загруженный архив (любого формата) сохраняется в `{Build:RootPath}/imports/{buildId}.mrpack`
до успешного завершения сборки (нужен для повторных попыток), затем удаляется вместе с очисткой
просроченных файлов при старте воркера. Удаление сборки (`DELETE /builds/{id}`)
удаляет и остаточный архив.

---

### Системные эндпоинты

#### Health Check

```
GET /health
```

**Ответ:** `Healthy` / `Unhealthy`

---

## Логирование

Проект использует **структурированное логирование** Microsoft Extensions Logging. Все сообщения логов написаны на английском языке.

### Формат логов

```
[2026-10-09 09:53:00.238] info: RequestLoggingMiddleware[0]
      Request started: GET /api/v1.0/servers [RequestId: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx]

[2026-10-09 09:53:00.245] info: RequestLoggingMiddleware[0]
      Request completed: GET /api/v1.0/servers [StatusCode: 200] [RequestId: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx] [Duration: 7ms]
```

### Что логируется

| Событие | Уровень | Описание |
|---|---|---|
| Начало запроса | `Information` | Метод, путь, RequestId |
| Завершение запроса | `Information` | Метод, путь, статус код, длительность в ms, RequestId |
| Ошибка запроса | `Error` | Исключение, стек-трейс |
| Maintenance mode | `Warning` | Запрос отклонён из-за техработ |
| Обновление кэша | `Information` | Количество серверов, статус maintenance |
| Бан/разбан | `Information` | ID пользователя, актор |
| Смена пароля | `Information` | ID пользователя, актор |
| Смена роли | `Information` | ID пользователя, старая/новая роль, актор |

### Настройка уровня логирования

Через `.env` файл:

```env
Logging__LogLevel__Default=Information
Logging__LogLevel__Microsoft.AspNetCore=Warning
```

Доступные уровни: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`

---

## Безопасность

### Реализованные меры

| Мера | Описание |
|---|---|
| **JWT-аутентификация** | Bearer-токены с проверкой издателя, аудитории, срока действия и подписи |
| **BCrypt** | Пароли хранятся только в виде хешей |
| **Rate Limiting** | 15 запров на 10 секунд с одного IP |
| **CORS** | Настраиваемый список разрешённых источников |
| **Валидация JWT ключа** | Приложение не запустится с ключом короче 32 символов |
| **Глобальная обработка ошибок** | Отсутствие утечки внутренней информации наружу |
| **Аудит действий** | Все действия админов логируются |
| **Maintenance Mode** | Возможность заблокировать API для пользователей |

### Рекомендации для продакшена

1. **Всегда используйте HTTPS** — настройте reverse proxy (nginx, caddy) с SSL
2. **Ограничьте CORS** — укажите конкретные домены в `CORS__AllowedOrigins`
3. **Используйте  strong JWT Key** — минимум 32 символа, лучше 64+
4. **Регулярно обновляйте зависимости** — `dotnet outdated` / `dotnet list package --vulnerable`
5. **Мониторинг** — настройте сбор логов (ELK, Grafana Loki, etc.)
6. **Резервное копирование** — регулярные бэкапы PostgreSQL

---

## Миграции базы данных

### Создание миграции

```bash
dotnet ef migrations add <MigrationName> --project UnderMineApi/UnderMineApi.csproj
```

### Применение миграций

```bash
dotnet ef database update --project UnderMineApi/UnderMineApi.csproj
```

### История миграций

| Миграция | Описание |
|---|---|
| `20260413113629_InitialCreate` | Создание всех таблиц |
| `20260422155059_SplitServerManifestAndConfig` | Разделение ServerManifest и ServerConfig |
| `20260621174830_FixServerManifests` | Исправление таблицы серверов |
| `20261009093052_RemoveChatAndFriendships` | Удаление таблиц чата и друзей |

---

## Docker

### Запуск через docker-compose

```bash
docker compose up --build
```

### Сервисы в docker-compose

| Сервис | Описание | Порт |
|---|---|---|
| `api` | Основное API | 8080 |
| `db` | PostgreSQL | 5432 |
| `redis` | Redis (опционально) | 6379 |

### Переменные окружения Docker

```yaml
environment:
  ConnectionStrings__PostgreConnection: "Host=db;Database=undermine;Username=postgres;Password=secret"
  Jwt__Key: "super-secret-key"
  Jwt__Issuer: "ApiUnderMine"
  Jwt__Audience: "ApiUnderMine"
```

---

## Тестирование

### Запуск тестов

```bash
dotnet test UnderMineApi.Tests/UnderMineApi.Tests.csproj
```

### Покрытие кода

```bash
dotnet test --collect:"XPlat Code Coverage"
```

### Существующие тесты

- `AuthControllerTests` — тесты аутентификации
- `AdminControllerTests` — тесты администрирования
- `ReceivingServerDataTests` — тесты получения данных серверов
- `DebugControllerTests` — тесты отладочного контроллера

---

## Разработка

### Рекомендуемые инструменты

- **IDE:** Visual Studio 2022, Rider, или VS Code с C# Dev Kit
- **Базы данных:** DBeaver, pgAdmin, или Azure Data Studio
- **API тестирование:** Postman, Insomnia, или Bruno

### Полезные команды

```bash
# Восстановление зависимостей
dotnet restore

# Сборка
dotnet build

# Запуск
dotnet run --project UnderMineApi/UnderMineApi.csproj

# Запуск в режиме разработки
dotnet watch run --project UnderMineApi/UnderMineApi.csproj

# Добавление миграции
dotnet ef migrations add <Name> --project UnderMineApi/UnderMineApi.csproj

# Удаление последней миграции
dotnet ef migrations remove --project UnderMineApi/UnderMineApi.csproj

# Генерация SQL скрипта
dotnet ef migrations script --project UnderMineApi/UnderMineApi.csproj

# Проверка устаревших пакетов
dotnet list package --outdated

# Проверка уязвимостей
dotnet list package --vulnerable
```

### Соглашения по коду

- Испование `camelCase` для локальных переменных и параметров
- Использование `PascalCase` для методов, свойств и классов
- Все публичные методы должны иметь XML-документацию
- Логи — только на английском языке
- Все секреты — только через `.env`
