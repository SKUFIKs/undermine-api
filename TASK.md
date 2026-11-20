# TASK — undermine-api

Список задач по итогам ревизии проекта. Формат: `ID · приоритет · оценка` (S — до часа, M — несколько часов, L — день+).

## Легенда

- **P0** — безопасность/данные/прод-блокер, делать первым.
- **P1** — надёжность, эксплуатация, заметный техдолг.
- **P2** — качество кода, документация, полировка.
- Отмечать выполнение: `- [x]`.

## Как проверять

```bash
dotnet build UnderMineApi.sln
dotnet test                     # после добавления тестового проекта
docker compose config           # валидация compose
docker compose up -d --build    # smoke-тест стека
```

---

## P0 — Безопасность

- [x] **API-01 · P0 · M — Forwarded headers и сетевая экспозиция**
  - Проблема: `Program.cs` очищает `KnownNetworks/KnownProxies` и ставит `ForwardLimit = null`, при этом API опубликован на `5000`. `X-Forwarded-For` можно подделать и обойти лимиты по IP.
  - Файлы: `UnderMineApi/Program.cs`, `UnderMineApi/Extensions/ForwardedHeadersExtensions.cs`, `docker-compose.yml`, `nginx.conf`, `.env.example`, `README.md`.
  - Сделать: доверять только внутренней сети/адресу nginx (настройка `ForwardedHeaders:KnownProxies`), в прод-профиле не публиковать `5000` наружу (только nginx), задокументировать.
  - Готово, когда: подставной `X-Forwarded-For` не меняет `RemoteIpAddress`; rate-limit и лимит регистраций считаются по реальному IP.
  - **Сделано:** добавлена конфигурация `ForwardedHeaders:*` (по умолчанию — только loopback, `ForwardLimit=1`); compose использует отдельную сеть `frontend` с подсетью `PROXY_SUBNET`, в которую входят только nginx и api; API/Postgres/MinIO слушают `127.0.0.1`; nginx проксирует `/api/` и `/admin/` с корректными `X-Forwarded-*`. Проверено: `dotnet build` (0 ошибок), `docker compose config`, `nginx -t`.

- [x] **API-02 · P0 · M — Проверка бана на каждом запросе**
  - Проблема: JWT живёт 30 дней; бан проверяется только в `login` и `user_data`. Забаненный продолжает пользоваться `/servers`, `/servers/{id}/manifest` и т.п.
  - Файлы: `Program.cs`, `Middleware/AccountBanMiddleware.cs`, `Services/UserAccessService.cs`, `Services/Interfaces/IUserAccessService.cs`, `Controllers/AuthController.cs`, `Services/UserService.cs`, `Services/AdminBootstrapService.cs`.
  - Сделать: при валидации токена подгружать пользователя, проверять активный бан и роль; кэшировать результат на 30–60 с. Возвращать `403 ACCOUNT_BANNED` (или `401` с корректным кодом).
  - Готово, когда: забаненный с валидным токеном получает отказ на всех защищённых `/api` эндпоинтах; `validate-token` тоже сообщает о бане.
  - **Сделано:** добавлен `AccountBanMiddleware` (работает после `UseAuthentication`/rate limiter, до maintenance/authorization). Проверяет существование аккаунта, активный бан и совпадение роли в БД с ролью в токене; состояние кэшируется в `IMemoryCache` (`Access__BanCacheSeconds`, 30 с) и инвалидируется при бане/разбане/смене роли. `validate-token` теперь тоже проверяет бан/роль/существование аккаунта. Login при бане отвечает `403 ACCOUNT_BANNED`. Bootstrap-админ при восстановлении снимает активные баны со своего аккаунта. Проверено: `dotnet build` (0 ошибок) + harness middleware (7/7 PASS: бан/удаление/смена роли/чистый пользователь/AllowAnonymous/аноним/не-API путь).

- [x] **API-03 · P0 · M — Лимиты регистрации и доверие к `DeviceId`**
  - Проблема: `DeviceId` приходит от клиента, лимит 2 аккаунта/устройство обходится сменой `MachineName`/CPU count. Лимит по IP обходится через API-01.
  - Файлы: `Controllers/AuthController.cs`, `Services/Registration/*`, `Program.cs`, `Domain/Entities/UserAccount.cs`, `Data/AppDbContext.cs`, `Migrations/*_AddRegistrationLimits.cs`, `README.md`, `.env.example`, `docker-compose.yml`.
  - Сделать: серверный лимит по IP/подсети (например, /24), валидация формата/длины `DeviceId`, отдельный строгий rate-limit на `register`; предусмотреть конфигурируемые лимиты.
  - Готово, когда: смена device-id не позволяет обойти лимит; значения лимитов настраиваются через конфиг; поведение описано в README.
  - **Сделано:** `RegistrationOptions` (все лимиты из конфига); `RegistrationPolicy` валидирует `deviceID` (16–128, charset); `RegistrationIp` нормализует IPv4-mapped IPv6 и считает лимит по подсети (IPv4 /32 по умолчанию, IPv6 /64) с корректной маской для небайтовых префиксов; конкурентные регистрации сериализуются PostgreSQL advisory-блокировками, добавлен отдельный rate-limit `register` по подсети; добавлены колонка `regip_prefix`, индексы по `device_id`/`regip`/`regip_prefix` и миграция `AddRegistrationLimits` (с безопасным усечением старых длинных device_id). Проверено на временном PostgreSQL: уникальные device-id + смена IPv4, лимиты `DEVICE_LIMIT`/`IP_LIMIT`, 10 параллельных регистраций дают ровно 4 успеха и 6×429, 11-я упирается в rate-limit; `dotnet build` — 0 ошибок.

- [x] **API-04 · P0 · S — Раздельные политики rate limiting**
  - Проблема: единые `15 запросов / 10 c` на все `/api` мешают админке (поллинг сборок/логов) и не дают отдельной защиты от брутфорса.
  - Файлы: `UnderMineApi/Extensions/RateLimitingExtensions.cs`, `Program.cs`, контроллеры (`[EnableRateLimiting]`), `README.md`, `.env.example`, `docker-compose.yml`.
  - Сделать: политики `auth` (жёсткая), `api` (обычная), `admin` (мягче для поллинга), `static`; заголовок `Retry-After` при 429; учитывать пользователя, если он авторизован.
  - Готово, когда: админка не ловит 429 при обычной работе; перебор пароля упирается в лимит.
  - **Сделано:** `AddConfiguredRateLimiting` с политиками `api` (120/60 с), `admin` (600/60 с), `auth` (20/60 с), `register` (10/600 с), `static` (1000/10 с); авторизованные запросы партиционируются по `UserId`, анонимные — по нормализованной подсети (IPv6 /64). Политики навешаны на контроллеры через `[EnableRateLimiting]` (публичный манифест сборки использует `api`). `OnRejected` отдаёт `429 JSON` (`RATE_LIMITED`, `retry_after`, `request_id`) и заголовок `Retry-After`. Важно: глобальный limiter тоже применяется к endpoint’ам с named policy, поэтому для них он возвращает `GetNoLimiter` — иначе глобальное окно `api` было бы жёстким потолком для админки/auth/register. Проверено на временном PostgreSQL: auth/admin/register держат свои окна и `Retry-After` (7/11/5), global api — 9; 429 содержит JSON; `dotnet build` — 0 ошибок.

- [x] **API-05 · P0 · S — CORS, TLS и security headers**
  - Проблема: при пустом `CORS:AllowedOrigins` включается «разрешить всё» + `AllowCredentials`; TLS отсутствует; админка без CSP.
  - Файлы: `Program.cs`, `Extensions/CorsExtensions.cs`, `Middleware/SecurityHeadersMiddleware.cs`, `nginx.conf`, `nginx.tls.conf.example`, `docker-compose.yml`, `docker-compose.tls.yml`, `.gitignore`, `wwwroot/admin/`.
  - Сделать: в Production требовать явный список origins; добавить security headers (`X-Content-Type-Options`, `Referrer-Policy`, CSP для `/admin/`); описать/добавить TLS-терминацию (nginx + пример конфига).
  - Готово, когда: в Production нет wildcard+credentials; CSP не ломает SPA.
  - **Сделано:** CORS вынесен в `AddConfiguredCors`: явный allow-list, `AllowCredentials` только opt-in, в Production без origins — same-origin (никаких CORS-заголовков), в Development — любой origin без credentials. Добавлен `SecurityHeadersMiddleware`: CSP, `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy`, `Permissions-Policy`, COOP/CORP, HSTS только при HTTPS. Добавлены `nginx.tls.conf.example` + `docker-compose.tls.yml` (HTTP→HTTPS, TLS 1.2/1.3, HSTS), `certs/` в `.gitignore`, обновлены README/.env.example/compose. Проверено: `dotnet build` (0 ошибок), `docker compose config`, `nginx -t` для TLS-конфига, harness заголовков/CORS.

- [x] **API-06 · P0 · M — Харденинг аутентификации**
  - Проблема: пароль минимум 6 символов, без максимума (BCrypt режет >72 байт), нет смены пароля пользователем и отзыва сессий.
  - Файлы: `Controllers/AuthController.cs`, `Controllers/UserController.cs`, `Services/UserService.cs`, `Services/JwtService.cs`, `Services/Security/PasswordPolicy.cs`, `Services/PasswordHasher/*`, `Middleware/AccountBanMiddleware.cs`, `Domain/Entities/UserAccount.cs`, `Migrations/*_AddAuthHardening.cs`.
  - Сделать: политика 8–72 байта (или pre-hash), `PATCH /user/password`, версия токена/`security stamp` для инвалидизации, dummy-BCrypt для несуществующего логина (выравнивание времени ответа).
  - Готово, когда: смена пароля инвалидирует старые токены; неизвестный логин отвечает за сопоставимое время.
  - **Сделано:** `PasswordPolicy` (8–72 байта UTF-8) для регистрации и смены пароля; `IPasswordHasher.VerifyPassword` с безопасной обработкой битых хешей/длинных паролей; dummy-BCrypt для неизвестного логина; `PATCH /api/v1.0/user/password` с проверкой текущего пароля и выдачей нового JWT; поле `token_version` в БД/JWT, проверка в `AccountBanMiddleware` и `validate-token`; инкремент версии при смене/сбросе пароля (включая bootstrap). Проверено на временном PostgreSQL: weak password → 400, неверный текущий пароль → 401, успешная смена → 200 + новый токен, старый токен → 401 TOKEN_REVOKED, вход по старому паролю → 401, по новому → 200; `dotnet build` — 0 ошибок.

- [x] **API-07 · P0 · S — XSS в админ-панели**
  - Проблема: `app.js` активно использует `innerHTML` с данными из БД (имена серверов, пользователей, логи сборок). Токен лежит в `localStorage`.
  - Файлы: `wwwroot/admin/app.js`, `wwwroot/admin/index.html`, `Middleware/SecurityHeadersMiddleware.cs`.
  - Сделать: проверить все интерполяции, применять `esc()`/`textContent`; добавить CSP; рассмотреть хранение токена вне `localStorage` (минимально — короткий TTL + предупреждение).
  - Готово, когда: сервер с именем `<img src=x onerror=alert(1)>` не выполняет скрипт.
  - **Сделано:** все `onclick` с пользовательскими строками заменены на `data-action` + `data-*` и делегированный обработчик; значения проходят через `esc()` в HTML-атрибутах и не попадают в JS-код. Для `/admin/` CSP теперь `script-src 'self'` (без `unsafe-inline`); Swagger сохраняет отдельную политику. Токен по-прежнему в `localStorage` — вынесение в HttpOnly-cookie требует переработки auth-контракта и остаётся отдельной задачей. `node --check app.js` — OK; `dotnet test` — 26 тестов.

---

## P1 — Надёжность и эксплуатация

- [x] **API-08 · P1 · M — Миграции fail-fast + healthchecks**
  - Проблема: `Database.Migrate()` обёрнут в try/catch и не останавливает приложение при ошибке.
  - Файлы: `Program.cs`, `Extensions/DatabaseMigrationExtensions.cs`, `HealthChecks/S3HealthCheck.cs`, `Services/S3StorageService.cs`, `docker-compose.yml`, `Dockerfile`, `.env.example`, `README.md`.
  - Сделать: ретраи подключения (например, 10× с backoff), затем падение; `healthcheck` для postgres/minio/api; `restart: unless-stopped`; `depends_on: condition: service_healthy`.
  - Готово, когда: API не стартует со старой схемой; compose переживает рестарт БД.
  - **Сделано:** `MigrateDatabaseWithRetryAsync` (по умолчанию 10 попыток × 2 с, конфигурируется), после чего исключение пробрасывается и `Environment.ExitCode=1`; health-эндпоинты `/health/live`, `/health/ready`, `/health` (readiness — PostgreSQL, полный — ещё и MinIO); Docker healthchecks для postgres/minio/api/nginx, `restart: unless-stopped`, `depends_on: service_healthy`; в API-образе оставлен `curl` для healthcheck. S3-проверка использует HTTP `/minio/health/live`, потому что Minio-клиент при недоступном сервере молча возвращал «успех». Проверено: при остановленном MinIO `/health` → 503, `/health/ready` → 200; при закрытой БД API завершается с кодом 1 после ретраев; `dotnet build` — 0 ошибок; `docker compose config` — OK.

- [x] **API-09 · P1 · M — Защита build-worker от нескольких реплик**
  - Проблема: воркер берёт `pending` без блокировки; две реплики API обработают одну сборку дважды.
  - Файлы: `Services/BuildWorkerService.cs`, `Services/BuildLockService.cs`, `Services/Interfaces/IBuildLockService.cs`, `Program.cs`.
  - Сделать: `SELECT ... FOR UPDATE SKIP LOCKED` / advisory lock / статус `leased` с владельцем и таймаутом.
  - Готово, когда: при двух процессах каждая сборка обрабатывается один раз (проверить тестом/локально).
  - **Сделано:** `BuildLockService` берёт PostgreSQL session advisory lock по `build_instances.id` на отдельном соединении (`Pooling=false`, heartbeat `SELECT 1` раз в 30 с). `BuildWorkerService` выбирает pending-сборки пакетом, под локом перечитывает статус, обрабатывает только захваченную; занятые другой репликой пропускает. Восстановление `downloading`/`hashing` выполняется при старте и периодически при простое, но только если удалось взять лок — иначе сборка считается живой в другом процессе. Проверено harness’ом на временном PostgreSQL: A берёт lock, B получает `null`, другой build берётся независимо, после dispose лок освобождается; сборка и запуск API с новой DI — 0 ошибок.

- [x] **API-10 · P1 · M — Ретеншен логов и пагинация сборок**
  - Проблема: `build_logs` растёт без ограничений; `GET /builds` отдаёт все сборки.
  - Файлы: `Controllers/BuildsController.cs`, `Services/BuildLogRetentionService.cs`, `Data/AppDbContext.cs`, `wwwroot/admin/app.js`, `index.html`, `styles.css`, `README.md`, `.env.example`, `docker-compose.yml`.
  - Сделать: пагинация/фильтры `/builds`; фоновая очистка логов старше N дней и/или ограничение на сборку; индексы под запросы.
  - Готово, когда: список сборок не деградирует на больших объёмах; логи не растут бесконечно.
  - **Сделано:** `GET /builds` теперь возвращает `{items,totalCount,page,pageSize,totalPages}` с `page`/`pageSize` (1–100), фильтрами `status` и `query` (ILIKE по имени); админка получила пагинатор и фильтр статуса, dashboard использует `totalCount`. Добавлен `BuildLogRetentionService`: раз в `Build__LogRetentionIntervalHours` удаляет логи старше `Build__LogRetentionDays` и опционально ограничивает строки на сборку (`Build__LogRetentionMaxPerBuild`). Добавлены индексы `build_instances(created_at)`, `(status, created_at)` и `build_logs(created_at)` (миграция `AddBuildQueryIndexes`). Проверено на временном PostgreSQL: 5 сборок, страницы 2/2/1, фильтры `failed`/`query`, старый лог удалён, свежий остался; `node --check app.js` — OK; `dotnet build` — 0 ошибок.

- [x] **API-11 · P1 · S — Отказаться от переиспользования ID**
  - Проблема: `IdentitySequence.RewindAsync` делает удалённые ID снова валидными (аудит/ссылки/логи).
  - Файлы: `Controllers/BuildsController.cs`, `Controllers/AdminPresetsController.cs`, `Services/ServerService.cs`, `Data/IdentitySequence.cs` (удалён), `README.md`.
  - Сделать: убрать rewind или заменить на «отображаемый номер» без влияния на PK.
  - Готово, когда: удаление не меняет последовательность PK.
  - **Сделано:** удалены все вызовы `RewindAsync` (сборки/файлы/логи, пресеты, серверы); `IdentitySequence.cs` удалён; из `AdminPresetsController` убрана ставшая ненужной зависимость логгера. README обновлён: ID никогда не переиспользуются, удалённые номера «сгорают». Проверено: `dotnet build` — 0 ошибок, 0 предупреждений; поиск по `IdentitySequence|RewindAsync` пуст.

- [x] **API-12 · P1 · S — Единый формат URL для CDN**
  - Проблема: `GetPublicUrl` отдаёт `/{bucket}/builds/...`, а `nginx.conf` и README описывают `/builds/...` со срезанием бакета.
  - Файлы: `Services/S3StorageService.cs`, `nginx.conf`, `README.md`, `BUILD_SERVICE.md`.
  - Сделать: выбрать один вариант (рекомендую срезать бакет в nginx), поправить `GetPublicUrl`, убрать мёртвый `location /builds/` или привести его в соответствие.
  - Готово, когда: URL из манифеста открывается и кэшируется через ожидаемый location.
  - **Сделано:** `GetPublicUrl` больше не добавляет имя бакета: `<CdnBaseUrl>/builds/{buildId}/...`, что соответствует `location /builds/` в nginx (бакет срезается). Старые манифесты с `/minecraft-builds/builds/...` работают через общий `location /`. Обновлены README и комментарии. Проверено harness’ом: `https://cdn.example.com/builds/abc/mods/example.jar`; `dotnet build` — 0 ошибок.

- [x] **API-13 · P1 · S — Гигиена Docker/nginx**
  - Проблема: `dhi.io/minio:latest` без версии, nginx монтирует `minio_data` без необходимости, `minio_image.tar` (71 МБ) в рабочей копии.
  - Файлы: `docker-compose.yml`, `nginx.conf`, `.gitignore`, `.dockerignore`, `.env.example`, `README.md`.
  - Сделать: зафиксировать версии образов, убрать лишний volume, добавить `minio_image.tar` в `.gitignore`/удалить.
  - Готово, когда: `docker compose config` чистый; сборка воспроизводима.
  - **Сделано:** образы вынесены в `POSTGRES_IMAGE` / `MINIO_IMAGE` / `NGINX_IMAGE`; nginx по умолчанию `1.27-alpine`, MinIO можно зафиксировать по digest; лишний `minio_data` volume у nginx убран ранее; `minio_image.tar` и локальный `builds/` исключены из git и Docker-контекста. `docker compose config` — OK.

- [x] **API-14 · P1 · M — Единый формат ошибок**
  - Проблема: 5 разных стилей (`Error/Message`, `status/message`, `error/message`, текст, пустое тело).
  - Файлы: `Middleware/ApiErrorNormalizationFilter.cs`, `Middleware/GlobalExceptionHandler.cs`, `Middleware/ApiLockMiddleware.cs`, `Program.cs` (JWT challenge/forbid), `README.md`.
  - Сделать: ProblemDetails-подобный конверт `{ error, message, request_id }` + хелперы; обновить README.
  - Готово, когда: все эндпоинты возвращают один конверт; ошибки описаны в одном разделе.
  - **Сделано:** `ApiErrorNormalizationFilter` централизованно приводит все MVC-ошибки к `{status,error,message,request_id,errors?}` (включая `ProblemDetails`/`ValidationProblemDetails` и текстовые ответы); JWT `OnChallenge`/`OnForbidden` отдают `401 UNAUTHORIZED`/`403 FORBIDDEN`; maintenance — `503 MAINTENANCE`; глобальный обработчик — `500 INTERNAL_ERROR`. Проверено на живом API: 401 без токена, 400 validation с `errors`, 403 для обычного пользователя, 404, 503 maintenance — все в едином конверте; `dotnet test` — 26 тестов.

- [x] **API-15 · P1 · S — OpenAPI/Swagger**
  - Проблема: `Asp.Versioning.Mvc.ApiExplorer` подключён, но спецификации нет.
  - Файлы: `UnderMineApi.csproj`, `Program.cs`.
  - Сделать: `Swashbuckle`/`Microsoft.AspNetCore.OpenApi`, скрыть в Production или закрыть admin-политикой.
  - Готово, когда: `/swagger` доступен в Development и отражает версии API.
  - **Сделано:** добавлен `Swashbuckle.AspNetCore` 7.2.0; `AddEndpointsApiExplorer()`/`AddSwaggerGen()`; `UseSwagger`/`UseSwaggerUI` только в Development. Проверено: `/swagger/index.html` → 200, `/swagger/v1/swagger.json` → 200; `dotnet build` — 0 ошибок.

- [x] **API-16 · P1 · S — DTO вместо EF-сущностей в админ-эндпоинтах**
  - Проблема: `AdminPresetsController` принимает `BuildPreset` напрямую (overposting, навигационные свойства).
  - Файлы: `Controllers/AdminPresetsController.cs`, `Controllers/UserController.cs`, `DTOs/PresetDto.cs`.
  - Сделать: request/response DTO, валидация, `ControllerBase` в `UserController`.
  - Готово, когда: клиент не может задать `Id`, `CreatedAt`, `Mods` и т.п.
  - **Сделано:** добавлен `BuildPresetUpsertDto` (Required/MaxLength/loader regex); create/update пресета принимают DTO, `Id`/`CreatedAt`/`Mods` задать нельзя; `CreatedAtAction` исправлен на `GetPreset`; `UserController` переведён на `ControllerBase`. `dotnet build` — 0 ошибок.

- [x] **API-17 · P1 · S — Аудит всех админ-действий**
  - Проблема: аудит есть для пользователей/серверов, но не для сборок, пресетов, maintenance.
  - Файлы: `Services/AdminAuditService.cs`, `Controllers/BuildsController.cs`, `Controllers/AdminPresetsController.cs`, `Controllers/AdminController.cs`, `Controllers/AdminModpackController.cs`.
  - Сделать: писать аудит на create/delete/retry/build-import/maintenance.
  - Готово, когда: действия видны в `admin_audits`.
  - **Сделано:** аудит добавлен на создание/удаление сборки, create/update/delete пресета, импорт модпака и переключение maintenance. Ошибки аудита по-прежнему не ломают запрос (логика в `AdminAuditService`). `dotnet build` — 0 ошибок.

- [x] **API-18 · P1 · M — Валидация конфигурации на старте**
  - Проблема: часть обязательных ключей проверяется вручную, часть падает позже (S3, CORS, Build path).
  - Файлы: `Program.cs`, `Options/StartupOptions.cs`, `Extensions/CorsExtensions.cs`.
  - Сделать: `IOptions` + `ValidateOnStart` для JWT, S3, Build, Bootstrap; понятные сообщения.
  - Готово, когда: приложение падает сразу с внятной ошибкой при неполном `.env`.
  - **Сделано:** `AddValidatedStartupOptions` с `ValidateOnStart` для `JwtOptions`, `S3Options`, `BuildOptions`, `BootstrapAdminOptions`; CORS origins проверяются на абсолютные http(s) URL. Ошибки конфигурации теперь падают на старте с понятным сообщением. `dotnet build`/`dotnet test` — 0 ошибок.

- [x] **API-24 · P1 · L — Полноценный редактор сборок в админке**
  - Проблема: админка позволяла только создать/удалить сборку и посмотреть готовый манифест; нельзя было менять настройки и файлы без пересборки.
  - Файлы: `Controllers/AdminBuildEditorController.cs`, `Services/BuildEditorService.cs`, `Services/BuildPathSafety.cs`, `DTOs/BuildEditorDto.cs`, `wwwroot/admin/app.js`, `wwwroot/admin/styles.css`, `Services/BuildManifestService.cs`, `Services/S3StorageService.cs`.
  - Сделать: вкладки Settings / Files / Manifest, безопасный список файлов, текстовый редактор, загрузка, выгрузка, удаление, переименование, обновление `build_files`/S3/`manifest_json` после каждого изменения.
  - Готово, когда: изменения сразу видны лаунчеру без полной пересборки; пути не выходят за пределы сборки; бинарные файлы не открываются как текст.
  - **Сделано:** кнопка `Edit` в таблице Builds открывает полноэкранный редактор. Реализованы `GET /api/v1.0/admin/builds/{id}/editor`, `PUT .../settings`, `GET .../manifest`, `GET/PUT .../files/content`, `POST .../files` (multipart), `GET .../files/download`, `DELETE .../files`, `POST .../files/rename`. После записи обновляются S3-объект, строка `build_files` и клиентский манифест; настройки меняют имя/версию загрузчика/пресет и `DisplayName` манифеста. Добавлены лимиты 2 МБ для текста и 256 МБ для загрузки, проверка UTF-8, защита путей `BuildPathSafety`, allow-list текстовых расширений, аудит действий. Заодно исправлен `ApiErrorNormalizationFilter`: `PhysicalFileResult` больше не превращается в `400 Bad Request` (это ломало также скачивание Java). Тесты: 73/73.
  - **Доработано:** файловый менеджер приведён к виду Pterodactyl: хлебные крошки, переход по подкаталогам, `Create Directory`, `Upload` в текущую папку, `New File`, меню действий `…` (Edit/Download/Rename/Delete), таблица с размерами и датой изменения. Добавлены `GET .../files?path=`, `POST .../directories`, `DELETE .../directories?path=`; пустые каталоги хранятся S3-маркерами с ключом `/`. Удаление сборки чистит весь S3-префикс, включая маркеры. Исправлена производительность большой сборки (4125 файлов): сборка и `build_files` читаются двумя запросами вместо медленного `Include` (38 с → ~0.3 с), листинг S3 — только на один уровень. Устранена ошибка загрузки пустых файлов (`ObjectSize=-1`).

---

## P2 — Качество, тесты, документация

- [x] **API-19 · P2 · L — Тестовый проект и CI**
  - Создать `UnderMineApi.Tests` (xUnit), покрыть: Auth (лимиты/бан), BuildWorker (retry/recover/lease), Manifest (политики/URL), S3 (dedup), ModpackImport (zip-slip).
  - CI: `.github/workflows/ci.yml` — `dotnet build` + `dotnet test` на PR.
  - **Сделано (базовый набор):** создан `UnderMineApi.Tests` (xUnit), 26 тестов: PasswordPolicy/BCrypt, JWT/token_version, RegistrationIp/DeviceId, S3 public URL, ForwardedHeaders, CORS. Проект добавлен в solution; CI запускает `dotnet build` + `dotnet test` + gitleaks. Интеграционные тесты BuildWorker/ModpackImport можно расширять отдельно.

- [x] **API-20 · P2 · S — Удалить мёртвый код**
  - `ApplicationStatusService`, дублирующий `ConfigurationBuilder` в `Program.cs`, неиспользуемые интерфейсы/поля.
  - Готово, когда: сборка без предупреждений, `grep` не находит неиспользуемых регистраций.
  - **Сделано:** удалён `ApplicationStatusService` (файл и регистрация), убраны неиспользуемые `_config` в `AuthController`, `_passwordHasher` в `AdminController`, `_cache` в `UserController`; комментарий в `ConfigCacheService` обновлён. `dotnet build` — 0 ошибок, 0 предупреждений.

- [x] **API-21 · P2 · S — Синхронизировать документацию**
  - README/BUILD_SERVICE/CLIENT_SYNC: реальные URL CDN, форматы ошибок, лимиты, актуальные статусы и env-переменные.
  - **Сделано:** README обновлён под единый формат ошибок, CDN-URL без бакета, healthchecks, ретеншен логов, rate limiting, регистрацию, TLS, Docker-образы; в BUILD_SERVICE убраны неиспользуемые статусы; в CLIENT_SYNC политики синхронизации приведены к фактическому `BuildManifestService`.

- [x] **API-22 · P2 · S — Гигиена репозитория и секретов**
  - Проверить, что `.env` не в индексе, добавить `.env*` правила, убрать логи/архивы, включить secret-scanning.
  - **Сделано:** `.env` не в индексе; `.env*`, `logs/`, `minio_image.tar` и локальный `builds/` в `.gitignore`; `minio_image.tar`/`builds` исключены из Docker-контекста; добавлен CI-workflow с `gitleaks` (`.github/workflows/ci.yml`).

- [x] **API-23 · P2 · S — Валидация манифеста/входных данных**
  - `POST /builds`: проверять loader/mcVersion по каталогу версий; `ModpackImport`: лимиты на число файлов/размер распаковки; отклонять подозрительные пути.
  - **Сделано:** `POST /builds` best-effort проверяет `mcVersion` через `VersionCatalogService` (`400 UNKNOWN_MC_VERSION`); `ModpackImportService` ограничивает индекс/манифест 20 000 файлами (плюс существующие лимит 1 ГБ и zip-slip-защита). `dotnet build` — 0 ошибок.

---

## Контракт с лаунчером (общие задачи)

- [ ] **CT-01 · P0 · S — Выровнять JSON-контракты**
  - `UserProfileDto`: лаунчер ждёт `stats`, API отдаёт `statistics`.
  - `ServerConfig`: лаунчер ждёт 6 полей, API отдаёт только `loaderVersion`.
  - `IPAddress/iPAddress`: задать явные `[JsonPropertyName]` с обеих сторон.
  - Готово, когда: интеграционные тесты/ручной прогон подтверждают десериализацию всех DTO.

- [ ] **CT-02 · P1 · S — Согласовать политики синхронизации**
  - API помечает `shaderpacks`/`resourcepacks` как `FORCE_SYNC`, лаунчер удаляет всё вне манифеста. Решить: `PRESERVE` для пользовательских папок или явное предупреждение в UI.

- [x] **CT-05 · P1 · M — Java runtime через API**
  - Проблема: лаунчер скачивал Temurin напрямую с Adoptium, без серверного кэша и контроля версий.
  - Файлы: `Controllers/JavaController.cs`, `Services/JavaRuntimeService.cs`, `Services/JavaRuntimeCatalog.cs`, `DTOs/JavaRuntimeDto.cs`, `Options/StartupOptions.cs`, `Program.cs`, `docker-compose.yml`, `.env.example`.
  - Сделать: API отдаёт метаданные и архив JRE, кэширует Temurin, проверяет контрольную сумму; лаунчер скачивает только через API.
  - **Сделано:** `GET /api/v1.0/java/{major}` (descriptor: версия, имя файла, размер, SHA-256, относительный `downloadUrl`) и `GET /api/v1.0/java/{major}/download` (auth, rate limit, range). API скачивает Temurin из Adoptium, проверяет SHA-256/размер и кэширует в `java_data`; поддерживаются `windows/linux/mac` и `x64/aarch64/x32/arm`. Лаунчер проверяет хеш перед распаковкой; при недоступном API остаются локальный/system Java. Тесты: 54/54, включая парсер Adoptium, кэш/повторную выдачу и обновление метаданных без повторного скачивания.

---

## Рекомендуемый порядок

1. API-01, API-02, API-05 (безопасность, быстрые победы).
2. API-03, API-04, API-06 (лимиты/аутентификация).
3. API-08, API-09 (надёжность деплоя и воркера).
4. CT-01 + API-16, API-14.
5. API-19 (тесты) и далее по списку.

---

# Инфраструктура и CI/CD (план внедрения)

> Цель: поднять сервис на 3 серверах в разных локациях с полным циклом CI/CD.
> Основной режим — **active-passive**: Швеция принимает трафик, Германия — warm standby,
> РФ — cold DR/последний резерв. Staging и мониторинг совмещены с резервными узлами.
>
> **Не делаем на первом этапе:** Kubernetes, managed PostgreSQL, MinIO в production,
> Tailscale, active-active. Это следующие этапы.

## Принятые решения

- **prod-1 (Швеция):** основной production; nginx + API + build worker + PostgreSQL primary.
- **prod-2 (Германия):** warm standby; nginx + API standby + PostgreSQL streaming replica.
  При необходимости здесь же можно держать staging в отдельном compose с лимитами ресурсов.
- **prod-3 (РФ):** cold disaster recovery / последний резерв; nginx + образ API, БД
  восстанавливается из локальных бэкапов. Здесь же MinIO replica, Uptime Kuma и
  Sentry/GlitchTip, чтобы мониторинг пережил падение prod-1 и prod-2.
- **PostgreSQL:** self-hosted: primary в Швеции, асинхронная реплика в Германии, cold DR в РФ.
  Без synchronous replication между Швецией и Германией.
- **Файлы сборок:** локальный MinIO на каждом сервере + асинхронная репликация между
  площадками. Cloudflare R2 в production не используем; при желании позже оставить как
  дополнительный offsite-backup.
- **Reverse proxy:** nginx на каждом сервере, TLS через Cloudflare Origin Certificate.
- **CI/CD:** GitHub Actions + GHCR + Ansible + Docker Compose.
- **Мониторинг:** Uptime Kuma (доступность) + Sentry/GlitchTip (ошибки приложения).
- **SSH:** ключи, fail2ban, при возможности Cloudflare Tunnel + Access. Tailscale не
  используем как обязательную зависимость.
- **Миграции БД:** отдельный one-shot шаг, не при старте каждой API-реплики.
- **Build worker:** только на prod-1. При failover включается на prod-2; на prod-3 — только
  если недоступны оба первых сервера.

## Итоговая схема

```text
                         Cloudflare DNS / TLS / WAF
                                    |
                 +------------------+------------------+
                 |                                     |
        prod-1 (Швеция)                         prod-2 (Германия)
   nginx -> API :8080                      nginx -> API :8080
        |       \                              |
        |        +--> MinIO (локально) <---------+ (файлы сборок)
        |             ^     ^
        |             |     |
        |       replication  replication
        |             |     |
        |         prod-2   prod-3
        |
   PostgreSQL primary
        |
        +---- async streaming replica ----> prod-2 PostgreSQL replica

        prod-3 (РФ) — cold DR / последний резерв
   nginx + API image (stopped until DR)
   PostgreSQL restore from локальных бэкапов
   MinIO replica + nginx для cdn-ru.example.com
   Uptime Kuma + Sentry/GlitchTip + staging optional on prod-2
```

DNS-имена (пример):

- `api.example.com` — production (prod-1 Швеция), проксируется Cloudflare.
- `staging.example.com` — staging на prod-2 (Германия), закрыт Cloudflare Access.
- `cdn.example.com` — nginx → локальный MinIO на prod-1/prod-2 (глобальный трафик).
- `cdn-ru.example.com` — nginx → локальный MinIO на prod-3 (РФ, без Cloudflare).
- `status.example.com` — Uptime Kuma на prod-3 (РФ), лучше за Cloudflare Access.

---

## OPS-01 · Подготовка репозитория и окружений

- [ ] Создать в репозитории каталоги:

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
    roles/
      common/
      docker/
      nginx/
      postgres/
      app/
  ```

- [ ] Создать GitHub Environments:
  - `staging` — без обязательного ревью, секреты staging.
  - `production` — required reviewer, секреты production.
- [ ] Создать пакет GHCR: `ghcr.io/<owner>/undermine-api`.
- [ ] Добавить `.env.production.example` со всеми обязательными ключами:
  - `ConnectionStrings__PostgreConnection`
  - `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience`
  - `S3__Endpoint`, `S3__AccessKey`, `S3__SecretKey`, `S3__Bucket`,
    `S3__UseSsl`, `S3__CdnBaseUrl`, `S3__PublicRead=true` (для локального MinIO)
  - `Build__RootPath=/app/builds`
  - `JavaRuntime__RootPath=/app/java`
  - `ForwardedHeaders__*`
  - `Access__BanCacheSeconds`, `CORS__*`, `RateLimiting__*`
- [ ] Убедиться, что `.env` и `deploy/.env*` не попадают в git.

**Готово, когда:** структура каталогов создана, окружения GitHub настроены, секреты не в
репозитории.

---

## OPS-02 · Ansible: базовая настройка серверов

- [ ] Описать `ansible/inventory.ini`:

  ```ini
  [prod_primary]
  prod-1-se ansible_host=<IP> ansible_user=deploy

  [prod_standby]
  prod-2-de ansible_host=<IP> ansible_user=deploy

  [prod_dr]
  prod-3-rf ansible_host=<IP> ansible_user=deploy
  ```

  Группы лучше именовать по роли, а не по локации: `prod_primary` (Швеция),
  `prod_standby` (Германия), `prod_dr` (РФ).

- [ ] Роль `common`:
  - создать пользователя `deploy`, SSH-ключи, запрет root-логина;
  - `ufw`: разрешить 22 (или нестандартный SSH), 80, 443;
  - `fail2ban`;
  - `unattended-upgrades`;
  - timezone UTC, swap (если нужно).
- [ ] Роль `docker`:
  - установить Docker Engine + Compose plugin;
  - `/etc/docker/daemon.json` с ограничением логов (`max-size`, `max-file`);
  - каталоги `/srv/undermine/{builds,java,postgres,nginx,certs}`.
- [ ] Роль `nginx`: установка и базовый конфиг (детали в OPS-04).
- [ ] Роль `postgres`: установка/подготовка каталогов (детали в OPS-03).
- [ ] Роль `app`: шаблон `.env`, каталоги, права `600`, `docker compose` файлы.
- [ ] Проверить: `ansible-playbook ... --check`, затем боевой прогон на prod-2/prod-3.

**Готово, когда:** на всех трёх серверах есть Docker, пользователь `deploy`, UFW,
fail2ban, каталоги, SSH по ключам.

---

## OPS-03 · PostgreSQL primary + replica + бэкапы

### prod-1: primary

- [ ] Поднять PostgreSQL (Docker Compose или пакет — выбрать одно и зафиксировать).
- [ ] Данные — на отдельном volume `/srv/undermine/postgres`.
- [ ] Настроить:
  - `listen_addresses` только для нужных интерфейсов;
  - `wal_level=replica`;
  - `max_wal_senders`, `max_replication_slots`;
  - `archive_mode=on`, `archive_command` для WAL-G/pgBackRest;
  - отдельные пользователи:
    - `undermine_app` — только работа с БД приложения;
    - `undermine_repl` — только репликация.
- [ ] `pg_hba.conf`:
  - приложение — только с Docker-сети/локального адреса;
  - реплика — только с IP prod-2 или через SSH-туннель.
- [ ] Включить SSL или SSH-туннель для связи prod-2 → prod-1. Для обучения проще
  SSH-туннель; для production — PostgreSQL TLS + allowlist.

### prod-2: streaming replica

- [ ] Сделать базовый backup:

  ```bash
  pg_basebackup -h <prod-1> -p 5432 -U undermine_repl \
    -D /srv/undermine/postgres -Fp -Xs -P -R
  ```

- [ ] Проверить `primary_conninfo`, `hot_standby=on`, запуск.
- [ ] Проверить отставание:

  ```sql
  SELECT client_addr, state, sent_lsn, replay_lsn,
         pg_wal_lsn_diff(sent_lsn, replay_lsn) AS lag_bytes
  FROM pg_stat_replication;
  ```

- [ ] Репликация **асинхронная**. Синхронную между Швецией и Германией не включать.
- [ ] При падении prod-1: `pg_ctl promote` / `pgBackRest promote` на prod-2.
  После promote API на prod-2 переключается на локальную БД.

### prod-3: cold DR

- [ ] Постоянная реплика на первом этапе не обязательна.
- [ ] Хранить на prod-3 актуальные `pgBackRest`/`WAL-G` бэкапы (локальная копия или
  внешний диск).
- [ ] При катастрофе (недоступны prod-1 и prod-2): восстановить БД из локального бэкапа на
  prod-3, поднять API и nginx, переключить Cloudflare.
- [ ] Позже, если нужен более быстрый DR, добавить третью асинхронную реплику
  (напрямую с prod-1 или каскадом через prod-2).

### Staging

- [ ] Отдельный PostgreSQL-контейнер/инстанс на **prod-2 (Германия)**.
- [ ] Отдельная БД `undermine_staging`.
- [ ] Лимиты CPU/RAM, чтобы staging не мешал standby.
- [ ] На время failover staging останавливается.
- [ ] Production-БД со staging не связывать.
- [ ] Если нужен полностью независимый staging — позже отдельный 4-й сервер.

### Бэкапы

- [ ] Установить `pgBackRest` или `WAL-G`.
- [ ] Ежедневный full/diff backup + WAL archive на prod-3 и/или внешний диск.
- [ ] При желании — дополнительная копия в R2/другое offsite-хранилище.
- [ ] Хранить 7–14 дней (или больше по месту).
- [ ] Раз в месяц делать тестовое восстановление в отдельную БД.
- [ ] Дополнительно `pg_dump` перед каждым production-деплоем.

**Готово, когда:** replica догоняет primary, `pg_stat_replication` показывает `streaming`,
бэкап восстанавливается на тестовом инстансе.

---

## OPS-04 · nginx + Cloudflare

- [ ] На каждом сервере nginx проксирует:
  - `/api/` → API;
  - `/admin/` → API (статика админки отдаётся API);
  - `/health` → API;
  - `/builds/` → локальный MinIO на этом же сервере (файлы реплицированы);
  - `cdn-ru.example.com` на prod-3 — отдельный DNS-only домен для РФ.
- [ ] `client_max_body_size` — не меньше лимита загрузки сборок/модпаков.
- [ ] `proxy_read_timeout` / `proxy_send_timeout` — 600s для долгих операций.
- [ ] Передавать `X-Forwarded-For`, `X-Forwarded-Proto`, `Host`.
- [ ] Настроить реальный IP Cloudflare:
  - `real_ip_header CF-Connecting-IP`;
  - `set_real_ip_from` для диапазонов Cloudflare;
  - в API настроить `ForwardedHeaders__KnownNetworks` на Docker-сеть/nginx.
- [ ] TLS:
  - Cloudflare Origin Certificate на nginx;
  - Cloudflare SSL/TLS mode: **Full (strict)**.
- [ ] Cloudflare:
  - `api.example.com` — proxied;
  - `staging.example.com` — proxied + Access;
  - `cdn.example.com` — проксируется на nginx prod-1/prod-2 → локальный MinIO;
  - `cdn-ru.example.com` — DNS-only на prod-3, без Cloudflare proxy;
  - WAF/rate limiting — по желанию;
  - кэш для `/builds/*` — на стороне nginx/Cloudflare для глобального домена.
- [ ] Проверить: `curl -I https://api.example.com`, `/health/ready`, вход в `/admin/`.

**Готово, когда:** HTTPS работает, API доступен через Cloudflare, в логах реальные IP,
`/admin` закрыт Access (для staging — обязательно).

---

## OPS-05 · Локальный MinIO + репликация

Cloudflare R2 в production не используем. На каждом сервере — свой MinIO, не один
распределённый кластер через WAN: MinIO/erasure-кластер плохо переносит задержки и кворум
между РФ, Швецией и Германией. Репликация — асинхронная, между независимыми инстансами.

- [ ] На prod-1/prod-2/prod-3 поднять MinIO (Docker Compose, отдельный сервис).
- [ ] Данные — на отдельном диске/volume `/srv/undermine/minio`.
- [ ] Bucket `undermine-builds`, включить versioning.
- [ ] Отдельные учётки:
  - `undermine-api` — read/write объектов;
  - `undermine-repl` — репликация;
  - root — только для администрирования, не в API.
- [ ] Публичный доступ:
  - bucket policy: read-only для `builds/*`;
  - наружу MinIO не смотреть, только nginx на localhost/Docker-сети.
- [ ] Репликация:
  - **Вариант A (рекомендую для старта):** bucket replication
    `prod-1 → prod-2` и `prod-1 → prod-3`;
  - **Вариант B:** MinIO site replication между тремя сайтами (двусторонняя,
    асинхронная) — удобнее при failover, но сложнее в настройке;
  - **Вариант C:** `mc mirror`/`rclone sync` по cron как временное решение.
    Для неизменяемых файлов сборок это допустимо, но лаг и отсутствие versioning.
- [ ] Пример служебных команд (алиасы/репликация через `mc`):

  ```bash
  mc alias set se http://127.0.0.1:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD"
  mc mb --ignore-existing se/undermine-builds
  mc version enable se/undermine-builds
  mc anonymous set download se/undermine-builds
  # репликация настраивается через mc replicate / консоль MinIO
  ```

- [ ] На prod-2/prod-3 проверить, что объекты появляются после загрузки на prod-1.
- [ ] Env для API на каждом сервере:

  ```env
  S3__Endpoint=http://minio:9000
  S3__UseSsl=false
  S3__Bucket=undermine-builds
  S3__AccessKey=undermine-api
  S3__SecretKey=...
  S3__CdnBaseUrl=https://cdn.example.com
  S3__FallbackCdnBaseUrl=https://cdn-ru.example.com
  S3__PublicRead=true
  ```

- [ ] nginx:
  - `cdn.example.com` → `/builds/` → локальный MinIO;
  - `cdn-ru.example.com` (prod-3, DNS-only) → локальный MinIO;
  - кэш и TLS на nginx.
- [ ] Диск: оценить размер всех сборок × 3 сервера. Репликация — не backup:
  удаление объекта реплицируется тоже. Держать versioning и минимум одну offsite-копию
  (внешний диск/R2) для важных сборок.
- [ ] Проверить:
  - загрузка файла из админки на prod-1;
  - объект появился в MinIO prod-2 и prod-3;
  - `https://cdn.example.com/builds/...` открывается;
  - `https://cdn-ru.example.com/builds/...` открывается из РФ;
  - лаунчер скачивает и проверяет SHA-256.

**Готово, когда:** файлы сборок лежат в локальном MinIO на всех трёх серверах,
репликация работает, оба CDN-домена отдают файлы без авторизации, R2 не является
обязательной зависимостью.

---

## OPS-06 · Production Docker Compose

- [ ] Создать `deploy/docker-compose.prod.yml`.
- [ ] Сервисы:
  - `api` — образ из GHCR, `env_file: .env`, `restart: unless-stopped`,
    healthcheck на `/health/ready`, volume `builds` и `java`, порт только
    `127.0.0.1:8080:8080`, если nginx на хосте; или внутренняя Docker-сеть.
  - `nginx` — 80/443, конфиги и сертификаты, зависит от `api`.
  - `postgres` — только на prod-1; staging-БД — отдельно на prod-2, в отдельном compose.
  - `minio` — на каждом сервере; данные на отдельном volume.
- [ ] На prod-2 (Германия, warm standby):
  - тот же образ API;
  - `Build__WorkerEnabled=false`;
  - `Database__MigrateOnStartup=false`;
  - локальная реплика PostgreSQL;
  - локальный MinIO replica;
  - отдельный compose-проект для staging с лимитами CPU/RAM.
- [ ] На prod-3 (РФ, cold DR):
  - nginx + образ API подготовлены, но API остановлен;
  - `Build__WorkerEnabled=false`;
  - `Database__MigrateOnStartup=false`;
  - локальный MinIO replica + nginx для `cdn-ru.example.com`;
  - БД не запущена постоянно, восстановление из локального бэкапа по runbook.
- [ ] Volumes:
  - `/srv/undermine/builds` — рабочая директория сборок;
  - `/srv/undermine/java` — кэш Java;
  - `/srv/undermine/postgres` — данные БД;
  - `/srv/undermine/minio` — объекты MinIO;
  - `/srv/undermine/nginx` — конфиги/сертификаты.
- [ ] Логи Docker ограничить (`max-size=50m`, `max-file=3`).
- [ ] Проверить `docker compose -f deploy/docker-compose.prod.yml config`.

**Готово, когда:** `docker compose up -d` на prod-1 поднимает API+nginx+БД, healthcheck
зелёный, админка открывается.

---

## OPS-07 · Небольшие изменения в коде для multi-node

Эти задачи нужны, чтобы 2 API не мешали друг другу.

- [ ] **Build worker flag**
  - добавить `Build:WorkerEnabled` (default `true`);
  - в `Program.cs` регистрировать `BuildWorkerService` только если включено;
  - на prod-1 `true`, на prod-2 `false`, на prod-3 `false` (включается только при DR).
- [ ] **Миграции отдельным шагом**
  - добавить `Database:MigrateOnStartup` (default `true`);
  - на API-репликах `false`;
  - миграции выполнять one-shot контейнером/командой перед деплоем.
- [ ] **S3 bucket management**
  - добавить `S3:ManageBucket` (default `true`);
  - для локального MinIO можно оставить `true`, для внешнего S3/R2 — `false`, если bucket
    создан вручную.
- [ ] **Sentry**
  - добавить `Sentry.AspNetCore`;
  - DSN из env;
  - включать только в Production.
- [ ] **Health/readiness**
  - убедиться, что `/health/ready` проверяет БД;
  - `/health/live` не зависит от внешних сервисов.

**Готово, когда:** на prod-2/prod-3 API не запускает worker и не делает миграции;
миграции выполняются ровно один раз на prod-1.

---

## OPS-08 · CI (GitHub Actions)

- [ ] `.github/workflows/ci.yml` на `pull_request` и `push` в `main`:
  - `dotnet restore/build/test`;
  - `node --check UnderMineApi/wwwroot/admin/app.js`;
  - Docker build без push;
  - опционально Trivy.
- [ ] Кэш NuGet.
- [ ] На `main` — сборка и push в GHCR:
  - тег `sha-<short>` — основной;
  - `latest` — только для удобства;
  - `docker/build-push-action` + `docker/login-action`.
- [ ] Не пушить образы из PR.
- [ ] Проверить, что PR не проходит при падении тестов.

**Готово, когда:** PR зелёный, в GHCR появился образ с SHA-тегом.

---

## OPS-09 · CD staging

- [ ] `.github/workflows/deploy-staging.yml`:
  - запуск после успешного CI в `main` или вручную;
  - GitHub Environment `staging`;
  - деплой на prod-2 (Германия) через Ansible/SSH, в отдельный compose-проект.
- [ ] Шаги:
  1. `docker pull ghcr.io/...:sha-...`;
  2. обновить `IMAGE_TAG` в `.env`;
  3. `docker compose -f ... up -d`;
  4. healthcheck;
  5. smoke-тест.
- [ ] Staging БД — отдельная, на prod-2 (Германия).
- [ ] Cloudflare Access на `staging.example.com`.
- [ ] На время failover staging останавливается, чтобы не мешать standby.

**Готово, когда:** merge в main автоматически обновляет staging, `/health/ready` зелёный.

---

## OPS-10 · CD production

- [ ] `.github/workflows/deploy-production.yml`:
  - только вручную или по тегу;
  - GitHub Environment `production` с required reviewer;
  - секреты production.
- [ ] Порядок:
  1. `pgBackRest backup` (или `pg_dump`) перед деплоем;
  2. деплой на prod-1;
  3. миграции one-shot;
  4. `docker compose up -d --no-deps api`;
  5. healthcheck `/health/ready`;
  6. деплой на prod-2 (Германия, standby);
  7. на prod-3 (РФ) обновить образ/конфиг, но API оставить остановленным;
  8. smoke-тест.
- [ ] Откат: вернуть предыдущий `IMAGE_TAG`; БД — из бэкапа, если миграция несовместима.
- [ ] Уведомление в Telegram/Discord об успехе/ошибке.
- [ ] Хранить историю тегов и релизов.

**Готово, когда:** тестовое изменение проходит staging → production, откат проверен.

---

## OPS-11 · Мониторинг и ошибки

- [ ] **Uptime Kuma** на prod-3 (РФ), чтобы мониторинг пережил падение prod-1/prod-2:
  - мониторинг `https://api.example.com/health/ready`;
  - мониторинг `/health/live`;
  - мониторинг админки;
  - TCP-мониторинг PostgreSQL (через локальный/туннельный адрес);
  - уведомления в Telegram.
- [ ] **Sentry** (SaaS) или **GlitchTip** на prod-3 (РФ):
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
  3. включить `Build__WorkerEnabled=true` на prod-2;
  4. переключить DNS/Cloudflare на prod-2;
  5. проверить `/health/ready`, вход в админку, запуск клиента.
- [ ] Если **prod-2 тоже недоступен** (последний резерв, prod-3 РФ):
  1. восстановить PostgreSQL на prod-3 из свежайшего локального/offsite
     `pgBackRest`/WAL-G бэкапа;
  2. поднять API и nginx на prod-3;
  3. включить `Build__WorkerEnabled=true`;
  4. переключить Cloudflare на prod-3;
  5. проверить `/health/ready`, админку, скачивание файлов сборок с локального MinIO.
- [ ] Если prod-1 вернулся:
  - не включать его сразу в трафик;
  - пересобрать как реплику с текущего primary (prod-2 или prod-3);
  - проверить lag;
  - вернуть трафик, когда всё стабильно.
- [ ] Провести учения на staging: имитировать падение prod-1, затем prod-2, пройти оба
  сценария (failover и DR).
- [ ] Записать RTO/RPO, которые получились.

**Готово, когда:** есть письменный runbook и хотя бы одно успешное учение.

---

## OPS-13 · Безопасность (чек-лист)

- [ ] SSH только по ключам, root login запрещён.
- [ ] fail2ban + UFW.
- [ ] 22/80/443 — только необходимое; 8080/5432 — только loopback/private.
- [ ] Cloudflare Access на staging и админку production (по желанию).
- [ ] Секреты только в env/GitHub Environments, не в git.
- [ ] JWT key ≥ 32 символа, ротация.
- [ ] Отдельные DB-пользователи для приложения и репликации.
- [ ] MinIO service accounts с минимальными правами; root не используется API.
- [ ] Docker log rotation.
- [ ] Автоматические security updates.
- [ ] Бэкапы БД и MinIO/offsite, проверка восстановления.
- [ ] Sentry не получает секреты/токены (scrub).

---

## OPS-14 · Критерии готовности

- [ ] CI на PR: build + tests + JS check.
- [ ] Образ в GHCR с immutable SHA-тегом.
- [ ] Staging обновляется автоматически.
- [ ] Production деплоится с ручным подтверждением.
- [ ] `api.example.com` работает через Cloudflare + nginx + TLS.
- [ ] Админка доступна, файлы сборок отдаются с локального MinIO/nginx.
- [ ] PostgreSQL primary (Швеция) + async replica (Германия), бэкапы локально/offsite.
- [ ] Cold DR на prod-3 (РФ) восстановлен из локального бэкапа хотя бы один раз.
- [ ] MinIO replication проверена: объект с prod-1 появился на prod-2 и prod-3.
- [ ] Миграции выполняются один раз.
- [ ] Build worker только на prod-1; на prod-2 включается при failover.
- [ ] Uptime Kuma и Sentry присылают алерты.
- [ ] Есть runbook failover (Швеция → Германия) и DR (Германия → РФ).
- [ ] Тестовое восстановление БД успешно.

---

## OPS-15 · Что можно делать позже (не сейчас)

- [ ] Cloudflare Load Balancer вместо ручного переключения DNS.
- [ ] Active-active API на 3 серверах.
- [ ] Patroni/etcd для автоматического failover PostgreSQL.
- [ ] Grafana + Prometheus + Loki для метрик и логов.
- [ ] Kubernetes/k3s + Argo CD, если появится реальная потребность.
- [ ] Перенос staging на отдельный домен/кластер.

---

## OPS-16 · Доставка файлов в РФ без зависимости от Cloudflare

После перехода на локальный MinIO отдача файлов вообще не обязана идти через Cloudflare.
Файлы реплицируются на все три сервера, а nginx на каждом отдаёт их локально.

- [ ] **Не отдавать файлы сборок через Cloudflare.**  
  Cloudflare остаётся для API/админки, но не для тяжёлых файлов.
- [ ] `cdn.example.com` — **DNS-only** запись на prod-1/prod-2, nginx → локальный MinIO.
  Без оранжевой тучки Cloudflare.
- [ ] `cdn-ru.example.com` — **DNS-only** запись на prod-3 (РФ), nginx → локальный MinIO.
- [ ] Оба домена — с TLS на nginx (Let's Encrypt/Origin CA), кэш nginx для
  `/builds/*`, `Range` для больших файлов.
- [ ] Репликация MinIO обеспечивает наличие файлов на всех трёх узлах.  
  Если репликация отстаёт, лаунчер может получить 404 на только что созданном файле —
  предусмотреть retry/повторную попытку и fallback-URL.
- [ ] В манифест добавить fallback-URL:
  ```json
  {
    "path": "mods/example.jar",
    "url": "https://cdn.example.com/builds/<id>/mods/example.jar",
    "fallbackUrl": "https://cdn-ru.example.com/builds/<id>/mods/example.jar"
  }
  ```
  - `BuildManifestService` заполняет оба URL по настройкам
    `S3:CdnBaseUrl` и `S3:FallbackCdnBaseUrl`;
  - лаунчер сначала пробует `url`, при сетевой ошибке/таймауте — `fallbackUrl`;
  - SHA-256 проверяется независимо от источника.
- [ ] Если DNS тоже на Cloudflare и он недоступен из РФ — держать `cdn-ru.example.com`
  в другом DNS-провайдере или использовать отдельную зону.
- [ ] Проверить из РФ (МТС, Билайн, Мегафон, Tele2):
  - скачивание с `cdn.example.com`;
  - скачивание с `cdn-ru.example.com`;
  - скорость и стабильность.
- [ ] Проверить API: если Cloudflare блокируется, login/манифест тоже недоступны.
  При необходимости добавить `api-ru.example.com` (DNS-only на prod-3 или РФ-прокси)
  и fallback API URL в лаунчере.
- [ ] Мониторить оба CDN-домена и репликацию MinIO отдельно.

**Готово, когда:** клиент в РФ скачивает файлы с локального MinIO/nginx без Cloudflare,
лаунчер умеет переключаться на fallback-URL и проверяет SHA-256.

---

## Рекомендуемый порядок выполнения

1. OPS-01 (репозиторий и окружения).
2. OPS-02 (Ansible, базовый сервер).
3. OPS-03 (PostgreSQL primary + бэкапы).
4. OPS-04 (nginx + Cloudflare).
5. OPS-05 (локальный MinIO + репликация).
6. OPS-06 (production compose).
7. OPS-07 (worker/migrations flags).
8. OPS-08 (CI).
9. OPS-09 (staging CD).
10. OPS-10 (production CD).
11. OPS-11 (мониторинг).
12. OPS-03 replica в Германии + OPS-12 (failover Швеция → Германия).
13. OPS-03 cold DR в РФ + учение DR (Германия → РФ).
14. OPS-13 (security) и OPS-14 (приёмка).

Начинать с PostgreSQL-реплики не нужно: сначала стабильный prod-1 и бэкапы, потом реплика и
failover, и только затем cold DR в РФ. Так меньше шансов запутаться в БД и CI/CD одновременно.
