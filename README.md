# Violet Soundpad

Violet Soundpad — звуковая панель для Windows. Она отправляет клипы в виртуальный микрофон, чтобы их могли слышать собеседники в Discord, игре или другом голосовом приложении.

## Установка и настройка

1. Запустите `VioletSoundpad-Setup-1.0.0.exe` и откройте Violet Soundpad из меню «Пуск». Для приложения нужна Windows 10 версии 2004 или новее либо Windows 11 с .NET Framework 4.8.
2. Установите [VB-CABLE](https://vb-audio.com/Cable/) по инструкции разработчика. В Violet Soundpad нажмите «Обновить устройства» и выберите **CABLE Input** в поле «Вход виртуального кабеля».
3. В настройках Discord, игры или программы записи выберите **CABLE Output** как микрофон. Для вывода звука в этой программе оставьте наушники.
4. В Violet Soundpad выберите физический микрофон и нажмите «Проверить микрофон». Говорите и смотрите на индикатор уровня. Чтобы вместе с клипами собеседники слышали ваш голос, включите «Передавать мой голос».
5. Выберите «Только в микрофон» или «В микрофон и наушники». Во втором режиме укажите наушники в самой панели. Они воспроизводят клипы без подмешивания вашего голоса.

Проверка физического микрофона доступна до установки виртуального кабеля. Сам драйвер VB-CABLE устанавливается отдельно.

## Библиотека

- Нажмите «Добавить звуки» или перетащите файлы в окно. Поддерживаются WAV, MP3, FLAC, M4A, AAC, WMA и AIFF. Декодирование зависит от мультимедийных компонентов Windows.
- Нажмите кнопку воспроизведения рядом с клипом, дважды щёлкните по строке или нажмите Enter на выбранном клипе. В нижней панели есть пауза, остановка и регулятор громкости клипов.
- Создавайте папки кнопкой «+» рядом с заголовком «Папки». Файлы, добавленные в открытую папку, попадут в неё. Через меню «…» можно переименовать клип или переместить его. При удалении папки её клипы остаются в разделе «Все звуки».
- Копии клипов, папки и настройки хранятся в `%LOCALAPPDATA%\VioletSoundpad`. Деинсталляция сохраняет библиотеку.

## Сборка из исходников

На Windows установите [.NET SDK](https://dotnet.microsoft.com/download), [пакет разработчика .NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48) и [Inno Setup 6.7 или 7](https://jrsoftware.org/inno-setup/downloads).

```powershell
.\scripts\build.ps1
```

При необходимости передайте путь к `ISCC.exe` параметром `-InnoCompiler`. Готовый установщик появится в папке `artifacts`.

## Публикация на GitHub

1. Создайте пустой репозиторий на GitHub. В папке проекта выполните команды ниже, подставив адрес нового репозитория вместо `REMOTE_URL`. [Загрузка локального проекта на GitHub](https://docs.github.com/en/migrations/importing-source-code/using-the-command-line-to-import-source-code/adding-locally-hosted-code-to-github)

   ```powershell
   git add -- .
   git commit -m "Initial release of Violet Soundpad"
   git branch -M main
   git remote add origin REMOTE_URL
   git push -u origin main
   ```

2. Процесс **Windows installer** в GitHub Actions собирает EXE и сохраняет его как артефакт. Возьмите установщик из артефакта или из локальной папки `artifacts`.
3. Создайте релиз с тегом `v1.0.0` и прикрепите `VioletSoundpad-Setup-1.0.0.exe` в раздел Assets. [Создание релизов в GitHub](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)
