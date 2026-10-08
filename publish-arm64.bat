@echo off
setlocal
set PLATFORM=ARM64
set RID=win-arm64
call "%~dp0publish.bat"
exit /b %ERRORLEVEL%
