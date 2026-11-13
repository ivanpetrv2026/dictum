# Подпись кода

> **Статус проекта (13.11.2026):** релизы, включая `v0.12.4`, выходят неподписанными. Вариант 1 ниже приведён **справочно**: сервис переименован в Azure Artifact Signing, а сертификат Public Trust выдаётся организациям из ограниченного списка стран и частным разработчикам только из США и Канады — для этого проекта он недоступен. Фактический план — бесплатная подпись для open source через [SignPath Foundation](https://signpath.org/); заявка отложена до появления у проекта проверяемой репутации. Актуальная формулировка — в [README](../README.md) и [SECURITY.md](../SECURITY.md).

Без подписи Windows показывает «Unknown publisher», SmartScreen блокирует запуск, а ML-модели антивирусов относятся к файлу настороженно. Подпись не делает приложение безопасным, но снимает большую часть ложных срабатываний и ускоряет разбор заявок в Microsoft.

## Вариант 1: Azure Artifact Signing, бывший Azure Trusted Signing (справочно)

Облачная подпись без USB-токена, около $10 в месяц. Подходит для CI: ключ не покидает Azure, GitHub Actions получает только токен.

### Шаги

1. Создай подписку Azure и ресурс **Trusted Signing Account**.
2. Пройди **Identity Validation** — Microsoft проверит организацию или частное лицо. Для ИП/физлица нужны документы и, как правило, история деятельности; доступность зависит от страны.
3. Создай **Certificate Profile** типа `PublicTrust` в том же аккаунте.
4. Зарегистрируй приложение в Entra ID и выдай ему роль `Trusted Signing Certificate Profile Signer` на уровне аккаунта.
5. Создай секрет приложения — он понадобится GitHub Actions.

### Секреты репозитория

| Секрет | Откуда взять |
|---|---|
| `AZURE_TENANT_ID` | Entra ID → обзор |
| `AZURE_CLIENT_ID` | Зарегистрированное приложение |
| `AZURE_CLIENT_SECRET` | Секрет приложения |
| `AZURE_CODE_SIGNING_ENDPOINT` | Например `https://weu.codesigning.azure.net` |
| `AZURE_CODE_SIGNING_ACCOUNT` | Имя Trusted Signing Account |
| `AZURE_CERT_PROFILE` | Имя Certificate Profile |

После добавления секретов workflow `release.yml` подпишет и `Dictum.exe`, и установщик.

## Вариант 2: обычный сертификат (OV/EV)

С 2023 года приватный ключ обязан храниться в HSM или аппаратном токене, поэтому в CI это неудобно: понадобится либо облачный HSM, либо self-hosted runner с токеном.

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
  /sha1 <THUMBPRINT> publish\Dictum.exe
```

EV-сертификат даёт мгновенную репутацию в SmartScreen, OV — только после накопления загрузок.

## Проверка подписи

```powershell
Get-AuthenticodeSignature .\Dictum_0.1.0_x64_setup.exe | Format-List Status, SignerCertificate
```

Ожидаемый статус — `Valid`.
