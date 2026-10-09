@echo off
rem Compile la version Linux depuis Windows (binaire autonome, SANS ffmpeg ni AppImage).
rem Sert surtout a verifier que le code compile. Les paquets finaux sont produits par GitHub Actions (.github/workflows/linux.yml).
cd /d "%~dp0"
dotnet publish FrameCastStudio.Linux.csproj -c Release -r linux-x64 --self-contained true -o dist\linux-x64
if errorlevel 1 (echo. & echo ECHEC de la compilation & pause & exit /b 1)
echo.
echo OK : dist\linux-x64\FrameCastStudio
pause
