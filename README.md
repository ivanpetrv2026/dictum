# Dictum

Локальная диктовка для Windows: удерживаешь **Ctrl+Win**, говоришь, отпускаешь — текст печатается в активное окно. Распознавание идёт на своей машине через whisper.cpp, без облака.

Проект задуман как «чистая» альтернатива типичным диктовщикам: никаких глобальных хуков клавиатуры, никакого чтения буфера обмена, никакой телеметрии и автозапуска. Именно эти паттерны чаще всего приводят к ложным срабатываниям антивирусов и блокировке неподписанных сборок.

## Как это устроено

| Компонент | Решение | Почему так |
|---|---|---|
| Хоткей | Опрос `GetAsyncKeyState` раз в 30 мс | `WH_KEYBOARD_LL` — профиль кейлоггера, главный источник ложных детектов |
| Запись | NAudio, WinMM, 16 кГц / 16 бит / моно | Ровно тот формат, который ждёт Whisper, без лишнего |
| Распознавание | Whisper.net (whisper.cpp) | Полностью офлайн, модель в памяти |
| Вставка текста | `SendInput` + `KEYEVENTF_UNICODE` | Не трогает буфер обмена, работает в RDP с отключённым клипбордом |
| Настройки | JSON в `%APPDATA%\Dictum` | Никаких служб и реестра |
| Установка | Inno Setup, `PrivilegesRequired=lowest` | Ставится в профиль пользователя, без UAC |
| Автозапуск | **Отсутствует** | Осознанно: persistence-паттерны повышают риск-скор |

## Структура

```
src/Dictum/
  App.xaml(.cs)              точка входа, single-instance mutex
  MainWindow.xaml(.cs)       окно «О программе»
  Services/AppController.cs  связывает хоткей, запись, распознавание и вставку
  Input/HotkeyPoller.cs      опрос Ctrl+Win без хука
  Input/TextInjector.cs      печать текста через SendInput
  Audio/AudioRecorder.cs     захват микрофона 16 кГц моно
  Speech/WhisperTranscriber.cs  обёртка над Whisper.net
  Settings/                  модель настроек и JSON-хранилище
  Tray/TrayIcon.cs           иконка в трее и меню
  Views/SettingsWindow.xaml  настройки
installer/dictum.iss         установщик Inno Setup
.github/workflows/release.yml  сборка, подпись, хеши, аттестация, VirusTotal
docs/SIGNING.md              как настроить подпись
docs/MICROSOFT_SUBMISSION.md шаблон заявки в Microsoft
```

## Требования

- Windows 10/11 x64
- .NET SDK 8.0
- Модель Whisper в формате ggml (`ggml-*.bin`), например `ggml-large-v3-turbo.bin`

## Сборка и запуск

```powershell
dotnet build src/Dictum/Dictum.csproj
dotnet run --project src/Dictum/Dictum.csproj
```

При первом запуске приложение попросит путь к модели. Дальше — удерживай Ctrl+Win и говори.

Публикация одним файлом:

```powershell
dotnet publish src/Dictum/Dictum.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Ускорение на GPU

По умолчанию используется CPU-рантайм. Для CUDA замени пакет:

```xml
<PackageReference Include="Whisper.net.Runtime" Version="1.9.1" />
<!-- на -->
<PackageReference Include="Whisper.net.Runtime.Cuda" Version="1.9.1" />
```

Учти: CUDA-рантайм тянет за собой библиотеки cuBLAS (~700 МБ), поэтому в релизной сборке их лучше скачивать отдельно, а не класть в установщик.

## Релиз

Тег `v*` запускает workflow: сборка → подпись (если заданы секреты) → установщик → SHA-256 → аттестация происхождения → проверка в VirusTotal → публикация релиза.

Секреты репозитория:

| Секрет | Назначение |
|---|---|
| `AZURE_TENANT_ID` | Подпись Azure Trusted Signing |
| `AZURE_CLIENT_ID` | |
| `AZURE_CLIENT_SECRET` | |
| `AZURE_CODE_SIGNING_ENDPOINT` | |
| `AZURE_CODE_SIGNING_ACCOUNT` | |
| `AZURE_CERT_PROFILE` | |
| `VIRUSTOTAL_API_KEY` | Необязательно: предполётная проверка |

Без секретов workflow всё равно соберёт и опубликует артефакты — просто без подписи и без проверки VT.

## Ограничения текущего скелета

- Хоткей фиксированный (Ctrl+Win) — нет UI для переназначения.
- Нет истории диктовок и словаря терминов.
- Нет автоскачивания модели: путь указывается вручную.
- Нет индикатора записи (только звук/трей).
- Вставка через `SendInput` может не сработать в окнах с повышенными правами (UIPI) — это ограничение Windows, а не приложения.

## Лицензия

MIT.
