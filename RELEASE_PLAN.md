# Hollowbound — упаковка, проверка и выпуск

Дата: 2026-09-30. Статус: инструкция для будущих билдов. В рамках обновления документов publish, загрузка в магазины, commit/tag/push и выпуск не выполнялись. Версии, даты и acceptance-критерии: [ROADMAP.md](ROADMAP.md).

## Предпочтительный маршрут

1. 0.4.2: закрытый Windows ZIP для 5–10 тестеров, сохранение baseline и отчёта.
2. 0.5–0.9: показывать короткие playable изменения, карты производства и истории миров; вести devlog и собирать feedback.
3. 0.10: бесплатная demo на itch.io; Steam Coming Soon и Playtest/demo после готового показательного игрового опыта.
4. 1.0 RC: проверка фиксированного scope, release build и магазинных checklist.
5. 1.0: платная полная локальная игра, сохранённая бесплатная demo и поддержка обновлений. Цену выбрать по содержанию, отзывам и отдельному исследованию сопоставимых игр перед продажей.

Платная alpha/Early Access — отдельное решение после demo, если доступная версия уже оправдывает покупку и есть ресурс поддерживать пользователей. Календарную дату и дополнительные платформы объявлять по фактической готовности. Оплата app fee и публичная публикация требуют отдельного действия владельца проекта.

## Ответственные и артефакт релиза

Владелец проекта утверждает ревизию, страницу, цену и момент выпуска. Разработка готовит сборку, validation, контент и release notes. Изолированная проверка выполняется на тестовых мирах; пользовательские saves сохраняются перед update/migration.

Официальный артефакт собирается из конкретного проверенного commit и закреплённых SDK/packages/tools. Локальный preview может иметь `dirty=true` и сохранённый patch manifest; он обозначается preview и не подменяет официальный воспроизводимый release.

Каждый release manifest содержит product version, commit, ruleset/hash, snapshot version, analytics schema, RID, SDK, дату сборки, зависимости и SHA-256 ZIP. Номер в EXE/menu, manifest, changelog, tag и странице совпадает. Текущая стартовая база: продукт 0.4.2-alpha.0 (кодовый checkpoint), snapshot v24, analytics v5. Переносимая alpha.1 ещё не собрана и не опубликована.

## Подготовка pipeline — ещё предстоит реализовать

Version/build stamp уже реализован: `--version`, `--build-info` (JSON) и заголовок
окна читают metadata EXE/assembly. Git commit и dirty state снимаются при сборке;
недоступное состояние обозначается `unknown`/`null`. Базовая версия остаётся
`0.4.2-alpha.0`, build/publish принимает `-p:Version=...`. Это часть подготовки
0.4.2, а не готовый пакет или подтверждение release gate. JSON stamp ещё не
заменяет release manifest: ruleset/hash, дата, зависимости, patch manifest
для dirty preview и SHA-256 ZIP должны формироваться pipeline отдельно.

1. Ввести `global.json` с проверенным SDK, поддерживать локальный tool manifest и выбранный способ фиксации NuGet dependencies.
2. Добавить Windows CI workflow: tool restore → restore → Release build с Content → regressions → publish → проверка состава → ZIP/checksum. Закрепить ревизии CI actions и сохранить logs как артефакты.
3. Подключить существующий version/build stamp к меню и manifest generation. Секреты магазина хранить в защищённом окружении публикации, внутренние CI checks не должны иметь доступ к ним.
4. Уточнить лицензию исходного кода и права на все включённые assets. Подготовить список third-party licenses. Текущий UIFont использует установленный Segoe UI: выбрать поставляемый с проектом шрифт с подходящей лицензией и проверить font build/кириллицу на чистом runner.
5. Добавить в будущую конфигурацию Git исключение generated `artifacts/`; формировать пакет из publish directory и списка разрешённых файлов. Пользовательские worlds, локальные логи, ключи и диагностические дампы не являются игровым content.

Self-contained включает runtime и позволяет запуск без SDK/.NET-установки. Рекомендация упаковки основана на [MonoGame packaging](https://docs.monogame.net/articles/getting_started/packaging_games.html) и [Microsoft deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/). Нативные библиотеки и графические/аудиодрайверы всё равно проверяются реальным запуском на поддерживаемой машине.

## Команды локальной подготовки

Это будущая процедура, а не запись об уже выполненной сборке. Запускать из корня выбранной release-ревизии:

После каждого CLI шага проверять `$LASTEXITCODE`; ненулевой код останавливает подготовку кандидата. CI выполняет те же шаги с fail-fast и сохраняет вывод.

```powershell
dotnet tool restore
dotnet restore Hollowbound.sln
dotnet build Hollowbound.sln -c Release -v minimal
dotnet run --project Hollowbound.csproj -c Release --no-build -- --self-test all
dotnet run --project Hollowbound.csproj -c Release --no-build -- --test-camera-layout
```

После изменения экономики добавить targeted regression-сценарии соответствующих систем в существующий runner и полный набор. Не документировать будущий сценарий как существующую CLI-команду до реализации.

Текущий CLI позволяет записать дополнительную baseline-информацию:

```powershell
dotnet run --project Hollowbound.csproj -c Release --no-build -- --self-test multi-seed
dotnet run --project Hollowbound.csproj -c Release --no-build -- --benchmark 12345 4000 500 300
dotnet run --project Hollowbound.csproj -c Release --no-build -- --playtest
```

Benchmark повторяется на одной машине после прогрева. Запрошенный x500 не является acceptance-требованием фактического x500. GUI FPS, звук, ввод, сворачивание и длительный фон проверяются в реальном окне. Camera/layout сценарии текущего кода сигнализируют об ошибках исключениями; CI проверяет exit code и итоговую строку `All tests passed!` отдельно от `[PASS]` self-test runner.

Publish отдельной папкой. Пример номера для первой плановой alpha; при другой версии изменить значение:

```powershell
$releaseVersion = '0.4.2-alpha.1'
$releaseDir = Join-Path (Get-Location) "artifacts\Hollowbound-$releaseVersion-win-x64"
if (Test-Path -LiteralPath $releaseDir) {
    throw 'Каталог уже существует. Выберите новый номер кандидата или проверьте его вручную.'
}
dotnet publish Hollowbound.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:TieredCompilation=false "-p:Version=$releaseVersion" -o $releaseDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
```

Первоначально использовать папку с EXE, DLL/native libraries и compiled `Content/`. Single-file, trimming и Native AOT вводятся только после отдельной проверки сохранений, content и native loading. Опция `Version` задаёт metadata сборки и используется существующими CLI/build stamp и заголовком окна. Вывод в меню и реальную GUI-проверку выполнить отдельно.

Перед архивированием добавить Player Guide, лицензии и generated manifest; проверить состав. Затем:

```powershell
$releaseZip = "$releaseDir.zip"
if (Test-Path -LiteralPath $releaseZip) { throw 'ZIP already exists' }
Compress-Archive -Path (Join-Path $releaseDir '*') -DestinationPath $releaseZip
Get-FileHash -LiteralPath $releaseZip -Algorithm SHA256
```

ZIP собирается после окончательной проверки содержимого; любое изменение файлов требует нового ZIP и checksum. Примерные команды не создают финальный manifest автоматически — это задача будущего pipeline.

## Проверка опубликованного кандидата

- Распаковать ZIP в новый путь с пробелами/кириллицей на машине без SDK; запустить EXE из папки и shortcut с другим working directory. Проверить загрузку font/content, звук, ввод, fullscreen и UI scale.
- Создать изолированный мир; сыграть 30–60 минут; сохранить, закрыть, загрузить. Проверить обычное сворачивание и выбранный background-режим. Проверку через temporary playtest отделить от проверки обычного user-data path.
- Проверить доступность автосохранения: текущий default `%LOCALAPPDATA%\Hollowbound\saves\autosave.json`; при отсутствии LocalApplicationData код использует fallback. Зафиксировать действия при read-only папке или ошибке записи.
- На копиях beta/legacy saves проверить migration, отказ от неизвестной будущей версии, повреждённый файл, backup и отмену catch-up. Путь update не удаляет исходные пользовательские данные.
- Убедиться, что тесты идут в Release и результаты относятся к точному candidate. В packaged EXE проверить хотя бы headless continuation/ecology и выход по ошибке; WinExe CLI-лог проверять захватом stdout/exit code в launcher/CI.
- На зафиксированной машине записать FPS/frame time, actual TPS, CPU/RAM, размер save/log, population/организации/узлы. Провести фоновые 2 часа и отдельный 8-часовой soak. Указать комфортный профиль и известные пределы.
- Проверить понятность нового мира, производства, отношений и возврата по наблюдениям пользователей. Acceptance-цели и размер выборки взять из активного roadmap.

Найденная потеря save, валюты, товаров или незавершённых заданий блокирует release. Приближения экономики или ограничения масштаба должны быть описаны и соответствовать странице игры. Конкретные системные требования выводятся из RC-замеров.

## GitHub и itch.io

GitHub Release подходит для воспроизводимых download/notes/checksum. В публикацию входят ZIP, checksum, короткое описание реализованных возможностей и validation. Указать pre-release для alpha/beta. Ревизию, commit/tag/push и публичность утверждает владелец; локальные uncommitted изменения и чужие saves автоматически не включаются.

Для itch.io:

1. Создать Downloadable project, загрузить честный trailer/скриншоты, описание первой сессии, controls, requirements и известные ограничения. Начать с ограниченного доступа тестерам, проверить download/install, затем открыть demo.
2. Подготовить отдельные каналы Windows demo/beta/stable. Установить actual version и platform metadata. Тестовый upload сначала проверить на закрытом проекте/доступе; имя канала само по себе не гарантирует приватность.
3. Загружать publish directory через butler или подготовленный ZIP вручную. Ниже пример будущей команды, `PUBLISHER` заменить на настоящий аккаунт:

```powershell
butler push $releaseDir PUBLISHER/hollowbound:windows-demo --userversion $releaseVersion
```

4. Скачать пакет как игрок, сравнить checksum/manifest и пройти smoke-session. После обновления повторить проверку сохранения предыдущей версии.

Команда/version channels: [официальный butler manual](https://itch.io/docs/butler/pushing.html). Согласно [itch.io Terms](https://itch.io/docs/legal/terms), publisher должен отвечать возрастным/договорным условиям; для разработчика младше 18 отдельно оформить необходимое согласие родителя/опекуна и правомерную возможность принять условия до upload. Это пункт подготовки аккаунта, не блокер локальных билдов.

## Steam

На дату проверки 2026-09-30 [Steamworks onboarding](https://partner.steamgames.com/doc/gettingstarted/onboarding) указывает app fee $100 USD или эквивалент за продукт, оформление договоров, идентификации и банковских/налоговых сведений. На этой же странице для первых продуктов указаны ожидание 21 день после оплаты и публичная Coming Soon минимум две недели. Перед выбором даты сверить действующий минимум и статус конкретного приложения в Steamworks; условия могут измениться.

Договорную сторону, доступность выплат и полномочия подписанта определить до оплаты; возрастные и региональные вопросы уточнять по реальным условиям аккаунта/поддержке. Не рассчитывать на будущие поступления до подтверждения onboarding.

Порядок действий:

1. После показательной playable demo подготовить pitch, gameplay trailer, screenshots, capsules, features, требования, языки, описание текущего scope и цену. Показать реальные производство/макроуправление/возврат; демонстрация прототипа маркируется стадией.
2. Завершить onboarding; создать приложение, назначить разрешения владельцу/публикатору. Подать страницу на review, затем выставить Coming Soon и начать собирать wishlists/feedback.
3. Настроить Windows depot, launch EXE, Content, зависимости и private beta branch. Загрузить через SteamPipe согласно актуальной документации, проверить скачанный билд через Steam на чистой машине.
4. Заполнить Content Survey, ratings и сведения об используемом AI/контенте согласно текущей форме. Проверить лицензии на опубликованные материалы.
5. При необходимости настроить demo/Steam Playtest отдельным продуктовым flow; проверить установку, сохранения и разделение данных demo/full game. Интеграции achievements/cloud вводить только с проверкой работы базовой локальной игры.
6. Завершить review страницы, build и configuration, выдержать все интервалы, сравнить точный approved build с tested candidate, утвердить дату/цену и выполнить release controls.

Steam проверяет страницу и build отдельно; страницы/сборки требуют review перед выпуском. Процесс и полномочия описаны в [Steam Release Process](https://partner.steamgames.com/doc/store/releasing). Буфер на review/исправления добавить к графику; точную дату назначать после готового RC и выполненных portal checklists.

## День выпуска и поддержка

1. Заморозить verified candidate; опубликовать notes, requirements, limits, support channel и способы получения логов. Проверить download от имени обычного игрока.
2. Проверить install/update, соответствие версии, сохранения и checkout покупки/скачивания, если есть платная версия. Не использовать production worlds для smoke-проверок.
3. В первые 48–72 часа отслеживать crashes, migration/save failures, economy duplication и startup blockers. Исправлять по влиянию на игроков, выпускать 1.0.x с обновлёнными notes/checksum.
4. Сохранить предыдущий артефакт и manifest. Rollback приложения возможен только если его save reader понимает новую схему; иначе восстановить backup или подготовить forward hotfix. Старый EXE с несовместимым новым save не считается безопасным rollback.
5. Через 1–2 недели изучить возвраты, понимание, игру в фоне и реальные истории; выбрать следующий срез по feedback. Remote telemetry — только с отдельным opt-in; достаточны добровольные логи, интервью и наблюдения.

## Release checklist для конкретного кандидата

- [ ] Утверждены версия, commit/ruleset, scope и точный артефакт.
- [ ] Пройдены соответствующие regressions, continuation, migrations и economic invariants.
- [ ] GUI, packaged EXE, чистая установка, update и background проверены.
- [ ] Требования к машине и фактический масштаб подтверждены RC-замерами.
- [ ] Пользовательские saves защищены; recovery и rollback/hotfix понятны.
- [ ] Состав пакета, лицензии, шрифт, manifest и checksum проверены.
- [ ] Store metadata, возрастные/договорные условия, reviews и сроки выполнены.
- [ ] Notes, Player Guide, support и предыдущий артефакт доступны.
- [ ] Публичный выпуск утверждён владельцем проекта.

Заполненный список и ссылки на артефакты добавляются в CHECKPOINT/CHANGELOG при фактическом выпуске. Незаполненный checklist в этом документе — план действий.
