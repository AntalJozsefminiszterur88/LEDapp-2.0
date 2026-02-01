@echo off
echo === LEDApp EXE build ===

:: Régi build fájlok törlése
rmdir /s /q build
rmdir /s /q dist
del main.spec

:: PyInstaller futtatása beágyazott ikonnal
pyinstaller main.py ^
--onefile ^
--icon=led_icon.ico ^
--add-data "led_icon.ico;." ^
--noconfirm ^
--name LEDApp

echo.
echo === Kész! Az EXE itt található: dist\LEDApp.exe ===
pause