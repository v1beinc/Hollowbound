# CHECKPOINT — рабочий снимок состояния для агента

> Обновляется в конце каждой рабочей фазы. После сжатия контекста читать этот
> файл ПЕРВЫМ, затем выборочно — файлы из карты ниже. Не заменяет git diff.

## Последняя фаза — 2026-09-10: Living Colonies

Актуальный пакет **0.4.0-prealpha**, snapshot **v20**. Все изменения локальные,
без commit/push; старый блок 0.3.0 ниже — история, не текущий gate.

- `EmergentSimulationWorld` разделён через partial; новый `ColonyEcology.cs`
  содержит питание, локальную оплату рождений, истощение и порчу запасов.
- Убрана бесплатная энергия за сбор/складирование/отдых. Кормление расходует
  переносимую единицу или запас в пределах 6 проходимых шагов (BFS, не сквозь стены).
  Это абстракция локальной раздачи, не полноценная доставка к складу.
- Колонии проверяют рождение независимо, с фазовыми сдвигами. Нужны родители
  старше 120 simulation seconds, энергия >75, свободное место, 6 еды + резерв
  в пределах 12 шагов. Незаселённые жители пока используют общую bootstrap-группу.
- Строители оставляют 40 энергии после оплаты стены; отдых не выбран при энергии <40.
- Истощение: минимум 8 жителей, первое предупреждение не ранее тика 3000,
  подготовка 800 тиков, активная фаза 1800, перерыв 4500. Зона Manhattan radius18
  около крупнейшего поселения. Bloom защищён. Пауза только по решению игрока.
- Порча раз в500 тиков: склады >8 теряют max(1, amount/100).
- Новые MaxAge=3600 seconds (36k тиков); старые сохранённые MaxAge не меняются.
- Двойной подсчёт запасов в кризисе/целях фракций исправлен; устаревший дальний
  Danger больше не даёт бесконечный крик. Смерть dormant от возраста не starvation.
- Экология сериализуется и валидируется в v20; старые сейвы получают grace3000.
  `--self-test ecology`: питание/местные рождения/стены + точный save/load двух фаз.
- JSONL snapshots и session_summary содержат `colony_ecology`; CLI `--analyze-log`
  выводит её и потребление пищи. Старые логи честно показывают unavailable.
- Аналитика UI содержит причины блокировки роста и потери пищи. Контур и подсказка
  объясняют кризис; UI не изменяет simulation state при отрисовке.

Проверки: окончательный полный прогон `--self-test all` 11/11 PASS (262s).
Roundtrip/continuation совпали, soak12k без нарушений. Responsiveness15 комбинаций:
0 stalls>100ms, 8 предупреждений>12ms, максимум около30ms. Это не гарантия60FPS.
Сборка 0/0, camera/layout21/21. Автоматический PNG1600x900 проверен визуально;
это не замена ручному тесту управления. Прогон seed424242: 10k ticks, pop26,
births24/deaths0, consumed226. Итоговый seed286830073: 40k ticks,
pop384 (пик570), births746/deaths364, consumed23112, foodSpentOnBirths4476,
foodSpoiled1797; 563 задержки рождения из-за местной еды. ~375 TPS измерены
параллельно с регрессиями, не использовать для заявления о приросте производительности.
Лог `simulation-20260910-172324145.jsonl`: 718 records, malformed0.
Важное ограничение: пять истощений дали DroughtFoodLost=0 на этом seed — в зоне
уже не было несобранной пищи; блокировка регенерации действует, но её отдельного
счётчика пока нет. Не утверждать доказанную силу кризиса; следующий баланс-пакет
должен проверить выбор зоны/предупреждения и эффект действий игрока.

Следующий gate: новый ручной мир и сравнение со старым сейвом, доступность еды,
понятность предупреждения на ускорении, полезность вмешательств. Не считать Phase1
завершённой. Дальний прогон может терять население; баланс не гарантирует рост.
Не обещать x500 или миллионы агентов. Полная архитектура экологии/войны/онлайн вне пакета.

## Предыдущая фаза — 2026-09-10: The Living Archive

Исторический блок 0.3.0; актуальное состояние описано выше.
Ветка `main`, HEAD `c6de03f`; все изменения патча остаются незакоммиченными.
Версия продукта: **0.3.0-prealpha**, snapshot **v19**.

- Новый слой представления в `Game1.Experience.cs` (partial Game1): интерфейс,
  меню, мини-карта, слои карты, колонии, график населения, фильтр Chronicle,
  процедурные эффекты. Движок и внешние зависимости не менялись.
- Passage (`4`, 2 Resonance) — реальная команда в simulation queue, не UI-чит.
- Исправлено размножение поселений при обнулении population перед recount;
  добавлены regression-сценарии `settlements` и `passage`.
- `TryScreenToCell` отделяет выбор на карте от clamped camera coordinates.
- Последняя проверка: `dotnet build -v minimal` 0/0;
  `--test-camera-layout` 21 PASS; `--self-test roundtrip` PASS (82.6 s, diffs=0).
  Предыдущий полный `--self-test all` дал 9/10: единственная ошибка — тест,
  ошибочно запрещавший повторяемую MigrationStarted. Теперь он проверяет
  прирост MigrationWaves и уникальные тики, а не просто исключает событие.
  Остальные девять сценариев прошли, включая 12k soak и Passage save/load.
- Benchmark `--benchmark 12345 2000 500 120`: 120 TPS, pop129, births9, deaths0.
  Ранее было346 TPS в других условиях; нельзя заявлять отсутствие регрессии.
- Native окно и PNG проверены визуально. Ввод GUI не полностью подтверждён:
  пользователь работал в других приложениях, автоматизация не продолжалась.
- `--playtest`: отдельный seeded мир, сохранение в TEMP, без перезаписи settings;
  `--preview path.png [ticks] [pop] [width] [height] [scale] [lens]`: снимок и выход,
  без сохранения мира. Оба режима всё ещё пишут обычные диагностические логи.
- Следующий gate: спокойный ручной тест меню/клавиш/resize/fullscreen и сравнение
  производительности при одинаковой нагрузке. Не начинать новую механику до него.
- Полные изменения и ограничения: `CHANGELOG.md`, раздел 0.3.0.

## Правила работы (заданы пользователем, действуют всегда)

- Работать ФАЗАМИ: сначала назвать цель, файлы в scope, критерий готовности;
  перед изменениями читать только нужные файлы.
- Останавливаться после 8–12 модельных шагов и давать checkpoint:
  подтверждено / изменено / осталось. После проверки — завершить фазу,
  не продолжать улучшения по своей инициативе.
- Рабочее дерево грязное постоянно (незакоммиченная работа пользователя) —
  это норма. Запрещены: `git reset|checkout|clean`, удаление saves/,
  commit без явной команды, новые внешние зависимости, многопоточная симуляция.
- Не начинать новый этап без команды. Не верить старым отчётам — проверять код.

## Проект

**Hollowbound** (`C:\it\Hollowbound`): C# / .NET 10 / MonoGame DesktopGL 3.8.5.1,
2D autonomous simulation sandbox (агенты, фракции, поселения, стены, еда,
поколения, Chronicle, LOD active/dormant/aggregated). GUI: `Game1.cs`,
`Camera2D`, `GameLayout`, SpriteFont и масштабируемые UI-примитивы. Headless: `--headless`, `--benchmark`,
`--benchmark-compare`, `--load`. Главный план: `ROADMAP.md`.

## Текущее состояние

**Phase 0: Stabilization — ЗАВЕРШЕНА целиком** (persistence/self-test gate +
runtime/acceleration gate).

**First Cycle: Player Agency — первый vertical slice готов и прошёл целевой save/load gate**. Реализован первый тип эксперимента `Resilient Settlement` с ограниченным ресурсом `Resonance` и тремя вмешательствами: `Bloom`, `Beacon`, `InsightPulse`.

**Shout v0 — ЗАВЕРШЕНА 2026-08-29.** Добавлена локальная коммуникация активных агентов через сигналы `Food`, `Danger` и `Rally`; обработка детерминирована, ограничена cooldown/лимитом на tick и не переписывает pathfinding, LOD или faction core.

**Shout v1 — ЗАВЕРШЕНА 2026-08-30.** Добавлено outcome-based learning: агент хранит отдельное доверие к трём типам сигналов, меняет его после подтверждённого результата и использует доверие при приёме будущих сообщений. Реализация не использует LLM и сохраняет масштабируемую модель Genome/Mind/Culture.

**Shout v2 — ЗАВЕРШЕНА 2026-08-30.** Добавлена bounded reputation memory: каждый агент хранит до 8 недавно услышанных источников с отдельным доверием к `Food/Danger/Rally`. Репутация источника влияет на reception и обучается вместе с outcome-learning; вытеснение записей детерминировано по давности и `SenderId`.

### UI Readability & Performance Foundation — ЗАВЕРШЕНА 2026-08-29

Цель: убрать ощущение debug-прототипа, исправить обрезку интерфейса, разделить HUD и debug-данные, добавить UI performance cache.

Реализовано:
- **Responsive layout**: минимальные физические ширины панелей (MinLeftPanelWidth=160, MinRightPanelWidth=240, MinTopBarHeight=36, MinBottomPanelHeight=100, MinWorldViewportWidth/Height=160/120) — UI scale масштабирует текст/spacing, но не сжимает панели ниже безопасного минимума. WorldViewport всегда положительный. Проверено на 640×360, 1280×720, 1920×1080, 3440×1440 при всех 4 UI scale (75/100/125/150%).
- **Читаемый UI-контент**: `DrawTextWrapped` для длинных строк (controls, First Cycle Guide, agent decisions) — нет обрезки через `...`. Tool buttons показывают название, стоимость, эффект, hotkey, disabled/active состояние. Inspector помещается в границы панели. Chronicle использует `DrawTextClipped`.
- **F3 Debug Overlay**: выносит технические данные из основного HUD (FPS, Sim Tick, Seed, LOD active/dormant/aggregated, Backlog, Catching Up, World/UI draw time, Camera pos/zoom, Viewport, Visible cells). Основной TopBar теперь компактный: Population, Food, Resonance, Score, Pause, Target/Actual speed.
- **UI Performance Cache** (`UICache`): кэширует форматированные строки (TopBar, Left/Right panel, Agent Inspector, Faction summary, Decision explanation) — обновляется 1 раз на simulation tick или при смене selected agent. Убраны per-frame `string.Join`, LINQ, MeasureString, списки для статичных элементов. Selected agent и hover обновляются мгновенно.
- **UI Interaction Safety**: `HandleInterfaceClick` перехватывает клики по Left/Right/Bottom/TopBar — не попадают в мир. Mouse wheel zoom только внутри WorldViewport (`IsPointInWorldViewport` guard). F3/F1/Ctrl+/- не ломают существующие controls. Chronicle кликабелен. Tool buttons работают мышью и 1/2/3.
- **Performance telemetry**: world/UI draw time в тиках (debug overlay), throttled только F3.

Новые/изменённые файлы:
- `Game1.cs` — DrawInterface rewrite с cache, DrawDebugOverlay, HandleCameraInput wheel guard, HandleInterfaceClick isolation.
- `Simulation/GameLayout.cs` — MinLeftPanelWidth/MinRightPanelWidth/MinTopBarHeight/MinBottomPanelHeight/MinWorldViewportWidth/Height, clamp к минимумам.
- `Simulation/CameraLayoutTests.cs` — +3 теста: `TestResponsiveLayoutMinPanelWidths`, `TestUIScaleAllScales`, `TestUIInputIsolation` (итого 20 тестов).

Результаты проверок (2026-08-29):
- `dotnet build -v minimal` — **0 warnings / 0 errors**.
- `--test-camera-layout` — **20/20 PASS** (включая новые тесты responsive layout, UI scale, input isolation).
- `--self-test interventions` — **PASS** (3.3 s).
- `--self-test roundtrip` — **PASS**, `diffs=0` (38.8 s).
- Simulation core, interventions, score, snapshots — неизменны.

### UI Readability Fix-pass — ЗАВЕРШЕНА 2026-08-29

После ручного аудита исправлены три точечные проблемы без изменения simulation
core, RNG, save/load или поведения агентов:

- `UICache` теперь принудительно заполняется на первом кадре (`_lastWorldTick = -1`),
  поэтому стартовая пауза не показывает пустой HUD/инспектор.
- FPS в F3 считается по реальному интервалу между кадрами через
  `Stopwatch.GetTimestamp()`, а world/UI draw time переводится в миллисекунды
  через `Stopwatch.Frequency`.
- На окне меньше рекомендуемого размера `GameLayout` больше не форсирует
  минимальный viewport поверх правой/нижней панели: viewport остаётся положительным
  и не пересекает соседние области. При нормальном размере минимальные ширины
  панелей сохраняются.

Проверки:
- `dotnet build -v minimal` — **0 warnings / 0 errors**;
- `--test-camera-layout` — **20/20 PASS**, включая проверку отсутствия overlap
  на below-minimum resize;
- `--self-test interventions` — **PASS**, включая pending/active save-load.

Осталось для отдельного пакета UI/analytics: часть динамических строк всё ещё
формируется в `DrawInterface`, а старые bitmap-renderer overloads остаются в
`Game1.cs` как технический долг; это не блокирует текущий fix-pass.

### Analytics Foundation — ЗАВЕРШЕНА 2026-08-29

Добавлен bounded-слой анализа поверх существующих JSONL-логов:

- каждый logger получает `run_id` и пишет `schema_version=2` в обычные записи и
  `world_event`;
- при штатном завершении GUI/headless/benchmark добавляется одна запись
  `kind=session_summary` с финальным состоянием, пиками population/food/walls,
  learning/decision/outcome counters, settlements, LOD и catch-up episodes;
- добавлен CLI `--analyze-log <path> [--json]`: он читает существующий JSONL,
  пропускает и считает malformed lines, группирует record kinds и Chronicle event
  types, показывает диапазон тиков, peak/final metrics и backlog;
- логирование остаётся периодическим и не добавляет per-agent/per-tick записи в
  горячий цикл симуляции.

Изменённые файлы:
- `Simulation/AnalyticsLogger.cs`;
- `AnalyticsReport.cs`;
- `Program.cs`;
- `HeadlessRunner.cs`;
- `Benchmark.cs`;
- `README.md`.

Проверки:
- `dotnet build -v minimal` — **0 warnings / 0 errors**;
- `--test-camera-layout` — **20/20 PASS**;
- `--self-test interventions` — **PASS**;
- headless smoke: seed `424242`, 800 ticks, 23 agents, 23 valid JSONL records,
  `session_summary` присутствует;
- `--analyze-log` — отчёт собран, malformed lines `0`, Chronicle типы, ключевые
  counters и Shout-типы распознаны.

Следующий feature-пакет `Shout v0` теперь завершён ниже; ручной UI-playtest и
более глубокое обучение доверию остаются отдельными задачами.

### First Cycle UX — Understand & Act — ЗАВЕРШЕНА 2026-08-29

...

Ручная проверка всё ещё обязательна: визуальная иерархия при 75/100/125/150%, мышь по трём инструментам, прохождение guide, клик по Chronicle, inspector, resize/fullscreen и отсутствие world-click через панели.

### UI Text & Scaling Foundation — ЗАВЕРШЕНА 2026-08-29

Цель фазы: сделать первый игровой цикл понятным без чтения README и без знания
горячих клавиш. Simulation core и детерминированные эффекты вмешательств не
менялись.

Реализовано:
- постоянная левая панель с кликабельными `Bloom`, `Beacon`, `Insight Pulse`;
- активный инструмент можно отключить повторным кликом или клавишей;
- все панели поглощают ввод: клик по UI не попадает в мир;
- пошаговый First Cycle guide: камера → выбор агента → запуск времени → наблюдение
  добычи еды → вмешательство → Chronicle;
- `F1` повторно запускает guide; завершение сохраняется в `settings.json`;
- Chronicle стал интерактивным: клик по событию центрирует камеру на его клетке,
  представителе фракции или живом агенте;
- inspector объясняет действие агента блоком `WHY THIS ACTION` (энергия, перенос
  еды, знания, опасность, поведенческие bias и последнее обучение);
- действующий `Food Crisis` вынесен в игровую панель и предлагает контекстное
  применение Bloom;
- настройки вынесены в `UISettingsStore`; тесты используют временный файл и
  больше не перезаписывают пользовательский `%LOCALAPPDATA%`.

Новые файлы:
- `Simulation/FirstCycleUXState.cs`;
- `Simulation/UISettingsStore.cs`.

Изменены в этой фазе:
- `Game1.cs`;
- `Simulation/GameLayout.cs`;
- `Simulation/UIPrimitives.cs`;
- `Simulation/CameraLayoutTests.cs`;
- `Simulation/EmergentSimulationWorld.cs` (только read-only статус Food Crisis);

Последняя автоматическая проверка:
- `dotnet build -v minimal` — **0 warnings / 0 errors**;
- `--test-camera-layout` — **17/17 PASS**;
- `--self-test interventions` — **PASS**;
- `--self-test roundtrip` — **PASS**, `diffs=0`.

Ручная проверка всё ещё обязательна: визуальная иерархия при 75/100/125/150%,
мышь по трём инструментам, прохождение guide, клик по Chronicle, inspector,
resize/fullscreen и отсутствие world-click через панели.

### UI Text & Scaling Foundation — ЗАВЕРШЕНА 2026-08-29

Новые файлы:
- `Content/Fonts/UIFont.spritefont` — SpriteFont (Consolas, 14pt, кириллица + ASCII).
- `Simulation/UITheme.cs` — централизованные константы темы (цвета, spacing, scale clamping).
- `Simulation/UIPrimitives.cs` — минимальные примитивы (DrawPanel, DrawButton, DrawTextClipped, TextAlign).

Изменённые файлы:
- `Content/Content.mgcb` — добавлена сборка SpriteFont.
- `Simulation/GameLayout.cs` — поддержка UI scale (TopBarHeight/RightPanelWidth/BottomPanelHeight теперь свойства с масштабированием; `WithUIScale()`).
- `Game1.cs`:
  - Загружает `_uiFont = Content.Load<SpriteFont>("Fonts/UIFont")`.
  - Полностью заменён старый bitmap `DrawText`/`Glyph` на `SpriteFont.DrawString`.
  - `Ctrl+-` / `Ctrl++` переключают scale 0.75/1.0/1.25/1.5.
  - Settings сохраняются атомарно в `%LOCALAPPDATA%\Hollowbound\settings.json` (temp + move).
  - Повреждённый/отсутствующий JSON безопасен → 100%.
  - Смена scale обновляет `_layout` и `_camera.SetViewport()` — камера position/zoom сохраняются.
  - HUD отрисован через новый `DrawText`/`DrawTextClipped` внутри layout rectangles.
  - UI scale feedback показывается в BottomPanel.
- `Simulation/CameraLayoutTests.cs` — расширены тесты:
  - 4 UI scale (75/100/125/150%) — проверка положительных viewport.
  - UISettings persistence (valid/invalid/missing JSON).
  - Все 17 актуальных тестов PASS, включая First Cycle UX и безопасное хранение settings.

Результаты проверок (2026-08-29):
- `dotnet build -v minimal` — **0 warnings / 0 errors**.
- `--test-camera-layout` — **17/17 PASS**.
- `--self-test interventions` — **PASS** (3.0 s).
- Simulation core, interventions, score, snapshots — неизменны.

### UI/Window/Camera Foundation (Phase 1-4) — ЗАВЕРШЕНА, FIX-PASS 2026-08-29

...

### Известные ограничения

- Active Bloom/Beacon и pending interventions проверяются сценарием `--self-test interventions`.
- Визуальный язык остаётся pre-alpha: функциональная компоновка и UX уже есть,
  но полноценный art/UI polish ещё не выполнен.
- Нет UI scaling настройки в меню (только Ctrl+-/Ctrl++ и settings.json).
- Chronicle центрирует камеру по клику; отдельного фокуса на поселении пока нет.
- No multi-seed и benchmark в этой фазе (длительные).

### Следующий этап

**Ближайший gate:** ручная проверка First Cycle UX в GUI. После подтверждения —
выбрать один продуктовый пакет: Results/New World flow либо визуальный polish и
мини-карта. Не начинать оба одновременно.

**Phase 1 — Window mode foundation:**
- `Window.AllowUserResizing = true`, минимальный размер 640×360.
- `HardwareModeSwitch = false` — borderless fullscreen.
- Сохранение windowed размеров, восстановление после fullscreen.
- `F11` / `Alt+Enter` — toggle fullscreen.
- `Escape`: 1) сбрасывает инструмент, 2) закрывает fullscreen, 3) выход.
- `OnClientSizeChanged` обновляет layout и камеру.

**Phase 2 — Screenshot & Analytics utilities:**
- `F12` — скриншот в `%USERPROFILE%\Pictures\Hollowbound\Screenshots` с UTC timestamp.
- `F10` — открывает `%LOCALAPPDATA%\Hollowbound\logs` (через `Process.Start`).
- Ошибки показываются через `_toolFeedback` и логируются в analytics.
- `AnalyticsLogger.LogPath` доступен UI.

**Phase 3 — Camera2D:**
- World position, zoom (min 0.25, max 4.0, default 1.0), pan.
- Zoom относительно курсора (колесо мыши в WorldViewport).
- Clamp к границам карты с margin 2 клетки.
- `WorldToScreen`, `ScreenToWorld`, `ScreenToCell`, `GetVisibleCellBounds`, `FitWorld`, `CenterOnCell`.
- Работает внутри `WorldViewport`, не всего окна.
- Ввод: WASD pan, средняя кнопка drag, колесо zoom, `Home` FitWorld, `F` фокус на выбранном агенте.

**Phase 4 — Layout foundation & Culling:**
- `GameLayout` вычисляет: `TopBar` (40px), `WorldViewport` (остаток), `RightPanel` (280px min), `BottomPanel` (120px min).
- Мир рисуется и принимает мышь ТОЛЬКО внутри `WorldViewport`.
- `SelectAgent` и `ApplyTool` используют camera/viewport conversion.
- Клики по UI никогда не применяют инструмент.
- World rendering использует scissor rectangle (`RasterizerState.ScissorTestEnable`).
- Off-screen entities (стены, еда, агенты, Blooms, Beacons) не рисуются.
- Запас 1–2 клетки вокруг visible bounds.
- `SamplerState.PointClamp` сохранён.
- HUD разделён по layout rectangles: краткий статус сверху, inspector справа,
  Chronicle и управление снизу. UI больше не перекрывает WorldViewport и не
  принимает клики как действия в мире.

**Automated tests (`--test-camera-layout`): 20/20 PASS**
- World→Screen→World round trip
- Screen corners → valid cells
- Zoom preserves world point under cursor
- View matrix учитывает ненулевой origin WorldViewport
- Resize viewport сохраняет position/zoom камеры
- Camera clamp to map bounds
- FitWorld shows entire map
- Visible cell bounds correct
- Small window (640×360) layout valid
- Below-minimum resize сохраняет валидный viewport
- Standard resolutions (1280×720, 1920×1080) layout correct
- Ultrawide (3440×1440) layout valid
- UI scale-ready layout recalculation works
- Click outside WorldViewport correctly rejected

**Foundation fix-pass 2026-08-29:**
- исправлено смещение rendering matrix относительно hit-testing на высоту TopBar;
- resize больше не пересоздаёт Camera2D и не сбрасывает position/zoom;
- fullscreen transition не перезаписывает сохранённый windowed size;
- WASD pan зависит от elapsed time и применяет zoom ровно один раз;
- scissor RasterizerState кэшируется и освобождается вместо создания каждый кадр;
- F12 откладывается до Draw и сохраняет фактически отрисованный backbuffer;
- GameLayout сохраняет положительный WorldViewport даже при размере окна ниже
  рекомендованных 640×360;
- удалены устаревшие методы старой screen-to-cell геометрии.

### Snapshot v14 (`Simulation/WorldSnapshot.cs`)

...

### Self-test (`Simulation/SimulationRegressionRunner.cs`, CLI `--self-test`)

...

### Результаты проверок (2026-08-29)

- Сборка: **0 warnings / 0 errors**.
- `--test-camera-layout` — **PASS, 20/20** (актуальный набор после UX-фазы).
- `--self-test interventions` — **PASS**, pending/active save-load проверены.
- `--self-test roundtrip` — **PASS**, `diffs=0` (44.7 s).
- `--self-test continuation` — **PASS** (58.0 s).
- `--self-test all` — частично запускался (последние сценарии потребовали >300s, не ждали).
- Не запускались в этой фазе: `multi-seed` (100 seeds), `benchmark` (4000 тиков).

### Что требует ручной проверки

- Переключение fullscreen/windowed (F11, Alt+Enter) — растягивание окна мышью.
- Скриншот (F12) — файл создаётся в Pictures/Hollowbound/Screenshots.
- Открытие папки логов (F10) — открывается проводник.
- Камера: WASD, middle-drag, wheel zoom, Home, F.
- Клики вне WorldViewport не применяют инструмент.
- Зум сохраняет точку мира под курсором.

### Известные ограничения

- HUD функционально разложен по layout rectangles и использует SpriteFont,
  wrapping и UI scale; художественный polish остаётся pre-alpha.
- UI scale доступен через Ctrl+-/Ctrl++ и сохраняется; отдельного меню нет.
- Chronicle центрирует камеру по клику; отдельного фокуса на поселении пока нет.
- No multi-seed и benchmark в этой фазе (длительные).

### Следующий этап

**Ближайший gate:** ручная GUI-проверка First Cycle UX; затем один ограниченный
продуктовый пакет, выбранный по результатам проверки.

### Snapshot v14 (`Simulation/WorldSnapshot.cs`)
- `CurrentVersion = 14`, `DeterminismBlockVersion = 11`.
- v11-блок: scheduler offsets, EventCooldown, grid-кэши, LOD counters.
- v12-блок: `OnceEventTypes`, `RecordedGenerations` (anti-replay).
- v13-блок (First Cycle): `Resonance`, `ResonanceRegenTick`, `TotalResonanceSpent`, `SuccessfulInterventions`, `FailedInterventions`, `PendingInterventions`, `InterventionLog`, `ActiveBlooms`, `ActiveBeacons`, score components (`ScoreLastTick`, `ScorePopulationComponent`, `ScoreFoodComponent`, `ScoreCrisisComponent`, `ScoreSettlementComponent`, `ScoreEfficiencyComponent`, `ScoreTotal`).
- v14-блок: `FoodCrisisCount`, `FoodCrisisRecoveredCount`, `BloomEffect.RemainingBoost`, `BloomEffect.CreatedSource`.
- Совместимость: v10/v11/v12/v13 грузятся; strict continuation для текущего формата подтверждён целевым intervention-сценарием.

### Snapshot v15 / Shout v0 (`Simulation/WorldSnapshot.cs`)
- `CurrentVersion = 15`.
- Сохраняются общие счётчики `ShoutsMade`/`ShoutsHeard`, счётчики по типам `FoodShouts`/`DangerShouts`/`RallyShouts`, cooldown и последнее услышанное/отправленное сообщение каждого агента.
- Legacy v10–v14 загружаются с безопасными значениями по умолчанию для Shout-полей.

### Shout v0 (`Simulation/ShoutSignal.cs`, `EmergentSimulationWorld.cs`)
- `Food`: передаёт локальное знание о найденной еде внутри фракции.
- `Danger`: распространяет предупреждение об опасном или неудачном направлении.
- `Rally`: зовёт союзников к строительству, исследованию или поселению.
- Радиус 5–14 клеток, затухание по Manhattan distance, социальность и интеллект влияют на шанс и приём.
- Обрабатываются только active-агенты, максимум 8 новых сигналов за tick; визуальный сигнал живёт ограниченное число тиков.
- Аналитика: `shouts_made`, `shouts_heard`, `food_shouts`, `danger_shouts`, `rally_shouts`, `active_shouts`.
- `--self-test shouts` проверяет наличие сигналов, баланс счётчиков, save/load и детерминированное продолжение.

### Snapshot v16 / Shout v1 (`Simulation/WorldSnapshot.cs`)
- Сохраняются `ShoutFoodTrust`, `ShoutDangerTrust`, `ShoutRallyTrust`, последнее сообщение/его sender и флаг оценки результата.
- Сохраняются счётчики `ShoutLearningEvents`, `SuccessfulShoutLessons`, `FailedShoutLessons` на агента и в мире.
- Для v15 и более старых snapshot новые trust-поля получают нейтральное значение `0.5`.
- Self-test подтверждает, что outcome-learning и продолжение после save/load остаются детерминированными.

### Snapshot v17 / Shout v2 (`Simulation/WorldSnapshot.cs`)
- Для каждого агента сохраняется до 8 `ShoutReputationSnapshot` с `SenderId`, доверием по трём типам, количеством услышанных сигналов, уроков и последним tick.
- Старые v10–v16 snapshot загружаются без репутационных записей и начинают с нейтрального доверия.
- Self-test проверяет bounded memory, source trust, save/load и продолжение.

### Self-test (`Simulation/SimulationRegressionRunner.cs`, CLI `--self-test`)
Сценарии: `roundtrip` | `continuation` | `v10` | `chronicle` | `soak` | `responsiveness` | `interventions` | `shouts` | `multi-seed`.
Новый сценарий `interventions` проверяет:
1. Два одинаковых мира с фиксированным seed.
2. Применение одинаковых команд в одинаковые ticks.
3. Bit-exact совпадение после вмешательств.
4. Bloom, Beacon, InsightPulse работают.
5. Списание Resonance, cooldown, invalid command, Chronicle.
6. Save/load в середине очереди/эффекта.
7. Продолжение после load.
8. Старые self-tests не сломаны.

### Результаты проверок
- Агентом ранее заявлены полный self-test и benchmark; их результаты не следует считать повторно проверенными в этой фазе.
- Последняя локальная проверка review-патча: отдельная сборка **0 warnings / 0 errors**.
- Последняя локальная проверка review-патча: `--self-test interventions` — **PASS**, 3 команды, pending save/load, active save/load и 510 тиков продолжения.

### Time management
- `FrameSimulationBudgetMilliseconds`: default **6 мс** (GUI), headless/benchmark явно ставят 200 мс.
- Телеметрия: `BacklogSeconds`, `SmoothedActualSpeed` (EMA), catching-up episodes.
- Autosave отложен при catching-up.

### First Cycle: Player Agency (реализовано)
- **Resonance**: старт 3, реген 1 за 5000 тиков, макс 10.
- **Bloom** (1 Resonance): временно усиливает источник еды в клетке (+30 еды на 500 тиков). Агенты сами решают идти туда или нет.
- **Beacon** (1 Resonance): создаёт сигнал притяжения в клетке на 200 тиков. Влияет на выбор цели агентов в Exploring.
- **InsightPulse** (1 Resonance): передаёт nearby агентам знание (+0.3 FoodKnowledge, +0.2 RouteKnowledge, +0.1 LearningRate в радиусе 5).
- **Command queue**: команды игрока применяются на границе tick для детерминизма.
- **Intervention log**: tick, тип, клетка, стоимость, успех/причина — в JSONL аналитике.
- **Score `Resilient Settlement`**: breakdown (Population, Food, Crisis, Settlement, Efficiency), сохраняется в снапшоте.
- **GUI**: `1/2/3` выбирают инструмент, ЛКМ применяет к клетке, HUD показывает Resonance, Tool, Score, feedback сообщения.
- **Визуализация**: Blooms — зелёное свечение, Beacons — синий пульс.

### Карта файлов (добавлено/изменено)
- `ROADMAP.md` — добавлен подраздел First Cycle: Player Agency Contract.
- `Simulation/EmergentSimulationWorld.cs` — ядро вмешательств, очередь команд, Resonance, 3 действия, score, snapshot v13.
- `Simulation/WorldSnapshot.cs` — v14 новые типы снапшотов для интервенций.
- `Simulation/AnalyticsLogger.cs` — логирование Resonance, score, intervention events.
- `Simulation/ResourceSpatialIndex.cs` — метод `Update` для инкрементального обновления.
- `Simulation/SimulationRegressionRunner.cs` — сценарий `interventions`.
- `Simulation/SpatialIndex.cs`, `Simulation/WallSpatialIndex.cs`, `Simulation/Map.cs` — стабильный порядок обхода и независимое хранение path-cache.
- `CHANGELOG.md` — описание текущего рабочего патча v0.2.0-prealpha.
- `Game1.cs` — ввод игрока (1/2/3 + ЛКМ), HUD, отрисовка эффектов.

### Команды проверки
```
dotnet build -v minimal          # 0 warnings / 0 errors
dotnet run --no-build -- --self-test all
dotnet run --no-build -- --self-test interventions
dotnet run --no-build -- --benchmark 12345 4000 500 300
```
- `FrameSimulationBudgetMilliseconds`: default **6 мс** (GUI), headless/benchmark
  явно ставят 200 мс. Бюджет ограничивает число шагов, не их содержание.
- Санитизация входов Advance: NaN/∞/≤0 → инертная пауза.
- Телеметрия: `BacklogSeconds`, `SmoothedActualSpeed` (EMA ~0.5 c),
  `CatchingUpEpisodes/CurrentCatchingUpForSeconds/LongestCatchingUpEpisodeSeconds`.
- Game1: HUD `TARGET xN | ACTUAL xM + backlog`; autosave отложен при catching-up,
  длительность save пишется в аналитику (`saved ... duration_ms`).
- AnalyticsLogger: snapshot-поля smoothed/backlog/budget/episodes + события
  `catching_up_started/ended`.

## Исправленные баги (краткий реестр)

1. Стены не регистрировались в чанках (RegisterWall/UnregisterWall при build/dig).
2. Детерминизм после load: scheduler offsets, event cooldowns, grids,
   `_knownClusterChunks`, LOD-счётчики теперь персистятся.
3. Dictionary-order недетерминизм: tie-break'и в TryConsumeStoredFood,
   FindOrCreateSettlement/FindNearestSettlement (+ThenBy Id), стабильная сортировка
   ресурсов/кандидатов строительства, ShareFoodKnowledge receiver'ы по Id,
   ordered итерации поселений для порядка Chronicle.
4. `FirstElder` не звал MarkRecordedOnce → спам «первых» событий.
5. `RestoreChronicleState` ошибочно клал MigrationStarted/settlement-типы в once-set.
6. Фантомные события при загрузке → флаг `_suppressChronicleRecording`.
7. `UpdateAgentLOD` обнулял чужой `AggregatedAgentCount` каждый тик.
8. Flaky LOD-инвариант → точная семантика: unclassified ≤ `BirthsThisTick`
   (поля `_birthsThisTick`/`UnclassifiedAliveAgents` добавлены).
9. Map.FindNearestWall(/Approach): детерминированный tie-break по координате.

## Карта файлов

- `Program.cs` — CLI-диспетчер (benchmark/headless/load/--self-test/--deterministic-test алиас/GUI).
- `Game1.cs` — MonoGame-оболочка, ввод, HUD, autosave каждые 30 c.
- `Simulation/EmergentSimulationWorld.cs` (~3.3k строк) — ядро: Step, LOD,
  Chronicle, поселения, birth/death, FromSnapshot/CreateSnapshot.
- `Simulation/SimulationRegressionRunner.cs` — весь self-test.
- `Simulation/WorldChunks.cs` — ChunkData struct, SetChunk обязателен для записи.
- `Simulation/WorldSnapshot.cs`, `AnalyticsLogger.cs`, `HeadlessRunner.cs`,
  `Benchmark.cs`, `Map.cs`, `ResourceSpatialIndex.cs`.

## Команды проверки

```
dotnet build -v minimal          # ожидание: 0 warnings / 0 errors
dotnet run --no-build -- --self-test [scenario]
dotnet run --no-build -- --benchmark 12345 4000 500 300
```

Эталон benchmark (seed 12345): до оптимизаций 36.0 TPS → после 47.1 TPS
(холодный прогон; ~33 может быть троттлинг после тяжёлой батареи).

## Известные ограничения / наблюдения (не задачи!)

- Один `Step()` на pop~300 стоит ~20–45 мс → ACTUAL x6–27 на высоких скоростях;
  микроптимизация тика возможна, но НЕ заказана.
- Микропоселения множатся (680 поселений / 364 агента) — прегрешение баланса
  `FindOrCreateSettlement`, существовало до вмешательств.
- Legacy (v10/v11) replay неполон по определению.
- Бисекция локализует divergence с точностью 20 тиков.
- `CompareWorlds` вручную зеркалит поля агентов — дрейфует при добавлении полей.
- Сохранение v12 несёт ~165 KB grid-данных (осознанный tradeoff).
- `_agentLodById.Clear()` каждый тик (пересборка) — ок; память не течёт.

## Текущая проверка после Shout v0 — 2026-08-29

- `dotnet build -v minimal` — **0 warnings / 0 errors**.
- `--self-test shouts` — **PASS**: `made=130`, `heard=1153`,
  `food=2`, `danger=105`, `rally=23`, `visible=0`, save/load и продолжение PASS.
- `--test-camera-layout` — **20/20 PASS**.
- `--self-test interventions` — **PASS**: команды, score и pending/active save/load.
- `--self-test roundtrip` — **PASS**: `ticks=3200+600`, `pop=284`,
  `chronicle=48`, `settlements=275`, `aggregated=63`, `diffs=0`.
- Тесты запускались без GUI; ручная проверка визуальных колец сигналов и читаемости
  инспектора остаётся отдельным шагом.

### Shout v2 verification — 2026-08-30

- Build: **0 warnings / 0 errors**.
- `--self-test shouts`: **PASS**, `made=130`, `heard=1153`,
  `food=2`, `danger=105`, `rally=23`, `learning=140 (133+/7-)`, save/load PASS.
- `--self-test v10`: **PASS**, legacy v10/v11 continued for 700 ticks.
- `--self-test roundtrip`: **PASS**, snapshot v17, `pop=284`,
  `settlements=275`, `aggregated=63`, `diffs=0`.
- `--self-test interventions`: **PASS**.
- `--test-camera-layout`: **20/20 PASS**.

### Shout v3 verification — 2026-08-30

Shout v3 добавляет bounded multi-signal memory и коллективную надёжность
сигналов на уровне фракции поверх Shout v0–v2.

- Каждый агент хранит до 6 воспоминаний о конкретном сигнале, отправителе,
  типе и клетке; слабые/старые записи вытесняются, а влияние устаревает через
  `ShoutMemoryLifetimeTicks`.
- При проверке исхода обновляются одновременно память места, репутация источника
  и reliability фракции отдельно для `Food`, `Danger` и `Rally`.
- Память сигналов учитывается при выборе еды и опасных направлений; это не
  заменяет существующую pathfinding/LOD-архитектуру.
- Snapshot поднят до **v18**. Сохраняются agent memories и faction reliability;
  v10–v17 загружаются с безопасными значениями по умолчанию.
- JSONL/UI дополнены `shout_memory_records` и
  `faction_shout_learning_events`.
- Regression-сценарий `shouts` теперь проверяет bounded memory, source trust,
  faction learning, v18 save/load и детерминированное продолжение.

Проверки этого этапа:

- `dotnet build -v minimal` — **0 warnings / 0 errors**.
- `--self-test shouts` — **PASS**: snapshot v18, `made=130`, `heard=1140`,
  `learning=140`, `reputation_records=633`, `memory_records=498`, save/load PASS.
- `--self-test roundtrip` — **PASS**: `ticks=3200+600`, `pop=288`,
  `chronicle=48`, `settlements=286`, `aggregated=94`, `diffs=0`.
- `--self-test v10` — **PASS**: legacy v10/v11 continued for 700 ticks.
- `--self-test interventions` — **PASS**.
- `--test-camera-layout` — **20/20 PASS**.

Ограничение: это ещё не полноценная культура или язык — агенты пока не
формируют устойчивые нормы, мемы и социальные роли. Следующий разумный пакет:
фракционные нормы и наблюдаемые коллективные стратегии, но только после
ручного playtest Shout v3 и анализа JSONL.

## Следующие направления (НЕ НАЧИНАТЬ без команды)

- UI/UX polish и ручной playtest Shout-сигналов.
- Баланс и разнообразие коммуникации по данным JSONL.
- Следом: ручной playtest Shout v3, затем фракционные нормы и более глубокая культура.
- Любые микроптимизации `Step()` или balance-fix поселений — отдельная фаза.

### UI Foundation Fix — 2026-08-30

Исправлен первый слой проблем, найденных при ручном тесте GUI и анализе JSONL:

- `F3` теперь рисуется относительно `WorldViewport`, а не от угла всего окна;
  overlay имеет динамическую высоту, ограниченные строки и двухколоночный режим,
  поэтому не перекрывает TopBar, левую панель и First Cycle Guide.
- Inspector получил bounded-вывод: длинные строки не выходят за правую панель,
  заголовок показывает диапазон строк, прокрутка выполняется колесом над панелью.
- Chronicle группирует повторяющиеся события по `(tick, type, faction)` и показывает
  суффикс `(+N similar)`, сохраняя переход к representative event по клику.
- LOD в debug overlay подписан как `alive / active / dormant / proxy coverage`,
  чтобы aggregated proxy не выглядел как лишняя популяция.
- `AnalyticsLogger` раз в секунду пишет `render_metrics`: FPS, world/UI draw ms,
  окно, UI scale и viewport. `AnalyticsReport` выводит min FPS и максимальное
  время world/UI draw без записи каждого кадра.

Проверки после патча:

- `dotnet build -v minimal` — **0 warnings / 0 errors**.
- `--test-camera-layout` — **20/20 PASS**.
- `--self-test interventions` — **PASS**.
- `--self-test shouts` — **PASS**: deterministic save/load и bounded memory.
- `--self-test roundtrip` и `--self-test v10` ранее прошли на том же UI-safe
  состоянии; после UI-only изменений simulation-код не менялся.

Ограничение: существующий ручной лог был создан до добавления `render_metrics`,
поэтому эти поля появятся только в следующем GUI-запуске. Следующая ручная проверка:
запустить игру на 125%, открыть F3, выбрать агента, проверить wheel-scroll инспектора,
группировку Chronicle и запись `render_metrics` в новом JSONL.
