@echo off
echo === LEDApp EXE build (no console) ===

:: Régi build fájlok törlése
rmdir /s /q build
rmdir /s /q dist
del LEDApp.spec 2>nul

:: PyInstaller futtatása ikonnal, konzol nélkül
pyinstaller main.py ^
--onefile ^
--icon=led_icon.ico ^
--add-data "led_icon.ico;." ^
--noconfirm ^
--name LEDApp ^
--windowed

echo.
echo === Kész! Az EXE itt található: dist\LEDApp.exe ===
pause

