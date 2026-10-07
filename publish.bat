@echo off
setlocal
cd /d "%~dp0"
call _env.bat || exit /b 1

for /f "delims=" %%v in ('powershell -NoProfile -Command "([xml](Get-Content App\App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"') do set APPVER=%%v
if not defined APPVER (echo [ERREUR] Version introuvable dans App\App.csproj & exit /b 1)

echo === Publish FrameCast Studio %APPVER% self-contained %RID% (ReadyToRun) ===
if exist dist\%RID% rd /s /q dist\%RID%
dotnet publish App\App.csproj -c %CONFIG% -r %RID% -p:Platform=%PLATFORM% ^
  --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=false ^
  -o dist\%RID% || exit /b 1

if not exist dist\%RID%\FrameCastStudio.exe (echo [ERREUR] FrameCastStudio.exe absent de la sortie & exit /b 1)

set ZIP=dist\FrameCastStudio-v%APPVER%-%RID%.zip
if exist "%ZIP%" del "%ZIP%"
echo === Archive de mise a jour : %ZIP% ===
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path 'dist\%RID%').Path, (Join-Path (Get-Location) '%ZIP%'), [IO.Compression.CompressionLevel]::Optimal, $false)" || exit /b 1
powershell -NoProfile -Command "$h=(Get-FileHash '%ZIP%' -Algorithm SHA256).Hash.ToLower(); Set-Content -NoNewline -Path '%ZIP%.sha256' -Value ($h + '  ' + (Split-Path '%ZIP%' -Leaf))"

echo.
echo [OK] Dossier : dist\%RID%\FrameCastStudio.exe
echo [OK] A joindre a la release GitHub (tag v%APPVER%) :
echo      %ZIP%
echo      %ZIP%.sha256
endlocal
