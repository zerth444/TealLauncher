Проверка раздела модов
====================

Из корня проекта:

```powershell
dotnet restore Tests/Launcher.Tests.csproj --configfile Tests/NuGet.Config
dotnet run --no-restore --project Tests/Launcher.Tests.csproj
```

Локальные проверки: создание и сохранение инстансов, совместимость Minecraft/загрузчика, контрольные суммы, защита путей, приоритет клиентских overrides, отмена и тайм-ауты, продолжение загрузки через Range, сборка крупных файлов из фрагментов, кэш, включение/выключение модов, ресурспаков и шейдеров.

Проверки с настоящими загрузками:

```powershell
dotnet run --no-restore --project Tests/Launcher.Tests.csproj -- --network --install --loaders
```

Проверяются Modrinth, зависимости Iris, Fabric/Forge/Quilt для Minecraft 1.20.1 и NeoForge для 1.20.2. Данные изолированы в `Tests/work` и исключены из Git.

```powershell
# Настоящие мод, ресурспак, шейдер и датапак, повторная установка без дубликатов:
dotnet run --no-restore -c Release --project Tests/Launcher.Tests.csproj -- --network --content
# Полная установка Sodium Plus, включая Minecraft, Java и загрузчик:
dotnet run --no-restore -c Release --project Tests/Launcher.Tests.csproj -- --sodiumplus
# Запуск клиента, проверка инициализации звука и графических ресурсов и закрытие игры:
dotnet run --no-restore -c Release --project Tests/Launcher.Tests.csproj -- --sodiumplus --launch-sodium
```

Флаг `--user-root` переключает проверку Sodium Plus на обычную папку `%APPDATA%/TealLauncher/minecraft`; без него используется изолированная папка тестов. Настоящие загрузки и первый запуск игры могут занять несколько минут. Перед обычной сборкой закройте проверяемое окно приложения, чтобы его EXE не был занят.

Проверки интерфейса и исправлений установки:

```powershell
dotnet run --no-restore -c Release --project Tests/Launcher.Tests.csproj -- --network --forge-clean
```

Локальный набор дополнительно проверяет сохранение никнейма при закрытии и повторном открытии окна, переключатели установленного содержимого в левой панели, сворачивание активного прогресса без отмены установки и закрытие завершённого уведомления. Сетевой набор проверяет наличие Fabric, Forge, NeoForge и Quilt в главном списке версий; `--forge-clean` устанавливает ещё не установленную версию Forge через публичный установочный механизм без рекламного callback.

```powershell
# Фильтры версий, настройки, русский язык первого запуска, резервные копии и страница проекта:
dotnet run --no-restore -c Release --project Tests/Launcher.Tests.csproj
# Обновление главной страницы и открытого каталога, описание проекта без установки:
dotnet run --no-restore -c Release --project Tests/Launcher.Tests.csproj -- --network --refresh
# Настоящая загрузка Beta b1.7.3, Alpha a1.2.6 и релиза 1.5.2:
dotnet run --no-restore -c Release --project Tests/Launcher.Tests.csproj -- --legacy
```
