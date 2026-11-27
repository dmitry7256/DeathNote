# Сборка релизного Setup.exe (Windows 10/11 x64)

1. Установите .NET **10 SDK**: https://dotnet.microsoft.com/download/dotnet/10.0
2. Установите **Inno Setup 6**: https://jrsoftware.org/isdl.php
3. Распакуйте архив проекта, например в `C:\DeathNoteBuild\DeathNote_WPF_3.04_CSharp_only` (желательно вне OneDrive)
4. Откройте эту папку и запустите **`BUILD_INSTALLER.bat`**
5. Дождитесь сообщения `Ready: DeathNote_Setup_3.04_x64.exe`
6. Проверьте собранный установщик на Windows: установка, запуск, Settings → Check integrity и Check modules
7. В GitHub Releases прикрепляйте **только готовый `DeathNote_Setup_3.04_x64.exe`**, не `published-installer`, не исходный ZIP и не личную базу

`BUILD_INSTALLER.bat` вызывает `PUBLISH_INSTALLER.ps1` и Inno Setup. Не путайте его с `INSTALL_FROM_SOURCE.bat`, который устанавливает сборку на компьютер разработчика.

Внутри исходников нет готового `Setup.exe`: компиляция WPF и упаковка Inno Setup выполняются на Windows. При ошибке проверьте `INSTALL_COMPILER.log` и сообщение в консоли. Исходные данные хранятся отдельно в `%LOCALAPPDATA%\DeathNote\data`.
