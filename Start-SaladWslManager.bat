@echo off
setlocal

set "EXE=%~dp0bin\SaladWslManager.exe"

if not exist "%EXE%" (
  echo Missing: %EXE%
  pause
  exit /b 1
)

start "" "%EXE%"
