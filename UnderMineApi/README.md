# UnderMineApi — Серверная часть API

## Описание

API для управления пользователями/аккаунтами и предоставления клиентам конфигурации игровых серверов и статистики через REST API.

## Основные компоненты

- `AuthController` — регистрация/вход/проверка токена.
- `UserController` — данные о пользователе, статистика.
- `ServersController` — получение списка серверов и конфигов.
- `AdminController` — администрирование пользователей, баны с доказательствами, серверы, maintenance mode.
- `AdminAccessController` — эффективные права текущего аккаунта и настройка прав ролей.
- `AdminActivityController` — журнал действий администраторов с фильтрами и сводкой.
- `AdminBuildEditorController` — файловый редактор сборок.
- `ConfigCacheService` — in-memory кеш конфигурации серверов с возможностью синхронизации через Redis и механизма pub/sub.
- `PermissionService` — права ролей `moderator`/`user`; роль `admin` неявно имеет все права.
- EF Core (PostgreSQL) — автоматическое применение миграций: `UserAccount`, `UserData`, `UserIpHistory`, `RolePermission`, `UserBanEvidence`, `ServerManifest`, `ServerConfigDetails`, `UserBans`, `AdminAudit`, `GlobalSetting`.

## Основные технологии

- JWT-аутентификация (Bearer). Токен передаётся в HTTP-заголовке `Authorization: Bearer <token>`.
- Хеширование паролей: BCrypt (`BcryptPasswordHasher`).
- Поддержка Redis (опционально) для распределённого кеша конфигураций и pub/sub уведомлений об изменениях.
- Автоматическое применение EF Core миграций при запуске (`context.Database.Migrate()`).
- Rate limiting (глобальный middleware) и healthchecks (`/health`).
- Аудит-лог действий через `AdminAuditService`; отдельная страница **Activity** с фильтрами
  и сводкой.
- Permission-политики для админ-эндпоинтов: `servers.view/create/edit`, `builds.upload`,
  `presets.create`, `users.ban/change_password/change_role`, `activity.view`.

## Зависимости

- .NET 9 (проект в разработке).
- PostgreSQL (основная база данных).
- Redis (опционально, используется для кеширования конфигураций).
- dotnet CLI для управления зависимостями.

## Конфигурация (переменные окружения / appsettings)

- `ConnectionStrings:PostgreConnection` — connection string к PostgreSQL.
- `ConnectionStrings:Redis` — строка подключения Redis (опционально). В качестве альтернативы можно задать секцию `Redis` с `Host`, `Port`, `Username`, `Password`, `Ssl`.
- `Jwt:Key` — секретный ключ для подписи JWT.
- `Jwt:Issuer` — издатель JWT.
- `Jwt:Audience` — аудитория JWT.
- `ASPNETCORE_ENVIRONMENT` — окружение (Development/Production).

## Безопасность и секреты

- Для локальной разработки используйте `dotnet user-secrets` или `.env`/переменные окружения. Для развёртывания в продакшене и CI используйте секреты контейнера/CI или другие безопасные хранилища.

## Запуск проекта

1. Установите PostgreSQL и создайте базу.
2. Настройте переменные окружения: задайте `ConnectionStrings__PostgreConnection` и `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience`.
   - Пример с `dotnet user-secrets` (в папке проекта):
     ```bash
     dotnet user-secrets init
     dotnet user-secrets set "ConnectionStrings:PostgreConnection" "Host=localhost;Database=undermine;Username=postgres;Password=secret"
     dotnet user-secrets set "Jwt:Key" "super-secret-key"
     dotnet user-secrets set "Jwt:Issuer" "undermine"
     dotnet user-secrets set "Jwt:Audience" "undermine-audience"
     ```
3. Запустите проект:
   ```bash
   dotnet run --project UnderMineApi/UnderMineApi.csproj
   ```
   При первом запуске будут применены миграции. Если база данных недоступна, в логах будет ошибка.

## Миграции

- В проекте есть папка `Migrations`. При каждом изменении модели запускайте `context.Database.Migrate()`.
- Для создания новых миграций используйте EF Core CLI:
  ```bash
  dotnet tool install --global dotnet-ef
  dotnet ef database update --project UnderMineApi/UnderMineApi.csproj
  dotnet ef migrations add <Name> --project UnderMineApi/UnderMineApi.csproj
  ```

## Развёртывание (Docker)

Есть базовый `docker-compose.yml` для поднятия окружения (postgres + redis + приложение):

```yaml
version: '3.8'
services:
  db:
    image: postgres:15
    environment:
      POSTGRES_DB: undermine
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: secret
    ports:
      - "5432:5432"
  redis:
    image: redis:7
    ports:
      - "6379:6379"
  api:
    build: .
    working_dir: /app
    command: dotnet UnderMineApi.dll
    depends_on:
      - db
      - redis
    environment:
      ConnectionStrings__PostgreConnection: "Host=db;Database=undermine;Username=postgres;Password=secret"
      Redis__Host: "redis"
      Jwt__Key: "super-secret-key"
      Jwt__Issuer: "undermine"
      Jwt__Audience: "undermine-audience"
    ports:
      - "5000:80"
```

Примечание: образ API должен собираться через multi-stage Dockerfile, чтобы выполнять `dotnet publish`.

## Эндпоинты (обзор)

- Auth
  - `POST /api/v1.0/register` — регистрация: `{ login, password, deviceId }`.
  - `POST /api/v1.0/login` — вход: `{ login, password, deviceId? }`. Возвращает JWT `{ token }`.
  - `POST /api/v1.0/validate-token` — проверка токена.

- Users
  - `GET /api/v1.0/user_data` — данные текущего пользователя (auth).
  - `GET /api/v1.0/user_data/statistics` — статистика пользователя.
  - `GET /api/v1.0/{id}/public-key` — публичный ключ пользователя.

- Servers
  - `GET /api/v1.0/servers` — список серверов (кешируется ConfigCacheService).
  - `GET /api/v1.0/servers/{id}` — детали сервера.

- Admin (роли `admin`/`moderator`/`user`, доступ по правам)
  - `GET /api/v1.0/admin/me` — роль и эффективные права текущей учётной записи
    (используется админ-панелью для скрытия недоступных разделов и действий).
  - `GET /api/v1.0/admin/permissions` — список прав и текущие наборы для ролей
    `moderator` и `user` (только администратор).
  - `PUT /api/v1.0/admin/permissions/{role}` — заменить набор прав роли
    `moderator` или `user` телом `{ "permissions": ["servers.view", ...] }`
    (только администратор). Роль `admin` всегда имеет все права.
  - `GET /api/v1.0/admin/activity` — журнал действий администраторов (`activity.view`):
    пагинация, фильтры `actor`, `action`, `targetType`, `query`, глубина `days`,
    а также сводка (`todayCount`, `last7DaysCount`, `topActions`, `topActors`)
    и варианты для фильтров.
  - `GET /api/v1.0/admin/users` — список пользователей (пагинация; право
    `users.ban`, `users.change_password` или `users.change_role`).
  - `GET /api/v1.0/admin/users/{id}/statistics` — персональная статистика:
    дата регистрации, последний вход, playtime, убийства/смерти, текущий бан,
    история IP-адресов и история банов с причинами, сроками и доказательствами.
  - `POST /api/v1.0/admin/users/{id}/ban` — блокировка пользователя (`users.ban`).
    Multipart-запрос: `reason` (10–2000 символов), необязательный `expiresAt`
    (Unix UTC; пусто = навсегда) и 1–5 файлов `evidence` (до 10 МБ каждый,
    до 40 МБ суммарно; изображения, PDF, текст/логи, JSON/CSV, ZIP, DOC/DOCX,
    MP4/WebM). Файлы хранятся в PostgreSQL, а не в публичном S3-бакете.
  - `GET /api/v1.0/admin/ban-evidence/{id}` — скачивание доказательства
    (`users.ban`). Доступно только через API с авторизацией.
  - `POST /api/v1.0/admin/users/{id}/unban` — разблокировка пользователя (`users.ban`).
  - `PATCH /api/v1.0/admin/users/{id}/role` — смена роли на `user`, `moderator`
    или `admin` (`users.change_role`). Назначать `admin` и менять администраторов
    может только администратор.
  - `POST /api/v1.0/admin/users/{id}/delete` — безвозвратное удаление аккаунта:
    требует `confirmUsername` (повторить username) и `reason` (5–500 символов).
    Нельзя удалить себя или администратора; сначала нужно снять роль admin.
    Удаляются статистика и история банов, старые JWT перестают работать,
    действие пишется в `admin_audits` в той же транзакции. Только администратор.
  - `PATCH /api/v1.0/admin/users/{id}/change-password` — смена пароля пользователем
    (`users.change_password`). Пароль администратора может менять только администратор.
  - `POST /api/v1.0/admin/servers` — создание сервера (`servers.create`; обновляет кеш и рассылает в Redis для синхронизации).
  - `PUT /api/v1.0/admin/servers/{id}` — обновление сервера (`servers.edit`).
  - `DELETE /api/v1.0/admin/servers/{id}` — удаление сервера (`servers.edit`).
  - `POST /api/v1.0/admin/status/maintenance?enabled=true|false` — включение/выключение maintenance mode (только администратор; сохраняется в `GlobalSettings`).

  Права: `servers.view`, `servers.create`, `servers.edit`, `builds.upload`,
  `presets.create`, `users.ban`, `users.change_password`, `users.change_role`,
  `activity.view`.
  Роль `admin` имеет все права неявно; для `moderator` при миграции заводятся
  права по умолчанию (всё, кроме смены паролей, смены ролей и журнала Activity).

## Примеры curl

- Регистрация:
  ```bash
  curl -X POST http://localhost:5000/api/v1.0/register \
    -H "Content-Type: application/json" \
    -d '{"login":"test","password":"pass123","deviceId":"dev1"}'
  ```

- Вход:
  ```bash
  curl -X POST http://localhost:5000/api/v1.0/login \
    -H "Content-Type: application/json" \
    -d '{"login":"test","password":"pass123"}'
  ```

## Заметки по работе

- Логи сервера выводятся в stdout (настраивается дополнительно в `Program.cs`).
- Если Redis недоступен — приложение работает в режиме single-node (без кеша конфигураций). В этом случае конфигурации будут загружаться из БД.
- Healthcheck доступен на `/health`.
- Rate limiting ограничивает частоту запросов с одного IP и может быть настроен дополнительно middleware.

## Структура проекта (основные папки)

- `Controllers/` — контроллеры API.
- `Services/` — сервисный слой и вспомогательные классы (JWT, admin audit и т.д.).
- `Models/`, `Domain/Entities/` — модели данных и сущности БД.
- `Data/` — `AppDbContext`.
- `Migrations/` — EF Core миграции.
- `Middleware/` — пользовательские middleware (логирование запросов, обработка ошибок, блокировка API и т.д.).

## Тестирование

В решении есть проект `UnderMineApi.Tests` (xUnit, SQLite in-memory) — 100+ тестов на
аутентификацию, безопасность, статистику, удаление аккаунтов, баны с доказательствами,
права ролей и аудит. Запуск:

```bash
dotnet test UnderMineApi.sln
```

## Лицензия

В проекте нет явной лицензии. Проверьте файл `LICENSE` или уточните у автора проекта, если планируете использовать код.

---

Если нужны дополнительные инструкции — добавьте `Dockerfile`, `docker-compose.yml` или другие конфигурационные файлы для развёртывания/окружения.
