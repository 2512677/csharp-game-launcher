# Game Launcher

Внутренний WPF-лаунчер для установки, обновления и запуска нескольких Windows-игр. Проект работает на .NET 8.

## Добавление игры

Все игры находятся в `GameLauncher/launcher-settings.json`. Чтобы добавить игру, скопируйте объект внутри массива `games` и измените его параметры:

```json
{
  "launcherName": "Game Launcher",
  "games": [
    {
      "id": "my-game",
      "name": "My Game",
      "description": "Внутренняя тестовая сборка",
      "gameDirectory": "Games/MyGame",
      "gameExecutable": "My Game.exe",
      "versionFile": "MyGame.version.txt",
      "coverImage": "images/my-game-cover.png",
      "backgroundImage": "images/my-game-background.jpg",
      "versionUrl": "ссылка на Version.txt",
      "packageUrl": "ссылка на Build.zip"
    }
  ]
}
```

Параметры:

- `id` — уникальный короткий идентификатор игры;
- `name` — название на карточке и в нижней панели;
- `description` — короткое описание сборки;
- `gameDirectory` — отдельная папка установки игры;
- `gameExecutable` — путь к `.exe` внутри игры;
- `versionFile` — уникальный локальный файл установленной версии;
- `coverImage` — обложка карточки;
- `backgroundImage` — фон выбранной игры;
- `versionUrl` — ссылка на текстовый файл с номером вида `1.2.3`;
- `packageUrl` — ссылка на ZIP-архив игры.

Для разных игр обязательно задавайте разные `id`, `gameDirectory` и `versionFile`. Изображения можно складывать в папку `GameLauncher/images`; PNG, JPG и JPEG автоматически попадают в сборку. Обычные ссылки Google Drive преобразуются в прямые автоматически.

## Работа библиотеки

После выбора карточки лаунчер проверяет только версию игры. Архив не загружается автоматически. Кнопка показывает нужное действие:

- `Установить` — игра отсутствует;
- `Обновить` — на сервере есть новая версия;
- `Играть` — установлена актуальная версия;
- `Повторить` — проверка завершилась ошибкой.

Одновременно выполняется только одна загрузка. Каждая игра обновляется независимо. При ошибке обновления старая рабочая версия восстанавливается.

## Сборка

```powershell
dotnet build GameLauncher.sln --configuration Release
```

Для публикации автономного Windows-приложения:

```powershell
dotnet publish GameLauncher/GameLauncher.csproj --configuration Release --runtime win-x64 --self-contained true
```
