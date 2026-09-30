# BASELINE 0.4.2 — проверенная simulation baseline

> Исторический отчёт второй модели до интеграционного ревью. Основной агент
> воспроизвёл две пропущенные ошибки резервов и исправил их; новые результаты
> и поправки к выводам этого отчёта: [ROOT_REVIEW_0_4_2.md](ROOT_REVIEW_0_4_2.md).
> Числа ниже относятся к состоянию до этих исправлений.

Дата проверки: **2026-09-30**. Исполнитель: второй агент (ревью и валидация
simulation diff). Интеграция, build stamp и release-документы — главный агент.

Это отчёт о проверке, а не объявление релиза. 0.4.2 не выпущена; перечисленные
ниже непроверенные gate остаются открытыми.

---

## 1. Состояние репозитория

| Параметр | Значение |
|---|---|
| HEAD | `8994c41` — `feat: add First Cycle performance and impact telemetry` |
| Ветка | `main` |
| Рабочее дерево | dirty, ничего не staged, commit/tag/push не выполнялись |
| SDK | `dotnet --version` = **10.0.401** |
| Конфигурация сборки | `-c Release`, `net10.0`, MonoGame DesktopGL 3.8.5.1 |
| Product version | 0.4.1-prealpha |
| `WorldSnapshot.CurrentVersion` | **24** |
| `DeterminismBlockVersion` | 11 |
| Analytics schema | **5** |
| Дата | 2026-09-30 |

Ничего не коммитилось и не публиковалось. `saves/` не трогалась. Новых
зависимостей, потоков и игровых систем не добавлялось.

## 2. Исходный список изменённых файлов (до моих правок)

Снят `git diff --stat` в начале работы. Изменённые **tracked**-файлы на тот
момент (14 файлов, +603 / −913):

```
 CHANGELOG.md                             |   12 +
 CHECKPOINT.md                            |   69 ++-
 Game1.cs                                 |    2 +-
 Hollowbound.csproj                       |   25 +
 Program.cs                               |   10 +-
 README.md                                |   31 +-
 ROADMAP.md                               |  894 +++++++-------------------------
 Simulation/AgentState.cs                 |    1 +
 Simulation/ColonyEcology.cs              |  102 ++++
 Simulation/EmergentSimulationWorld.cs    |  128 +++++++----
 Simulation/FirstCycleUXState.cs          |    3 +
 Simulation/Map.cs                        |  118 +++---
 Simulation/ResourceSpatialIndex.cs       |   21 +-
 Simulation/SimulationRegressionRunner.cs |  100 ++-
```

Untracked на тот момент: `AGENTS.md`, `BuildInfo.cs`, `RELEASE_PLAN.md`,
`TECH_STACK.md`, `VISION.md`, `docs/`, `saves/`.

Разделение владения соблюдено. `BuildInfo.cs`, `Program.cs`, `Game1.cs`,
`Hollowbound.csproj`, `README.md`, `CHECKPOINT.md`, `CHANGELOG.md` и release-документы
мной не редактировались; они принадлежат главному агенту.

### Существо исходного simulation diff

Две связанные темы:

1. **Освобождение hot paths от аллокаций** — `Map.cs` (LRU-кэш маршрутов на
   `LinkedList` вместо неограниченной очереди; caller-owned `TryFindPath` /
   `TryGetPathFromDistanceGrid`), `ResourceSpatialIndex.cs` (переиспользуемый
   буфер результатов), `EmergentSimulationWorld.cs` (три scratch-буфера и
   `TryAssignPath` вместо `FindPath` + проверка длины на каждом вызывающем сайте).
2. **Новое действие агента `AgentAction.GoingToStoredFood` = 13** — голодный агент
   может пройти по проходимому маршруту к колониальному запасу вместо голода,
   когда ближайшая куча вне радиуса немедленного питания. BFS в
   `ColonyEcology.TrySetStoredFoodTarget` с generation-stamp буферами и
   `_storedFoodReservations`; реконструкция резервов в `FromSnapshot`.

## 3. Рассмотренный scope ревью

Читал и проверял:

- `Simulation/EmergentSimulationWorld.cs` — конвейер тика (24 фазы, порядок и
  периодичность), пул активных агентов, LOD-классификация, `UpdateAgentState`
  / `MoveAgent` / `ResolveAction`, все 34 места присваивания `agent.Action`,
  `TryConsumeStoredFood`, `SetFoodTarget` / `ReleaseFoodReservation`,
  `FindNearestFood`, `RegrowFood` / `AddFoodNode`, `FromSnapshot`,
  проверка связности проходов;
- `Simulation/ColonyEcology.cs` — `ReachableStorage`, `TrySetStoredFoodTarget`,
  `CompleteStoredFoodTrip`, `SpendLocalBirthFood`, засуха и порча;
- `Simulation/Map.cs` — `IsWalkable`/`InBounds`, `PathFinder` целиком (A*,
  stamp-массивы, LRU-кэш, distance grid);
- `Simulation/ResourceSpatialIndex.cs` целиком;
- `Simulation/AgentState.cs`, `Simulation/FirstCycleUXState.cs`;
- `Simulation/SimulationRegressionRunner.cs` — сценарий `ecology`, покрытие
  `pathfinder` и `resource-index`.

Не в моём scope и не проверялось: GUI, рендер, звук, ввод, упаковка, магазины.

## 4. Проверка рисков и результат

| Риск | Где проверяется | Код | Regression | Итог |
|---|---|---|---|---|
| Еда через стены | `ReachableStorage` требует `_map.IsWalkable`; `TrySetStoredFoodTarget` проверяет `IsWalkable` на каждом шаге расширения BFS | `ColonyEcology.cs:90,155` | `ecology`: закрытый запас (`Feeding crossed a closed wall`), запас за непроходимым барьером в оценке засухи | **подтверждено** |
| Освобождение резерва при смерти | 4 пути смерти вызывают `ReleaseFoodReservation` до `Action = Dead` | `EmergentSimulationWorld.cs:1562,1594,1615,1664` | **было пусто → добавлено** (раздел 5) | **подтверждено** |
| Освобождение резерва при смене задачи | `ReleaseFoodReservation` ставит `HasFoodTarget = false`, поэтому повторный вызов безопасен; все выходы из `GoingToStoredFood` сначала освобождают | `EmergentSimulationWorld.cs:2726-2751` | `ecology` (маршрут, энергия, исчерпание пути) | **подтверждено** |
| Освобождение резерва при неудаче пути | `MoveAgent` очищает путь на непроходимой клетке; следующий тик `UpdateAgentState` видит пустой путь и освобождает резерв | `EmergentSimulationWorld.cs:2819,2744` | `ecology` | **подтверждено** |
| Утечка/пере-освобождение резерва | Единственные точки выдачи резерва — `TrySetStoredFoodTarget` и `FromSnapshot`; единственное место, меняющее `Action` без освобождения, — Rally-шут (`receiver.Action is Idle or Resting or Exploring`, `GoingToStoredFood` исключён) | `EmergentSimulationWorld.cs:145,975,2381` | `ecology`, `shouts`, `roundtrip` | **подтверждено, ошибок нет** |
| Save/load continuation | `FromSnapshot` восстанавливает `_storedFoodReservations` обходом агентов; `_storedFoodReservations` не сериализуется и целиком выводится из сохранённых агентов | `EmergentSimulationWorld.cs:969-977` | `continuation`, `roundtrip`, `ecology`, `interventions`, `passage`, `shouts` | **подтверждено** |
| Bounded path cache | `_pathCache` (dict) + `_cacheNodes` (dict) + `_cacheOrder` (LinkedList) удаляются синхронно; `InvalidatePathCache` чистит все три; `AddCacheEntry` вызывается только при промахе | `Map.cs:445-620` | `pathfinder`: `lru=bounded`, 0 B на 128 cache-hit | **подтверждено** |
| Caller-owned буферы | `TryFindPath`/`TryGetPathFromDistanceGrid` очищают буфер вызывающего; кэш хранит независимую копию | `Map.cs:472-600` | `pathfinder`: `caller_buffers=pass`, мутация буфера не портит кэш | **подтверждено** |
| Индекс при исчерпании/регенерации | Исчерпанные узлы остаются в индексе; `AddFoodNode` переиспользует существующий узел, новый — с `_resourceIndex.Add`; Bloom создаёт узел с `Add` | `ResourceSpatialIndex.cs:8`, `EmergentSimulationWorld.cs:3941-3954,4583` | `resource-index`: `regrowth=pass`, `incremental_add=pass` | **подтверждено** |
| Новый агент не теряется в LOD | `forceActive` включает любое действие кроме Idle/Resting/SearchingFood, плюс `Energy < 60` | `EmergentSimulationWorld.cs:1998-2007` | `roundtrip`, `soak`, `benchmark` (LOD 175/223/80) | **подтверждено** |

### Найденные ошибки

**Воспроизводимых или однозначно доказанных регрессий в simulation diff не
найдено.** Ни одно из перечисленных предположений не подтвердилось как дефект.
Код и regression cases согласованы; все пять названных в задании рисков имеют
покрытие и проходят.

Отмеченные наблюдения (не ошибки, ничего не менялось):

- `TryConsumeStoredFood` (`EmergentSimulationWorld.cs:2174`) выбирает ближайшую
  кучу по результату BFS и **не** учитывает `_storedFoodReservations`, тогда как
  `TrySetStoredFoodTarget` требует `amount > reserved`. Агент может съесть единицу
  из кучи, полностью зарезервированной идущими к ней агентами. Это асимметрия
  правил, а не нарушение инварианта; исправление изменило бы питание и требует
  отдельного продуктового решения.
- `ReachableStorage` аллоцирует `HashSet`/`Queue`/`List` и вызывается для
  каждого голодного агента на каждом тике (`EmergentSimulationWorld.cs:1660`),
  рядом с намеренно allocation-free BFS в `TrySetStoredFoodTarget`. Это
  оптимизация, а не ошибка; в эту фазу не входит.
- Поле `Action` в `AgentSnapshot` — `byte` (`WorldSnapshot.cs:384`), поэтому в
  новом regression-фикстуре значение приводится явно. Замечание к fixture, не к
  runtime.

## 5. Изменения, сделанные мной

Один файл: **`Simulation/SimulationRegressionRunner.cs`** (+136 / −3).

Никаких изменений simulation-кода не потребовалось, поэтому исправления
поведения нет — добавлено только покрытие одного из рисков, названных в задании
и не имевшего regression.

**Закрытый пробел: освобождение колониального резерва при смерти путешествующего
агента.** Код корректен (все четыре пути смерти вызывают
`ReleaseFoodReservation`), но ни один сценарий этого не проверял.

Добавлено в сценарий `ecology`: детерминированная fixture из 2 агентов и одного
запаса в 40 единиц на расстоянии 50 клеток; агент направляется к нему через
`TrySetStoredFoodTarget`, затем погибает от голода на следующем тике. Проверяется,
что резерв ровно уменьшается на единицу, агент действительно умер, а
`FromSnapshot` пересоздаёт ту же картину резервов. Второй агентfixture намеренно
держится в покое с высокой энергией, чтобы не влиять на счётчик. Сценарий теперь
печатает `travel_death_release=released`.

Тест не вакуумный: и установка резерва, и его освобождение проверяются явными
assertions с числами.

Мои изменения в `Simulation/`:

```
 Simulation/SimulationRegressionRunner.cs | 139 ++++++++++++++++++++++++++++++-
```

Все прочие файлы `Simulation/` (`AgentState.cs`, `ColonyEcology.cs`,
`EmergentSimulationWorld.cs`, `FirstCycleUXState.cs`, `Map.cs`,
`ResourceSpatialIndex.cs`) оставлены в состоянии исходного diff без изменений.

## 6. Выполненные команды и результаты

Все команды выполнялись в Release с `--no-build` после явной сборки. Exit code
проверялся после каждой команды.

### 6.1 Сборка

| # | Команда | Exit | Результат |
|---|---|---|---|
| 1 | `dotnet build Hollowbound.sln -c Release -v minimal` | **0** | `Сборка успешно завершена. Предупреждений: 0 Ошибок: 0` |
| 2 | `dotnet build Hollowbound.sln -c Release -v minimal` (после правки теста) | **0** | 0 / 0 |

### 6.2 Полный self-test на окончательном simulation-коде

```
dotnet run --project Hollowbound.csproj -c Release --no-build -- --self-test all
```

**Exit 0 — `Result: PASS (15/15 scenarios, 268,0s)`**

```
[PASS] roundtrip    seed=20260826 ticks=3200+600 pop=358 chronicle=48 (+7 new) settlements=20 aggregated=86 diffs=0
[PASS] continuation seed=987654 pre=2400 post=1200 pop=332 fingerprints_match=true rng=444915636
[PASS] v10          v10: pop=2 lod_active=2 continued=700 | v11: pop=2 lod_active=2 continued=700
[PASS] chronicle    seed=4451 evicted=60 once_restored=yes generations=[5,10,15] refires=0 chronicle=48
[PASS] soak         ticks=12000 tps=65 pop=414 lod=274/140/196 settlements=23 walls=1149 chronicle=48 inv_errors=0
[PASS] responsiveness frames_per_combo=480 combos=15 stalls=0 backlog_bounded=yes invalid_speeds=inert clear_backlog=ok
[PASS] interventions commands=3 pending_save_load=pass active_save_load=pass log_entries=3 score=42,7
[PASS] shouts       made=96 heard=449 types=food:45,danger:0,rally:51 learning=243 (236+/7-) reputation_records=423 memory_records=395 visible=2 save_load=pass
[PASS] settlements  stable_ids=7 repeated_updates=20 population_assigned=126
[PASS] passage      queued_save_load=pass reserved_cost=2 duplicate_rejected=yes player_crossing=1 impact_saved=pass
[PASS] ecology      nutrition=conserved travel_death_release=released contextual_risk=9,0 %>7,0 % drought_food_lost=12 profiler_samples=15
[PASS] profiler     cadence=17 phases=2,5,10,25,50,200 coverage_samples=200 rolling_samples=256 record_alloc=0
[PASS] pathfinder   open_distance=29 obstacle_distance=34 cached_hit_allocated_bytes=0 lru=bounded caller_buffers=pass corner_cut=prevented barrier=blocked
[PASS] resource-index expanding_radius=pass stable_order=pass reservations=pass regrowth=pass incremental_add=pass reusable_buffer_allocated_bytes=0 queries=69
[PASS] analytics    worlds=2 segments=3 segment_summary=isolated causal_fields=present disabled_writer=drained
```

Первый полный прогон (до моей правки теста) дал тот же результат: **15/15,
exit 0, 187,4s**. Оба полных прогона относятся к одному и тому же
simulation-коду; различается только test-файл.

Замечания по качеству прогона, не по коду:

- `soak` дал `tps=126` в первом прогоне и `tps=65` во втором при тех же
  инвариантах (`inv_errors=0`) — разброс нагрузки машины.
- `responsiveness` сообщил 8 perf-warning в первом прогоне и 13 во втором, оба
  раза `stalls=0` и `backlog_bounded=yes`. Предупреждения — кадры выше
  интерактивного бюджета 12 мс при пороге stall 100 мс.
- Сценарий `multi-seed` **не входит** в `all` и в этой фазе не запускался.

### 6.3 Camera / layout

```
dotnet run --project Hollowbound.csproj -c Release --no-build -- --test-camera-layout
```

**Exit 0 — `All tests passed!`, 23/23 `[PASS]`**

Все 23 проверки: round trip world↔screen, углы экрана, отбрасывание letterbox,
zoom под курсором, origin viewport, resize камеры, clamp, FitWorld, visible bounds,
640×360, clamp минимального окна, resize ниже минимума, 1280×720/1920×1080,
3440×1440, UI-scale layout, отказ клика вне viewport, UISettings (valid/invalid/
missing JSON), onboarding, объяснение решения агента, минимальные ширины панелей,
4 UI scale, изоляция ввода, render-профили.

Замечание для CI (уже отмечено в `RELEASE_PLAN.md`): этот путь сообщает об
ошибке исключением и **не выставляет exit code**, поэтому CI обязан отдельно
проверять итоговую строку `All tests passed!`.

## 7. Baseline симуляции (headless, Release)

Команда: `dotnet run --project Hollowbound.csproj -c Release --no-build -- --benchmark 12345 4000 500 300`

Условия: seed 12345, 4000 тиков, запрошенная скорость x500, начальная
population 300, LOD включён, budget кадра 200 мс (throughput-режим), Release,
одна машина, без параллельных нагрузок. Прогрев — один запуск, не учитывается.

| Прогон | Wall time | TPS | Effective speed | Final pop | Births | Deaths | Step p50/p95/max |
|---|---|---|---|---|---|---|---|
| прогрев | 36,89 s | 108,4 | x10,84 | 398 | 98 | 0 | 8/32/31,45 ms |
| **1** | 49,77 s | **80,4** | x8,04 | 398 | 98 | 0 | 16/64/111,57 ms |
| **2** | 37,23 s | **107,4** | x10,74 | 398 | 98 | 0 | 16/32/47,49 ms |
| **3** | 44,00 s | **90,9** | x9,09 | 398 | 98 | 0 | 16/32/44,69 ms |

Контрольный прогон после финальной сборки: 28,84 s, **138,7 TPS**, x11,20,
pop 398, births 98, deaths 0.

Сводка по трём измеренным прогонам: **TPS 80,4 / 107,4 / 90,9**; среднее
≈ **92,9**, медиана **90,9**; wall time **37,2–49,8 с**. Разброс TPS между
прогонами — **×1,34**, поэтому заявлять точное значение нельзя; диапазон
**80–140 TPS** на этом профиле — честная оценка.

Одинаковые во всех прогонах счётчики симуляции (это же доказательство
детерминизма на данном профиле):

```
Queries: agents=53486, food=53250, walls=27990, paths=41306, cache_hits=20698
LOD agents: active=175, dormant=223, aggregated=80
Food consumed=3779, food gathered=5708, walls built=785, walls removed=147,
knowledge shared=58699, decisions=102889, learning updates=14097 (+12130/-1967)
```

`paths=41306` и `cache_hits=20698` совпадают с записанными в `CHECKPOINT.md` для
предыдущего локального пакета, и итоговая `population=398` / `deaths=0` тоже.
Это означает, что текущий diff **не изменил исход симуляции** относительно
состояния, которое уже было записано в чекпойнте. Ускорение не заявляется:
сопоставимой исходной серии на той же машине и в той же конфигурации нет, а
записанные ранее 31,5 TPS относились к другой конфигурации сборки.

Профиль горячего участка (p95 фаз, из прогона 2): agent loop, включая
per-agent pathfinding — **32 мс**; distance-grid prep — **0,1 мс**; прочее,
включая LOD — **0,5 мс**. Узкое место — agent loop, а не distance grids.

### Чего эти цифры не доказывают

- **GUI FPS не измерен.** GUI в этой задаче не запускался, как и требовалось.
  Все числа выше — headless throughput.
- Запрошенный x500 фактически даёт x8–x11. Это ожидаемое и честное ограничение,
  не дефект; `responsiveness` фиксирует `CATCHING UP` вместо подтормаживания.
- Время на этой машине не изолировано: тесты и benchmark шли последовательно,
  но фоновые процессы ОС не контролировались.

## 8. Оставшиеся gate

Непроверенными считаются до фактического выполнения:

| Gate | Статус |
|---|---|
| Полный `--self-test all` на **packaged** EXE, а не `dotnet run` | не проверен |
| `--self-test multi-seed` (100 seed) | **не запускался** в этой фазе |
| GUI FPS / frame time при population ~300, 1280×720, x1 | не проверен (GUI не запускался) |
| Ручная сессия 30–60 минут: старт, вмешательства, кризис, save/возврат | не проверен |
| Запуск на машине без SDK, распаковка ZIP, шрифт/кириллица | не проверен |
| Обновление с legacy-сейвов, backup/recovery, отказ от неизвестной версии | не проверен |
| Фоновая 2-часовая сессия и отдельный 8-часовой soak | не проверен |
| Звук, ввод, resize/fullscreen, сворачивание | не проверен |
| Билд 5–10 тестерам и запись их наблюдений | не проверен |
| Спецификация первого рецепта и склада для 0.5 | не начата (вне этой фазы) |
| Версия 0.4.2-alpha.1 как релизный тег/артефакт | **не выпущена** |

## 9. Риски, оставшиеся после ревью

1. **God-класс `EmergentSimulationWorld`** — 4815 строк, тик-шедулер, FSM агента,
   обучение, экология, LOD, вмешательства и сериализация в одном классе. Не
   исправлялось: выделение подсистем — отдельная задача, а не часть baseline.
2. **`_storedFoodReservations` не сериализуется** и выводится обходом агентов.
   Инвариант «резерв ⇔ живой агент в `GoingToStoredFood` с `HasFoodTarget` и
   существующей кучей» сейчас выполняется, но ничто в типах не делает его
   обязательным для будущих изменений. Новый способ сменить `Action` без
   `ReleaseFoodReservation` (например, если новый вид сигнала начнёт
   перенаправлять агентов) сразу создаст утечку резервов. Это главный риск
   расширения 0.5.
3. **Разброс производительности ×1,34** между соседними прогонами на
   идентичном входе. Любое сравнение версий на этой машине требует нескольких
   прогонов; одиночный TPS не является характеристикой сборки.
4. **`responsiveness` показывает 85–100 % catching-up** при population ~300 и
   x250–x500. Это заявленное ограничение, а не сбой, но оно означает, что
   фактическая скорость в GUI будет ниже запрошенной.
5. Мёртвый код вне моего scope и не трогавшийся: `LegacySimulationWorld`
   (~424 строки, `[Obsolete]`, без ссылок), `AgentLOD.Aggregated` (никогда не
   присваивается), `WorldChunks.UpdateActivity` (нет вызывающих, из-за чего
   `ActivityLevel` всегда 0 и оба distance-grid фактически статичны).

## 10. Резюме

Simulation baseline проверена на текущем simulation diff. Сборка чистая
(0/0), `--self-test all` — 15/15 PASS с exit 0, camera/layout — 23/23 PASS с
exit 0, headless baseline воспроизведён (TPS 80,4 / 107,4 / 90,9 при полном
совпадении всех счётчиков симуляции). Регрессий в diff не найдено. Внесено одно
изменение: предметный regression на освобождение колониального резерва при смерти
агента — единственный из пяти названных рисков, не имевший покрытия.

0.4.2 не готова к выпуску: GUI, ручная сессия, packaged EXE, чистая машина,
`multi-seed`, фоновая сессия и feedback тестеров остаются невыполненными.
