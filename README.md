# Read Speed

Read Speed — WPF-приложение для измерения скорости последовательного чтения файлов, сетевых ресурсов и HTTP/HTTPS-ссылок.

[![Build](https://github.com/n1vers/ReadSpeed/actions/workflows/build.yml/badge.svg)](https://github.com/n1vers/ReadSpeed/actions/workflows/build.yml)
[![Latest Release](https://img.shields.io/github/v/release/n1vers/ReadSpeed?display_name=tag)](https://github.com/n1vers/ReadSpeed/releases/latest)

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

Готовый self-contained EXE доступен на странице [последнего релиза](https://github.com/n1vers/ReadSpeed/releases/latest).
Скачай `ReadSpeed.exe` и запусти его напрямую.

## Запуск из интерфейса

Укажи источники в поле по одному в строке. Поле **Потоки** задаёт максимальное число одновременно выполняемых тестов.

Для каждого источника отображается собственная карточка: состояние (в очереди / идёт / пауза / завершён / ошибка), прогресс, текущая, средняя, минимальная и максимальная скорость, объём, время и свой график. Карточки остаются на экране после завершения теста. Под ними показывается суммарная скорость всех активных потоков.

Несколько источников можно перетащить в окно разом или выбрать в диалоге файлов (Ctrl/Shift).

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

