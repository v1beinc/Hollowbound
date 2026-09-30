# Интеграционное ревью baseline 0.4.2

Дата: 2026-09-30. Основной агент проверяет отчёт второй модели
[BASELINE_0_4_2.md](BASELINE_0_4_2.md) и устраняет подтверждённые ошибки.
HEAD: `8994c41559e4e8b21bfd13105d82fb53ba447cdc`, рабочее дерево dirty.
Product version: 0.4.1-prealpha; snapshot v24; analytics v5; SDK 10.0.401.
0.4.2 не выпущена. Проверки ниже относятся к Release на этой машине.

## Что принято и что исправлено

Тест второй модели на освобождение колониального резерва при смерти принят:
fixture сначала проверяет выдачу одного резерва, затем смерть от голода,
уменьшение счётчика и восстановление после save/load. Это предметное покрытие.
Существующие изменения cache/index/buffers и пользовательские saves сохранены.

Вывод второго агента «ошибок в lifecycle резервов нет» не подтверждён
интеграционным ревью. Основной агент добавил в `ecology` две fixture и получил
ошибки **до** исправления соответствующих ветвей:

```text
[FAIL] ecology Stored-food feeding interrupted GoingToFood without releasing its source reservation
Result: FAIL (0/1 scenarios, 0,1s), exit 1

# После исправления первой ветви:
[FAIL] ecology Save/load dropped the pending reservation of a temporarily depleted colony reserve
Result: FAIL (0/1 scenarios, 0,1s), exit 1
```

1. `TryConsumeStoredFood` при слабом агенте отменял `GoingToFood` в пользу отдыха,
   оставляя `HasFoodTarget` и запись `ResourceNode.ReservedBy`. Исправление:
   вызвать `ReleaseFoodReservation` до очистки пути и смены Action.
2. Последнюю единицу колониального запаса может съесть сосед, пока к нему идёт
   другой агент. В исходном мире резерв путешественника сохраняется до его
   следующего обновления, но `FromSnapshot` отбрасывал его при отсутствии кучи.
   После пополнения той же клетки до обновления путешественника reload мог
   разрешить второму агенту зарезервировать ту же единицу. Исправление:
   восстанавливать резервы из живых агентов с `GoingToStoredFood`/`HasFoodTarget`,
   включая временно отсутствующие кучи. Следующее обновление штатно освобождает
   отменённую заявку; схема save не меняется.

Новые проверки устанавливают реальный source reservation, кормят агента через
штатную функцию, истощают склад через питание другого агента, восстанавливают
мир, пополняют склад через `StoringFood`, проверяют отказ конкуренту в обоих
мирах и освобождение заявки после отмены пути.

После обоих исправлений targeted `ecology`: **1/1 PASS, exit 0, 0,4 s**:

```text
nutrition=conserved travel_death_release=released food_cancel_release=released depleted_reserve_reload=preserved
```

## Уточнения исходного отчёта

- Три измеренных запуска второй модели: 80,4 / 107,4 / 90,9 TPS; среднее 92,9,
  медиана 90,9, диапазон 80,4–107,4. Контрольный запуск 138,7 TPS не входит в
  эту серию. Округление до «80–140» смешивает серии и не используется как baseline.
- Для 138,7 TPS формула в `Benchmark.Run` даёт effective speed x13,87;
  записанные в отчёте x11,20 противоречат этой формуле. Это арифметическая
  поправка, исходный stdout контрольного запуска не предоставлен.
- Равенство population/deaths/paths/cache hits не доказывает полного совпадения
  world state или будущего развития. Полное совпадение подтверждают отдельные
  roundtrip/continuation проверки на своих сценариях.
- Конфигурация исторического замера 31,5 TPS не подтверждена самим checkpoint.
  Нет основания утверждать сравнимое ускорение относительно него.
- `all` включает legacy fixtures v10/v11 и 12k-tick soak, но не multi-seed.
  Проверка реальных пользовательских legacy saves и 2h/8h фоновые сессии остаются
  отдельными gate.
- Camera/layout вызывает исключение при ошибке; `Program.cs` не перехватывает
  его. Утверждать, что этот путь всегда возвращает нулевой exit code, нельзя.
  Успех следует проверять по exit code и итоговой строке, как в RELEASE_PLAN.
- p50/p95 профайлера — верхние границы histogram buckets, а max — точный
  максимум sampled окна. Поэтому bucket p95=32 ms может быть выше max=31,45 ms;
  эти значения не означают точный percentile всех тиков или GUI FPS.

## Финальная проверка

| Проверка | Команда | Результат |
|---|---|---|
| Release build | `dotnet build Hollowbound.sln -c Release -v minimal` | 0 warnings / 0 errors, exit 0 |
| Targeted ecology | `dotnet bin/Release/net10.0/Hollowbound.dll --self-test ecology` | 1/1 PASS, 0,4 s, exit 0 |
| Полный regression | `dotnet bin/Release/net10.0/Hollowbound.dll --self-test all` | 15/15 PASS, 255,4 s, exit 0 |
| Camera/layout | `dotnet bin/Release/net10.0/Hollowbound.dll --test-camera-layout` | 23/23 PASS, `All tests passed!`, exit 0 |

`roundtrip`: diffs=0; `continuation`: fingerprints_match=true. Legacy v10/v11
fixtures продолжились 700 тиков. Soak: 12 000 ticks, pop 412, inv_errors=0.
До исправлений в отчёте второй модели soak завершался с pop 414: исправления
могут изменить траекторию прежнего seed; межверсионное совпадение не обещается.
Responsiveness: 15 combinations, 0 stalls >100 ms, backlog bounded, 2 perf
warnings >12 ms (максимум 22,6 ms). GUI в этих проверках не запускался.

Raw stdout полного набора: `%TEMP%\Hollowbound-review-20260930\self-test-all.log`.
Локальные машинные логи хранятся вне репозитория, в commit не включаются.

## Benchmark на исправленном ядре

Референсная команда, совпадающая с заданием второй модели:

```powershell
dotnet run --project Hollowbound.csproj -c Release --no-build -- --benchmark 12345 4000 500 300
```

Seed 12345, 4000 ticks, initial population 300, requested x500, LOD on,
FrameSimulationBudgetMilliseconds=200. Запуски последовательные на этой машине;
фоновые процессы ОС не изолированы. Прогрев и три измерения, все exit 0:

| Прогон | Wall time, s | TPS | Effective speed | Final pop / births / deaths |
|---|---|---|---|---|
| прогрев | 34,524 | 115,9 | x11,59 | 398 / 98 / 0 |
| 1 | 31,626 | 126,5 | x12,65 | 398 / 98 / 0 |
| 2 | 28,474 | 140,5 | x14,05 | 398 / 98 / 0 |
| 3 | 32,128 | 124,5 | x12,45 | 398 / 98 / 0 |

Измеренная серия: **126,5 / 140,5 / 124,5 TPS**, среднее **130,5**, медиана
**126,5**, диапазон **124,5–140,5**. Во всех запусках paths=41306,
cache_hits=20698, food_consumed=3779, decisions=102889. Эти агрегаты совпадают
с предыдущим отчётом на данном 4k-профиле; полного межверсионного совпадения
world state это не доказывает. p95 agent loop: histogram bucket **32 ms**;
distance-grid prep: **0,1 ms**; other: **0,5–1 ms**.

Raw stdout измерений: `%TEMP%\Hollowbound-review-20260930\benchmark-sdk-1.log`,
`benchmark-sdk-2.log`, `benchmark-sdk-3.log`.

### Отдельные измерения прямого запуска DLL

До референсной серии выполнен прогрев и три запуска через
`dotnet bin/Release/net10.0/Hollowbound.dll --benchmark 12345 4000 500 300`.
Измерения: **313,2 / 313,6 / 314,8 TPS**, 12,706–12,770 s, x31,32–x31,48;
агрегаты те же, p95 agent loop bucket 8 ms. Raw stdout: `benchmark-warmup.log`
и `benchmark-1.log`/`benchmark-2.log`/`benchmark-3.log` в том же TEMP-каталоге.

Наблюдается расхождение замеров при разных путях запуска, его причина не
изолирована. Серии не смешиваются; ускорение от исправлений не заявляется.
Baseline для следующего сравнения — референсная команда и её три измерения.
При будущем профилировании записывать точный launcher и условия; GUI FPS,
native packaged EXE и переносимость требуют собственных проверок.

## Открытые gate

GUI-профиль/ручная сессия; self-contained ZIP со шрифтом, лицензиями и manifest;
запуск без SDK; реальные legacy saves и recovery; multi-seed на исправленном
ядре; 2h/8h фон; 5–10 внешних тестеров. Это отдельные проверки. 0.4.2 не выпущена.

`git diff --check` прошёл. Пользовательские saves не изменялись; commit/push
и публикация не выполнялись.

## Подготовка версионного commit

По отдельной команде пользователя product metadata повышена до
**0.4.2-alpha.0** для кодового checkpoint. Simulation-код и save/analytics
contracts не менялись. Повторные проверки: Release build 0 warnings/errors,
`--version`/JSON assertions PASS и `ecology` 1/1 PASS, exit 0. Полный набор
15/15 выше выполнен до смены metadata на том же simulation-коде.
Пакет 0.4.2-alpha.1 остаётся будущим gate; push/tag/upload не запрашивались.
