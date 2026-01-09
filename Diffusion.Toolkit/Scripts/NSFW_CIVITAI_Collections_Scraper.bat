@echo off
mode con: cols=120 lines=30

REM Delete completion marker if it exists from previous run
if exist "%~dp0.download_complete" del "%~dp0.download_complete"

cd /d "E:\Clouding\Dropbox\INFORMATICA\GitHub\Civitai Collections Scraper"
call ".venv\Scripts\activate.bat"

python main.py sync

REM Create completion marker file
echo Done > "%~dp0.download_complete"

pause
