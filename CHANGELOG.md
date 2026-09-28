# Changelog

Этот файл описывает текущее **незакоммиченное** рабочее состояние. Это не Git tag
и не обещание публичного релиза.

## 0.4.0-prealpha — Living Colonies (2026-09-10, uncommitted)

### Gameplay

- Replaced the global birth timer with staggered colony-local opportunities.
  Parents need maturity, energy, nearby space and six units of reachable stored
  food, with a small reserve left for existing residents.
- Gathering, storing and resting no longer generate free energy. Residents eat
  carried food or reachable nearby stock; walls block local distribution.
- Builders retain an energy reserve; critically hungry residents no longer choose
  rest as a source of nonexistent nutrition.
- Added a forecast local source drought, map outline and recovery period.
  Bloom sources resist drought depletion; new natural sources avoid the affected area.
- Large individual stockpiles slowly spoil. New residents live about 36,000 ticks;
  existing saves retain previously assigned lifespans.
- Danger shouts require nearby, relevant danger rather than indefinitely repeating
  a distant failed trip. Corrected dormant old-age deaths counted as starvation.
- Removed double counting of stored food in crisis and faction food-pressure checks.

### Observability and persistence

- Added food consumption, birth expenditure, spoilage, drought loss and blocked
  birth counters to the analytics panel, JSONL telemetry and CLI log report.
- Snapshot v20 persists ecology phases and counters. Older saves receive a grace
  period before the first drought; their existing population and stock are not reset.
- New `--self-test ecology` exercises local births, nutrition conservation,
  inaccessible storage and exact continuation across warning/active drought saves.

### Boundaries

- Local feeding is an abstract distribution within six walkable steps, not a new
  hauling/job system. Droughts use one deterministic phase cycle, not a full event director.
- Balance and high-population performance still require playtesting. Stock alone
  does not guarantee access; colonies can decline. No multiplayer or new leaderboard.
- Seeds evolve differently from earlier builds. This patch is not committed or published.

### Verification

- Build: zero errors/warnings. Full self-test: 11/11; camera/layout: 21/21.
- Ecology round-trip and continuation matched exactly; 12,000-tick soak passed invariants.
- Responsiveness: no >100 ms stalls in 15 test combinations; eight >12 ms warnings remain.
- Seed 286830073, two founders, 40,000 ticks: 384 residents, peak 570,
  746 births, 364 deaths, 23,112 food consumed. This is balance evidence, not a growth guarantee.
- The same run lost no existing food directly to drought (sources were already depleted).
  Regrowth is suppressed, but crisis impact needs further tuning and measurement.
- Automated 1600×900 rendering checked; manual controls and perceived gameplay not re-tested.

## 0.3.0-prealpha — The Living Archive (2026-09-10, uncommitted)

Hollowbound becomes an observatory you can influence, not just a debug dashboard.
This is a local pre-alpha patch, not a published release or a scalability claim.

### New experience

- Rebuilt Russian-language interface: compact world summary, four intervention
  cards, scrollable agent details, colonies and analytics tabs, and a focused Chronicle.
- Dark teal world treatment, softer walls, luminous organisms and resources,
  carried-food markers, selected-agent paths and colony names.
- Clickable mini-map; world, food, energy and faction lenses; camera follow;
  `H` focuses a colony, `G` an event, `M` switches lenses.
- Pause menu with save/load, UI scaling, fullscreen, screenshots, logs and guided
  onboarding. Creating a new experiment first archives the current world; if
  archival fails, the current world stays open. New worlds start paused.
- Population history for the current viewing session and a visible score breakdown.
  These are observational charts, not a persistent leaderboard.
- **Passage** (`4`, 2 Resonance): open a single wall tile as a protected walkable
  passage. It uses the deterministic command queue, reserves its cost, rejects
  duplicates and survives save/load before execution.

### Fixes and reliability

- Fixed repeated settlement creation during population recount: existing live
  settlements are reused even while their cached population is being rebuilt.
- Settlement cohesion uses sorted coordinate sums instead of an all-pairs scan.
- Pointer picking rejects empty space outside the map instead of clamping to
  border tiles. Menu/panels remain separate from world input.
- Chronicle prioritises important events; diagnostics are bounded inside the
  world viewport. Text layout supports narrow windows and large UI scales.
- Disabled MonoGame's outer fixed-step catch-up; the simulation itself still uses
  deterministic fixed ticks. Autosave deferral during overload is capped at 120 seconds.
- Snapshot **v19** identifies the new Passage command. Older saves remain readable;
  older executables should not be used to read v19. Identical outcomes across
  different game versions are not promised.
- Added isolated `--playtest` and deterministic `--preview` modes for presentation QA.
  Playtest saves/settings are isolated from ordinary player saves; telemetry still logs.

### Verification and limits

- Build: 0 warnings, 0 errors. Camera/layout/picking: 21 checks passed.
- Full simulation run: 9/10 initially; the remaining roundtrip failure was an
  incorrect once-only assertion for repeatable migration waves. Replaced it with
  counter/tick checks and reran roundtrip: PASS, 0 state differences. The other
  nine scenarios passed, including continuation, soak, responsiveness, interventions,
  shouts, settlement stability and Passage pending-save/load.
- Soak: 12,000 ticks, 367 agents, 22 settlements, 0 invariant errors.
- Responsiveness: 15 combinations, no frames exceeding the test's 100 ms stall
  threshold; 11 slower-than-12-ms warnings remain. This is not a 60-FPS guarantee.
- Actual window and rendered screenshots inspected. Full manual mouse/keyboard,
  fullscreen and resize acceptance still needs an uninterrupted playtest.
- Benchmark `12345 2000 500 120`: 120 TPS today versus an earlier 346 TPS baseline.
  Runs were not under controlled identical machine load; performance regression
  is **not ruled out**. Do not advertise a speedup or huge population support.
- Very small windows use compact text; comfortable play is recommended at 1280×720
  or larger. UI animation/culling does not remove the simulation's per-tick cost.

## 0.2.0-prealpha — First Cycle: Player Agency

**Статус:** рабочий vertical slice.<br>
**Формат сохранения:** snapshot v14.<br>
**Базовый commit:** `c6de03f` на ветке `main`; поверх него есть незакоммиченные изменения.

Hollowbound впервые получает осмысленную роль игрока: он не управляет агентами
напрямую, а тратит редкий ресурс `Resonance`, меняя условия автономного мира.

### Добавлено

- Эксперимент `Resilient Settlement` с breakdown-score: Population, Food,
  Crisis, Settlement и Efficiency.
- Три детерминированные команды игрока:
  - **Bloom** — временная добавка еды к источнику;
  - **Beacon** — временная точка исследования;
  - **Insight Pulse** — локальная передача знаний агентам.
- Очередь команд: действие применяется на границе следующего simulation tick,
  а значит корректно участвует в deterministic replay.
- HUD-инструменты `1`/`2`/`3`, применение ЛКМ и обратная связь об операции.
- Лог вмешательств и отдельное Chronicle-событие `PlayerIntervention`.
- Состояние игрока, очередь, активные эффекты и score в сохранениях.
- Сценарий `--self-test interventions`: проверяет очередь, ресурс, invalid
  command, Chronicle, save/load до применения и во время активных эффектов.

### Исправлено в review-патче

- **Save/load активных эффектов действительно проверяется.** Ранее код теста
  печатал `save_load_ok=yes`, хотя соответствующий блок был отключён.
- **Bloom корректно завершается:** снимается только неиспользованный временный
  бонус, а не абсолютное старое количество еды. Временный источник также не
  остаётся в мире навсегда.
- Нельзя поставить несколько Bloom на один фактический источник через соседние
  клетки; очередь резервирует `Resonance`, поэтому пауза не позволяет набрать
  невыполнимые команды.
- Beacon разрешён только на проходимой клетке и выбирается агентами по
  стабильному правилу близости.
- Score приведён к шкале **0–100** и использует сохранённые счётчики кризисов,
  а не ограниченную 48-событийную витрину Chronicle.
- Строгое сравнение миров и fingerprints включают First Cycle state, поэтому
  дальнейшие регрессии очереди, эффектов или score не останутся незамеченными.
- PathFinder больше не отдаёт агенту тот же изменяемый список, который хранит
  в cache. Это исправляет расхождение мира после save/load.
- Порядок ячеек в wall spatial index нормализован, чтобы обход стен не зависел
  от того, был ли мир восстановлен из сохранения.

### Совместимость

- Приложение читает save formats **v10–v14**.
- v14 хранит два необходимых для честного score счётчика кризисов, а также
  точный остаток временного бонуса Bloom.
- Для v13 во время активного Bloom остаток бонуса восстанавливается
  консервативно из текущей еды: старый формат не содержит достаточно данных для
  идеальной ретроспективы.

### Проверено локально

- Отдельная сборка: **0 warnings, 0 errors**.
- `--self-test interventions`: **PASS** — команды, pending save/load, active
  save/load и 510 последующих тиков совпадают bit-exact.

### Известные ограничения

- Это foundation игрового цикла, а не готовая система лидербордов: формула
  score пока является локальным отчётом эксперимента, а не античит-рейтингом.
- Камера, zoom, viewport culling, onboarding и экран итогов ещё не сделаны.
- Полный многосценарный self-test и benchmark после review-патча не запускались
  повторно: они занимают заметное время и должны быть отдельным release gate.
