# Contributing

Спасибо за интерес к Dictum. Проект небольшой, поэтому правила короткие.

## Сообщить о проблеме или предложить функцию

- Баг — через [шаблон bug report](.github/ISSUE_TEMPLATE/bug_report.yml): версия релиза, модель Whisper, шаги воспроизведения, что ожидали и что произошло.
- Идея — через [шаблон feature request](.github/ISSUE_TEMPLATE/feature_request.yml): какую задачу решаете, как обходились раньше.
- Уязвимость — **не** через публичные issues: см. [SECURITY.md](SECURITY.md).
- Общие вопросы — можно без шаблона или в [Discussions](../../discussions).

## Сборка из исходников

Нужен .NET SDK 8.0, Windows 10/11 x64:

```powershell
dotnet build src/Dictum/Dictum.csproj
dotnet publish src/Dictum/Dictum.csproj -c Release -r win-x64 --self-contained true -o publish
```

Быстрая проверка без микрофона:

```powershell
publish/Dictum.exe --vad-test file.wav
publish/Dictum.exe --polish-test "привет, запятая"
publish/Dictum.exe --benchmark
```

## Требования к изменениям

- Приватность по дизайну: не добавлять сетевые вызовы, кроме уже описанных в [SECURITY.md](SECURITY.md); любая новая сеть — сначала обсуждение в issue.
- Никаких хуков клавиатуры (`WH_KEYBOARD_LL`) и доступа к буферу обмена — это часть модели безопасности проекта.
- Стиль кода — как вокруг: стандартные конвенции C#, nullable включён.
- Коммиты — [Conventional Commits](https://www.conventionalcommits.org/ru/): `feat:`, `fix:`, `docs:`, `chore:` на английском.
- Перед PR убедитесь, что `dotnet build` проходит без новых предупреждений.

## Релизы

Релизы выпускает мейнтейнер тегом `v*` — CI собирает full/lite/portable, считает `SHA256SUMS.txt` и прикрепляет build-аттестации.
