@echo off
setlocal EnableExtensions
cd /d "%~dp0"
call _env.bat || exit /b 1

for /f "delims=" %%v in ('powershell -NoProfile -Command "([xml](Get-Content App\App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"') do set APPVER=%%v
if not defined APPVER (echo [ERREUR] Version introuvable dans App\App.csproj & exit /b 1)

set OUT=dist\%RID%
set ZIP=dist\FrameCastStudio-v%APPVER%-%RID%.zip
set R2R=true
if /i "%NO_R2R%"=="1" set R2R=false

:build
echo.
echo === Publish FrameCast Studio %APPVER% / %RID% / autonome / ReadyToRun=%R2R% ===
if exist "%OUT%" rd /s /q "%OUT%"
dotnet publish App\App.csproj -c %CONFIG% -r %RID% -p:Platform=%PLATFORM% ^
  --self-contained true -p:PublishReadyToRun=%R2R% -p:PublishSingleFile=false ^
  -o "%OUT%" || exit /b 1

call :overlay || exit /b 1
call :verify || exit /b 1
call :smoke
if errorlevel 1 (
  if "%R2R%"=="true" (
    echo.
    echo [ATTENTION] Le test de lancement a echoue avec ReadyToRun : nouvelle tentative sans ReadyToRun.
    set R2R=false
    goto build
  )
  echo.
  echo [ERREUR] L'application publiee ne demarre pas : aucun zip n'a ete cree.
  exit /b 1
)

if exist "%ZIP%" del "%ZIP%"
echo.
echo === Archive de mise a jour : %ZIP% ===
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path '%OUT%').Path, (Join-Path (Get-Location) '%ZIP%'), [IO.Compression.CompressionLevel]::Optimal, $false)" || exit /b 1
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $z=[IO.Compression.ZipFile]::OpenRead((Join-Path (Get-Location) '%ZIP%')); $ok=$z.Entries | Where-Object { $_.FullName -eq 'FrameCastStudio.exe' }; $z.Dispose(); if(-not $ok){ exit 1 }" || (echo [ERREUR] FrameCastStudio.exe n'est pas a la racine du zip & exit /b 1)
powershell -NoProfile -Command "$h=(Get-FileHash '%ZIP%' -Algorithm SHA256).Hash.ToLower(); Set-Content -NoNewline -Path '%ZIP%.sha256' -Value ($h + '  ' + (Split-Path '%ZIP%' -Leaf))"

echo.
echo [OK] Dossier testable : %OUT%\FrameCastStudio.exe
echo [OK] A joindre a la release GitHub, tag v%APPVER% :
echo      %ZIP%
echo      %ZIP%.sha256
endlocal
exit /b 0

:overlay
rem La build produit un .pri fusionne avec les ressources WinUI. On le recopie par-dessus la sortie de publish
rem pour etre sur que dist contient exactement celui de la build qui fonctionne.
set BIN=App\bin\%PLATFORM%\%CONFIG%\net10.0-windows10.0.22621.0\%RID%
if not exist "%BIN%\FrameCastStudio.pri" (echo [INFO] Pas de .pri dans %BIN% : superposition ignoree. & exit /b 0)
echo === Superposition des ressources WinUI de la build : .pri .xbf Microsoft.UI.Xaml ===
robocopy "%BIN%" "%OUT%" *.pri *.xbf /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (echo [ERREUR] Copie des ressources impossible. & exit /b 1)
if exist "%BIN%\Microsoft.UI.Xaml" robocopy "%BIN%\Microsoft.UI.Xaml" "%OUT%\Microsoft.UI.Xaml" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (echo [ERREUR] Copie de Microsoft.UI.Xaml impossible. & exit /b 1)
exit /b 0

:verify
if not exist "%OUT%\FrameCastStudio.exe" (echo [ERREUR] FrameCastStudio.exe absent de %OUT% & exit /b 1)
if not exist "%OUT%\coreclr.dll" (echo [ERREUR] Build non autonome : coreclr.dll absent. Le runtime .NET n'est pas embarque. & exit /b 1)
if not exist "%OUT%\Microsoft.ui.xaml.dll" (echo [ERREUR] Windows App SDK absent de la sortie. & exit /b 1)
findstr /c:"includedFrameworks" "%OUT%\FrameCastStudio.runtimeconfig.json" >nul || (echo [ERREUR] runtimeconfig non autonome. & exit /b 1)
rem --- fichiers XAML compiles : sans eux, la fenetre ne peut pas se charger "XAML parsing failed"
set OBJ=App\obj\%PLATFORM%\%CONFIG%\net10.0-windows10.0.22621.0\%RID%
for %%f in (App.xbf MainWindow.xbf) do call :needfile %%f || exit /b 1
if not exist "%OUT%\FrameCastStudio.pri" if not exist "%OUT%\resources.pri" (echo [ERREUR] Aucun fichier .pri dans %OUT%. & exit /b 1)
echo Fichiers XAML et ressources presents dans %OUT% :
for %%f in ("%OUT%\*.pri" "%OUT%\*.xbf") do echo    %%~nxf  %%~zf octets
exit /b 0

:needfile
if exist "%OUT%\%1" exit /b 0
if exist "%OBJ%\%1" (copy /y "%OBJ%\%1" "%OUT%\%1" >nul & echo [INFO] %1 manquait dans la sortie : copie depuis obj. & exit /b 0)
echo [ERREUR] %1 introuvable ni dans %OUT% ni dans %OBJ%.
exit /b 1

:smoke
if /i "%SKIP_SMOKE%"=="1" (echo [INFO] Test de lancement ignore. & exit /b 0)
if /i "%PLATFORM%"=="ARM64" if /i not "%PROCESSOR_ARCHITECTURE%"=="ARM64" (echo [INFO] Test de lancement ignore : cible ARM64 sur un PC non ARM64. & exit /b 0)
echo === Test de lancement : le processus doit rester vivant 10 s ===
set FRAMECAST_SMOKETEST=1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$exe=(Resolve-Path '%OUT%\FrameCastStudio.exe').Path; $p=Start-Process -FilePath $exe -PassThru; $t=0; while($t -lt 100 -and -not $p.HasExited){ Start-Sleep -Milliseconds 100; $t++ }; if($p.HasExited){ Write-Host ('[ECHEC] Le processus s est arrete, code ' + $p.ExitCode); exit 2 }; $p.Refresh(); if($p.MainWindowHandle -eq 0){ Write-Host '[ATTENTION] Aucune fenetre detectee, mais le processus tourne.' }; Stop-Process -Id $p.Id -Force; Write-Host '[OK] Le processus tourne.'; exit 0"
set SMOKE_RC=%ERRORLEVEL%
set FRAMECAST_SMOKETEST=
if not "%SMOKE_RC%"=="0" call :showlog
exit /b %SMOKE_RC%

:showlog
echo.
echo --- Fin du journal : %LOCALAPPDATA%\FrameCastStudio\log.txt ---
powershell -NoProfile -Command "$l=Join-Path $env:LOCALAPPDATA 'FrameCastStudio\log.txt'; if(Test-Path $l){ Get-Content $l -Tail 25 } else { Write-Host 'Journal absent : le crash survient avant le code .NET, voir l Observateur d evenements.' }"
exit /b 0
