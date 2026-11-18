@echo off
chcp 65001 >nul
title PartyGames 隧道
echo ============================================================
echo   保持这个窗口开着，关掉隧道就断了。
echo   窗口里会出现一行 https://xxxx.trycloudflare.com
echo   朋友在插件里填：wss://那个地址/ws
echo ============================================================
echo.

set "CF=C:\Program Files (x86)\cloudflared\cloudflared.exe"
if not exist "%CF%" (
  where cloudflared >nul 2>nul
  if errorlevel 1 (
    echo 找不到 cloudflared，请先安装：winget install Cloudflare.cloudflared
    pause >nul
    exit /b 1
  )
  set "CF=cloudflared"
)

"%CF%" tunnel --url http://127.0.0.1:5180 --no-autoupdate

echo.
echo 隧道已断开。按任意键关闭。
pause >nul
