# Заявка в Microsoft на перепроверку

Подавать нужно, если Defender или SmartScreen блокируют подписанный релиз. С подписью шанс на переклассификацию кратно выше: Microsoft видит издателя и может связать файл с репутацией.

## Куда идти

<https://www.microsoft.com/en-us/wdsi/filesubmission>

1. Выбери **Home customer** (для частного разработчика) или **Software developer**, если у тебя есть организация.
2. Загрузи установщик из последнего релиза.
3. Заполни почту — на неё придёт ответ.
4. Пройди капчу и отправь.

## Текст заявки (English)

> **Subject:** Possible false positive — Dictum 0.1.0 (open-source dictation app)
>
> Hello,
>
> Microsoft Defender flags our installer as `Trojan:Win32/<name>!ml`. We believe this is a false positive caused by ML heuristics applied to an unsigned build.
>
> About the application:
> - Dictum is an open-source, local-only voice dictation tool for Windows.
> - Source code: https://github.com/<owner>/<repo>
> - Release: https://github.com/<owner>/<repo>/releases/tag/v0.1.0
> - SHA-256: <hash from SHA256SUMS.txt>
> - The build is produced by GitHub Actions and attested: `gh attestation verify <file> --repo <owner>/<repo>`
>
> Design notes relevant to detection:
> - The app does **not** install a low-level keyboard hook. The push-to-talk chord is detected by polling `GetAsyncKeyState` every 30 ms.
> - The app does **not** read or modify the clipboard. Text is inserted with `SendInput` + `KEYEVENTF_UNICODE`.
> - The app does **not** create autostart entries, services, or scheduled tasks.
> - The app makes **no network connections**. Speech recognition runs offline via whisper.cpp.
>
> Please reanalyze the attached installer. If the detection is confirmed as a false positive, we would appreciate the signature being removed.
>
> Thank you.

## Что приложить

- установщик из релиза;
- ссылку на коммит, из которого он собран;
- `SHA256SUMS.txt` или хеш в тексте;
- по возможности — ссылку на отчёт VirusTotal.

## Сроки

Обычно ответ приходит в течение 24–72 часов. Если детект признают ложным, сигнатуру убирают в одном из ближайших обновлений Defender; у пользователей это занимает до суток после выхода обновления.

## Если отказали

- проверь, что релиз подписан и подпись валидна (`Get-AuthenticodeSignature`);
- прогони файл через VirusTotal и приложи разбивку: если детектит только один движок из семидесяти, это сильный аргумент;
- подавай повторно после изменения поведения, которое могло вызвать детект.
