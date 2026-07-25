@echo off
rem Double-click installer for SeaPowerUX on Windows.
rem
rem This just runs install.ps1 (which does the real work) with an execution-policy bypass so it
rem works without changing your system PowerShell settings. Any arguments are passed through, e.g.
rem   install.bat -Uninstall
rem   install.bat -GameDir "D:\Steam\steamapps\common\Sea Power"

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set EXITCODE=%ERRORLEVEL%

rem Pause only when launched by double-click (no console attached), so it isn't annoying from a
rem terminal. If run from cmd, %cmdcmdline% contains our own name; a double-click shows just the
rem shell invocation.
echo %cmdcmdline% | find /i "%~nx0" >nul
if errorlevel 1 pause

exit /b %EXITCODE%
