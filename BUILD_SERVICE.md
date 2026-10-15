# Build Service — Серверная система подготовки сборок Minecraft

## Обзор

Полная переработка серверной части: автоматическая подготовка сборок Minecraft с версионированием, генерацией манифестов целостности и раздачей файлов клиентам через S3/MinIO.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        Build Service Architecture                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────┐    ┌───────────┐ │
│  │   REST API   │───▶│ Build Worker │───▶│  S3 Uploader │───▶│  MinIO/   │ │
│  │  Controller  │    │  (Background)│    │  (Dedup)     │    │  S3       │ │
│  └──────────────┘    └──────────────┘    └──────────────┘    └───────────┘ │
│         │                  │                    │                          │
│         ▼                  ▼                    ▼                          │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────┐                  │
│  │  EF Core +   │    │  Engine      │    │  Manifest    │                  │
│  │  PostgreSQL  │    │  Downloader  │    │  Generator   │                  │
│  └──────────────┘    └──────────────┘    └──────────────┘                  │
│                           │                                                 │
│                           ▼                                                 │
│                    ┌──────────────┐                                         │
│                    │  Forge/Fabric│                                         │
│                    │  Installer   │                                         │
│                    │  (Headless)  │                                         │
│                    └──────────────┘                                         │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Концепция

```
Сервер (S3/MinIO/Nginx)              Клиент (Launcher)
┌─────────────────────┐               ┌─────────────────────────┐
│ builds/             │               │ 1. Fetch manifest.json  │
│   └─ {buildId}/     │◄── HTTP GET ──│ 2. Compare SHA-256      │
│       ├─ manifest.json              │ 3. Download missing     │
│       ├─ files/...   │◄── HTTP GET ──│ 4. Prune old files      │
│       └─ runtime/... │               │ 5. Launch with --gameDir│
└─────────────────────┘               └─────────────────────────┘
```

## Новые файлы

### Domain Entities
- `UnderMineApi/Domain/Entities/BuildInstance.cs` — сущности BuildInstance и BuildFile (+ колонки живого прогресса)
- `UnderMineApi/Domain/Entities/BuildLog.cs` — строка журнала сборки (build_logs)

### DTOs
- `UnderMineApi/DTOs/BuildDto.cs` — модели запросов/ответов, ClientManifestResponse, BuildLogResponse

### Services
- `UnderMineApi/Services/EngineDownloadService.cs` — загрузка ванильных файлов + headless-установка Forge/NeoForge/Fabric
- `UnderMineApi/Services/S3StorageService.cs` — загрузка в MinIO/S3 с дедупликацией по SHA-256
- `UnderMineApi/Services/BuildManifestService.cs` — сканирование директории, хеширование, генерация манифеста
- `UnderMineApi/Services/BuildWorkerService.cs` — фоновый воркер обработки очереди сборок
- `UnderMineApi/Services/BuildProgressReporter.cs` — сбор и персист прогресса/логов во время обработки сборки

### Controllers
- `UnderMineApi/Controllers/BuildsController.cs` — REST API (create, list, status, manifest)

### Infrastructure
- `docker-compose.yml` — полный стек: PostgreSQL + MinIO + Nginx + API
- `nginx.conf` — CDN-прокси с CORS
- `BUILD_SERVICE.md` — эта документация

### Tooling
- `ManifestGenerator/Program.cs` — CLI-генератор манифеста из подготовленной сборки

## Обновлённые файлы

| Файл | Изменения |
|------|-----------|
| `UnderMineApi/Data/AppDbContext.cs` | DbSets для BuildInstances/BuildFiles/BuildLogs + конфигурация модели |
| `UnderMineApi/Program.cs` | Регистрация build-сервисов, HttpClient, hosted worker |
| `UnderMineApi/UnderMineApi.csproj` | Пакет Minio v6.0.3 |

## API Endpoints

| Метод | Путь | Описание |
|-------|------|----------|
| POST | `/api/v1.0/builds` | Создать сборку (admin) |
| GET | `/api/v1.0/builds` | Список сборок с живым прогрессом (admin) |
| GET | `/api/v1.0/builds/{id}` | Статус сборки с живым прогрессом (admin) |
| GET | `/api/v1.0/builds/{id}/logs?afterId=0&limit=200` | Журнал обработки сборки (admin) |
| GET | `/api/v1.0/builds/{buildId}/manifest` | Манифест сборки (public) |
| GET | `/api/v1.0/servers/{id}/manifest` | Манифест, который лаунчер скачивает для сервера (public) |

## Статусы сборок

```
pending → downloading → hashing → uploading → completed
                                         ↘ failed
```

## Живой прогресс и логи сборки

Пока воркер обрабатывает сборку, он публикует в БД (с троттлингом ~1 раз в секунду):

- `progress_percent` — процент выполнения текущей фазы;
- `bytes_downloaded` / `total_bytes` — сколько уже скачано и сколько всего (суммарно по `Content-Length`);
- `download_speed` — мгновенная скорость загрузки, байт/с (усредняется по скользящему окну);
- `current_step` — название фазы («Скачивание клиента 1.20.1», «Загрузка ассетов (3666 файлов)», «Хеширование…», «Загрузка в S3…»);
- `current_file` — файл, обрабатываемый прямо сейчас;
- `downloaded_files` / `total_files` — прогресс по файлам (используется на фазах хеширования и выгрузки).

Отдельные строки пишутся в таблицу `build_logs` (level: `info`/`warn`/`error`): старт сборки, каждый
артефакт/библиотека, сводки по ассетам каждые 50 файлов, ошибки HTTP-загрузок и ошибки установщиков.
Агрегированные поля отдаются в `GET /builds`, журнал — в `GET /builds/{id}/logs`:

```bash
# первые порции (новейшие 200 строк, в хронологическом порядке)
curl -H "Authorization: Bearer <admin_token>" \
  http://localhost:5000/api/v1.0/builds/5/logs?afterId=0

# инкрементально: только строки новее последней полученной
curl -H "Authorization: Bearer <admin_token>" \
  http://localhost:5000/api/v1.0/builds/5/logs?afterId=142
```

В админ-панели (вкладка Builds) под статусом активной сборки рисуется прогресс-бар со скоростью и
текущим файлом, а кнопка **Logs** открывает окно журнала, которое обновляется раз в 2 секунды.

## Manifest JSON Schema

```json
{
  "buildId": "a1b2c3d4...",
  "displayName": "UnderMine 1.20.1",
  "gameVersion": "1.20.1",
  "loaderType": "forge",
  "loaderVersion": "47.2.0",
  "javaVersion": 17,
  "mainClass": "net.minecraftforge.bootstrap.ForgeBootstrap",
  "jvmArgs": ["-XX:+UseG1GC", ...],
  "gameArgs": ["--username", "${username}", ...],
  "baseUrl": "http://localhost:8080",
  "files": [
    {
      "path": "mods/jei-1.20.1.jar",
      "sha256": "abc123...",
      "size": 1234567,
      "policy": "FORCE_SYNC",
      "url": "http://localhost:8080/builds/.../mods/jei-1.20.1.jar"
    }
  ]
}
```

## Политики синхронизации

| Политика | Описание |
|----------|----------|
| `FORCE_SYNC` | Всегда перезаписывать |
| `DOWNLOAD_ONCE` | Скачать если нет, не перезаписывать |
| `PRESERVE` | Никогда не удалять |

## Поддерживаемые загрузчики

| Тип | Main Class |
|-----|------------|
| Vanilla | `net.minecraft.client.main.Main` |
| Fabric | `net.fabricmc.loader.impl.launch.knot.KnotClient` |
| Forge | `net.minecraftforge.bootstrap.ForgeBootstrap` |
| NeoForge | `net.neoforged.bootstrap.NeoForgeBootstrap` |

## Локальное развертывание

```bash
docker-compose up -d
```

Сервисы:
- PostgreSQL: `localhost:5432`
- MinIO: `localhost:9000` (console: `localhost:9001`)
- Nginx CDN: `localhost:8080`
- API: `localhost:5000`

## Использование

### Создание сборки

```bash
curl -X POST http://localhost:5000/api/v1.0/builds \
  -H "Authorization: Bearer <admin_token>" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Tech",
    "mc_version": "1.21.1",
    "loader": "neoforge",
    "loader_version": "21.1.55"
  }'
```

### Получение манифеста (клиент)

```bash
curl http://localhost:5000/api/v1.0/builds/a1b2c3d4/manifest
```

### Генерация манифеста из готовой сборки

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

### Привязка сборки к серверу

Готовая (`completed`) сборка становится клиентским манифестом сервера двумя способами:

- **Авто (по умолчанию).** Поле `buildId` не передаётся — сервер автоматически раздаёт манифест
  новейшей `completed`-сборки, у которой совпадают `mcVersion` + `loaderType` (+ `loaderVersion`,
  если он задан у сервера). Сборка при этом решается на лету при каждом
  `GET /servers/{id}/manifest`, поэтому привязка «оживает» сразу после завершения подходящей сборки.
- **Явная привязка.** `buildId: "<buildId>"` — сервер навсегда раздаёт манифест именно этой сборки.
  `buildId: ""` — убрать манифест совсем.

```bash
curl -X POST http://localhost:5000/api/v1.0/admin/servers \
  -H "Authorization: Bearer <admin_token>" \
  -H "Content-Type: application/json" \
  -d '{
    "serverName": "Main",
    "appVersion": "1.20.1",
    "loaderType": "forge",
    "typeVersion": "47.4.27",
    "buildId": "a1b2c3d4e5f6..."
  }'
```

В админ-панели это поле — выпадающий список «Client Build» в форме сервера; в таблице серверов
показывается, какая сборка привязана (`auto` / `none` / первые 8 символов `buildId`).

## Сборка и миграции

```bash
dotnet ef migrations add AddBuildService --project UnderMineApi --startup-project UnderMineApi
dotnet build UnderMineApi.sln
```
