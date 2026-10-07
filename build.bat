@echo off
setlocal
cd /d "%~dp0"
call _env.bat || exit /b 1
echo === Restore ===
dotnet restore App\App.csproj -p:Platform=%PLATFORM% -r %RID% || exit /b 1
echo === Build %CONFIG% %PLATFORM% ===
dotnet build App\App.csproj -c %CONFIG% -p:Platform=%PLATFORM% -r %RID% --no-restore || exit /b 1
echo.
echo [OK] Build termine. Lancer : run.bat
endlocal
