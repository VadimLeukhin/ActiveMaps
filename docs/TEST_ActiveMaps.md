# Active Maps — чеклист теста (мини-сборка)

Мини-набор модов: Prepatcher, Harmony, Core, `crystallize.activemaps`, `khatum.outpostdemo`.  
Конфиг: `ModsConfig.ActiveMapsTest.xml` (сейчас он же в `ModsConfig.xml` и в AppData).  
Полный пак сохранён: `ModsConfig.KhatumFull.xml` + `Backups\modsconfig_activemaps_test_*`.

Восстановить полный список:
```powershell
Copy-Item F:\RimWorldModpacks\KhatumStable\ModsConfig.KhatumFull.xml F:\RimWorldModpacks\KhatumStable\ModsConfig.xml -Force
Copy-Item F:\RimWorldModpacks\KhatumStable\ModsConfig.KhatumFull.xml "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModsConfig.xml" -Force
```

Старт: **новая игра**, только Core, Dev mode ON. 3–4 колониста, сформировать караван, уехать на свободный тайл.

---

## A. Mode1 — stash / караван

1. На караване есть гизмо **Сформировать аванпост** / Form outpost.
2. После формы: world object аванпоста, караван исчез, inspect: Mode=Stash, occupants > 0.
3. **Запрет Enter**: float-menu «войти» на аванпост — отказ (`Crystallize_AM_NoPlayerEnter`).
4. **Disband**: гизмо роспуска → караван снова, аванпост уничтожен, люди и инвентарь на месте.
5. Повторно Form на том же/другом свободном тайле (тайл с объектом → `TileOccupied`).

## B. Demo ledger + Mode2 fire

6. DEV/гизмо **Seed ledger** (demo): в ledger миномёты + HE shells, PrimaryAmmo задан.
7. **Залп** (Fire barrage): выбрать тайл на мировой карте → появляется travelling strike / доставка; cooldown/ammo списываются (inspect cooldown).
8. Повторный залп без ammo / на КД — отказ.
9. Залп **во время активного рейда (HasMap)** — отказ (`CannotFireDuringRaid`).

## C. Рейд → Mode3 (Promote)

10. DEV: **Simulate raid letter** на аванпосте.
11. **Accept**: карта грузится, Jump на карту, Mode=Map, RaidActive, колонисты на карте.
12. Demo **sketch**: кольцо мешков + миномёт(ы) около центра (и куча снарядов, если ledger > 0).
13. **Decline** письма (отдельный прогон): без карты, абстрактные потери (occupants/ledger/items уменьшились), аванпост остаётся Mode1.

## D. Бой / Demote mid-combat

14. Пока есть **живые активные враги** и **живые свои** → **End raid & fold** отклоняется.
15. Убить/убрать всех врагов → Demote проходит: люди в stash, карта выгружена, Mode=Stash.
16. На карте перед Demote: бросить оружие колониста (Forbidden автоматом) + лут на земле → после fold всё в stash (Forbidden не мешает scoop).
17. Ранить колониста до Downed + кровотечение → Demote → Disband: **ходит** в караване, Bleed остановлен / BloodLoss низкий (стабилизация C).

## E. Wipe (все свои мертвы)

18. На Mode3 убить всех колонистов игрока, врагов оставить живыми.
19. **End raid & fold** должен пройти (abandon): сообщение wipe, карта снята, трупы/лут в stash, Mode=Stash, occupants=0.
20. (Ожидаемо хрупко) Disband с 0 пешек — сейчас отказ; зафиксировать, нужен ли отдельный «забрать stash / снести пустой аванпост».

## F. Повторный цикл

21. После успешного Demote с живыми: снова letter → Accept → sketch снова, люди на карте из stash.
22. Save/Load на Mode1 со stash и на Mode3 mid-raid — без NRE, флаги mode/raidActive/occupants на месте.

## G. Вне скоупа этой мини-сборки (потом на полном паке)

- Soft bridge Rimatomics Fire Mission (нужен `dubwise.rimatomics`).
- Конфликты с VOE / VehicleFramework / Map Preview.
- RU-строки всех keyed на полном языке.

---

Лог при старте: `[KhatumOutpostDemo] Sketch provider registered.` и отсутствие красных Harmony errors по `crystallize.activemaps` / `outpostdemo`.
