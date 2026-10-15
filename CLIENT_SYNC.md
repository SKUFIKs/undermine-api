# Client Sync Downloader — Архитектура

## Обзор

Легковесный установщик/синхронизатор клиента Minecraft с поддержкой кастомного CDN/S3-хранилища (MinIO / Nginx).

## Концепция

```
Сервер (S3/MinIO/Nginx)              Клиент (Launcher)
┌─────────────────────┐               ┌─────────────────────────┐
│ builds/             │               │ 1. Fetch manifest.json  │
│   └─ {serverId}/    │◄── HTTP GET ──│ 2. Compare SHA-256      │
│       ├─ manifest.json              │ 3. Download missing     │
│       ├─ files/...   │◄── HTTP GET ──│ 4. Prune old files      │
│       └─ runtime/... │               │ 5. Launch with --gameDir│
└─────────────────────┘               └─────────────────────────┘
```

## Manifest JSON Schema

```json
{
  "buildId": "uuid",
  "displayName": "UnderMine 1.20.1",
  "gameVersion": "1.20.1",
  "loaderType": "forge",
  "loaderVersion": "47.2.0",
  "javaVersion": 17,
  "mainClass": "net.minecraftforge.bootstrap.ForgeBootstrap",
  "jvmArgs": ["-Xmx4G", "-XX:+UseG1GC"],
  "gameArgs": ["--username", "${username}", "--uuid", "${uuid}"],
  "files": [
    {
      "path": "mods/jei-1.20.1.jar",
      "sha256": "abc123...",
      "size": 1234567,
      "policy": "FORCE_SYNC",
      "url": "https://cdn.example.com/builds/{serverId}/mods/jei-1.20.1.jar"
    }
  ]
}
```

## Типы политик синхронизации

| Политика | Описание | Примеры |
|----------|----------|---------|
| `FORCE_SYNC` | Всегда перезаписывать | libraries, client.jar, mods, runtime |
| `DOWNLOAD_ONCE` | Скачать если нет, не перезаписывать | options.txt, конфиги модов |
| `PRESERVE` | Никогда не удалять и не перезаписывать | saves, screenshots, resourcepacks |

## Структура директорий

### Сервер (S3/MinIO/Nginx)

```
builds/
└── {serverId}/
    ├── manifest.json
    ├── versions/
    │   └── {gameVersion}/
    │       ├── {gameVersion}.jar
    │       └── {gameVersion}.json
    ├── libraries/
    │   └── ...
    ├── mods/
    │   └── ...
    ├── config/
    │   └── ...
    ├── runtime/
    │   └── ...
    └── assets/
        └── ...
```

### Клиент (Launcher)

```
%AppData%/UnderMine/
└── instances/
    └── {ServerName}/
        ├── manifest.json (cached)
        ├── versions/
        ├── libraries/
        ├── mods/
        ├── config/
        ├── runtime/
        └── assets/
```

## Генератор манифеста

CLI-утилита для создания `manifest.json` из подготовленной сборки:

```bash
dotnet run --project ManifestGenerator -- \
  ./builds/underMine \
  ./builds/underMine/manifest.json \
  --name "UnderMine 1.20.1" \
  --game-version 1.20.1 \
  --loader forge \
  --loader-version 47.2.0 \
  --java 17 \
  --cdn-base https://cdn.example.com/builds/underMine
```

## API Endpoints

| Метод | Путь | Описание |
|-------|------|----------|
| GET | `/api/v1.0/servers/{id}/manifest` | Получить манифест сборки |
| POST | `/api/v1.0/admin/servers` | Создать сервер с манифестом |
| PUT | `/api/v1.0/admin/servers/{id}` | Обновить сервер с манифестом |

## Поддерживаемые загрузчики

| Тип | Main Class | Особенности |
|-----|------------|-------------|
| Vanilla | `net.minecraft.client.main.Main` | Без дополнительных аргументов |
| Fabric | `net.fabricmc.loader.impl.launch.knot.KnotClient` | Стандартные аргументы |
| Forge | `net.minecraftforge.bootstrap.ForgeBootstrap` | `--add-modules ALL-MODULE-PATH` |
| NeoForge | `net.neoforged.bootstrap.NeoForgeBootstrap` | `--add-modules ALL-MODULE-PATH` |

## Клиентский модуль синхронизации

### Фазы работы

1. **Fetch Manifest** — загрузка `manifest.json` с сервера
2. **Compare** — сравнение локальных файлов с манифестом (размер + SHA-256)
3. **Download** — параллельная загрузка отсутствующих/изменённых файлов
4. **Prune** — удаление файлов, которых больше нет в манифесте
5. **Launch** — генерация команды запуска и старт процесса

### Оптимизации

- **Fast-Check**: сначала сравнение размера, затем хеша (если размер совпал)
- **Параллельная загрузка**: пул из 4 воркеров
- **Retry с backoff**: 3 попытки с экспоненциальной задержкой
- **Валидация хешей**: проверка SHA-256 каждого скачанного файла
- **Pruning**: удаление пустых директорий после очистки
