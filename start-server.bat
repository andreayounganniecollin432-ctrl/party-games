@echo off
chcp 65001 >nul
title PartyGames 服务器
cd /d "%~dp0src\PartyGames.Server\bin\Release\net10.0"

echo ============================================
echo   朋友在插件里填这个地址：
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /c:"IPv4"') do call :show %%a
echo ============================================
echo.

set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

"%DOTNET%" "PartyGames.Server.dll"

echo.
echo 服务器已停止。
pause >nul
exit /b

:show
set "ip=%1"
set "ip=%ip: =%"
echo     ws://%ip%:5180/ws
exit /b
