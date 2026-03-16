# GodSpeed OTD Plugin — Инструкция по установке

## Структура файлов

```
GodSpeedOTD/
 ├── GodSpeedFilter.cs       ← весь код плагина
 ├── GodSpeedOTD.csproj      ← проект для сборки
 └── metadata.json           ← описание плагина для OTD
```

---

## ШАГ 1 — Установи .NET 8 SDK

Скачай с https://dotnet.microsoft.com/download/dotnet/8.0

Проверь в терминале:
```
dotnet --version
```
Должно быть `8.0.x`

---

## ШАГ 2 — Скачай OpenTabletDriver.Plugin.dll

1. Зайди на https://github.com/OpenTabletDriver/OpenTabletDriver/releases
2. Скачай последний релиз (например `OpenTabletDriver.win-x64.zip`)
3. Распакуй архив
4. Найди файл `OpenTabletDriver.Plugin.dll`
5. Скопируй его в папку `GodSpeedOTD/` рядом с `.csproj`

После этого структура:
```
GodSpeedOTD/
 ├── GodSpeedFilter.cs
 ├── GodSpeedOTD.csproj
 ├── metadata.json
 └── OpenTabletDriver.Plugin.dll   ← только что добавили
```

---

## ШАГ 3 — Скомпилируй плагин

Открой командную строку (Win+R → `cmd`) или PowerShell:

```cmd
cd C:\путь\к\GodSpeedOTD
dotnet build -c Release
```

Успешная сборка выглядит так:
```
Build succeeded.
  GodSpeedOTD -> ...\bin\Release\net8.0\GodSpeedOTD.dll
```

Готовый файл будет здесь:
```
GodSpeedOTD\bin\Release\net8.0\GodSpeedOTD.dll
```

---

## ШАГ 4 — Установи плагин в OpenTabletDriver

Создай папку плагина:
```
%localappdata%\OpenTabletDriver\Plugins\GodSpeedOTD\
```

Скопируй туда **два файла**:
```
GodSpeedOTD.dll      ← из bin\Release\net8.0\
metadata.json        ← из корня проекта
```

Итоговая структура в папке OTD:
```
%localappdata%\OpenTabletDriver\Plugins\
  └── GodSpeedOTD\
       ├── GodSpeedOTD.dll
       └── metadata.json
```

---

## ШАГ 5 — Включи фильтр в OTD

1. Запусти **OpenTabletDriver**
2. Перейди в вкладку **Filters**
3. Нажми **+** (Add Filter)
4. В списке найди и выбери `GodSpeed Filter`
5. Нажми **Apply**

---

## ШАГ 6 — Настрой параметры

В UI OTD у фильтра появятся настройки:

| Параметр       | По умолч. | Описание |
|----------------|-----------|----------|
| Mode           | EMA       | Алгоритм: EMA / Kalman / Ring / Hybrid |
| Smooth Ms      | 4         | Задержка сглаживания в мс (0 = выкл) |
| Prediction Ms  | 0         | Предикция вперёд в мс (0 = выкл) |
| Deadzone       | 0         | Мёртвая зона (подавляет дрожание пера) |
| Aggression     | 3         | Скорость перехода slow→fast (1–10) |
| Pro Mode       | false     | vFactor² кривая для быстрых прыжков |
| Kalman Q       | 2         | Шум процесса (Kalman/Hybrid) |
| Kalman R       | 4         | Шум измерения (Kalman/Hybrid) |
| Ring Size      | 8         | Размер кольцевого буфера (Ring) |

### Рекомендованные пресеты для osu:

**Минимальная задержка:**
- Mode: EMA, Smooth: 2, Pred: 0, Deadzone: 0, Aggression: 7

**Баланс (аналог Hawku):**
- Mode: EMA, Smooth: 4, Pred: 2, Deadzone: 0, Aggression: 3

**Максимальное сглаживание:**
- Mode: Kalman, Q: 1, R: 8, Pred: 0, Deadzone: 0

**Hybrid (лучший для стримов):**
- Mode: Hybrid, Smooth: 3, Pred: 2, Q: 2, R: 4, Aggression: 4

---

## ШАГ 7 — Если плагин не появился в списке

Проверь по очереди:

❌ **dll не в той папке** — убедись что путь точно:
`%localappdata%\OpenTabletDriver\Plugins\GodSpeedOTD\GodSpeedOTD.dll`

❌ **Не тот .NET** — пересобери под net8.0 (проверь в .csproj)

❌ **Ошибка компиляции** — запусти `dotnet build` и прочитай ошибки

❌ **OTD не перезапущен** — закрой и открой OTD заново

❌ **Неправильная версия Plugin.dll** — убедись что взял из того же релиза OTD что установлен

---

## Архитектура (что происходит внутри OTD)

```
Планшет (HID)
      ↓
  Report Parser
      ↓
  [ GodSpeed Filter ]   ← твой код здесь
      ↓
  Output Mode (Absolute)
      ↓
  Windows Cursor
```

Плагин получает `Vector2(x, y)` координаты, обрабатывает их через
выбранный алгоритм (EMA/Kalman/Ring/Hybrid) и возвращает
отфильтрованные координаты. HID, SendInput, Boost — всё делает OTD.
