# Read Speed

Read Speed — WPF-приложение для измерения скорости последовательного чтения файлов, сетевых ресурсов и HTTP/HTTPS-ссылок.

## Возможности

- локальные файлы и диски;
- сетевые пути и NAS;
- HTTP/HTTPS-источники;
- отдельный график для каждого параллельного теста;
- ограничение количества одновременных тестов;
- режим Loop;
- пауза, отмена и история результатов;
- выбор единиц скорости: MB/s, GB/s, Mbps и Gbps.

## Требования

- Windows x64;
- .NET 10 SDK для сборки из исходников.

Готовый self-contained EXE собирается GitHub Actions и доступен в разделе **Actions → workflow run → Artifacts**.

## Запуск из интерфейса

Укажи источники в поле по одному в строке. Поле **Потоки** задаёт максимальное число одновременно выполняемых тестов.

Для каждого источника отображается собственная карточка с прогрессом и графиком.

## Запуск из CMD

Один источник:

```powershell
ReadSpeed.exe "D:\Tests\file.bin"
```

Несколько источников одновременно:

```powershell
ReadSpeed.exe --parallel 2 "D:\Tests\disk.bin" "https://example.com/test.iso"
```

Короткая форма параметра:

```powershell
ReadSpeed.exe -p 4 "source-1" "source-2" "source-3" "source-4"
```

Loop для каждого источника:

```powershell
ReadSpeed.exe --parallel 2 --loop "D:\Tests\disk.bin" "https://example.com/test.iso"
```

Поддерживаемые параметры:

- `--parallel N` или `-p N` — максимум одновременно активных тестов;
- `--loop` или `-l` — повторять каждый источник после завершения;
- каждый аргумент после параметров — отдельный файл, сетевой путь или HTTP/HTTPS-ссылка.

## Сборка локально

```powershell
dotnet restore
dotnet build -c Release
```

Публикация одного self-contained EXE:

```powershell
dotnet publish ReadPerfomance.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## GitHub Actions

Workflow `.github/workflows/build.yml` запускается при push, Pull Request и вручную через **Run workflow**. Он выполняет restore, build, publish и загружает ZIP с готовым Windows-приложением.

## Лицензия

Лицензия пока не выбрана.
