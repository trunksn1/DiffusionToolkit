@echo off
mode con: cols=120 lines=30

REM Close Diffusion Toolkit
taskkill /IM "Diffusion.Toolkit.exe" /F 2>nul

REM Wait a moment to ensure the application has closed
timeout /t 2 /nobreak >nul

REM Run the pipeline
cd /d "E:\Clouding\Dropbox\INFORMATICA\GitHub\CivitAi Prompt Examiner"
call "E:\Clouding\Dropbox\INFORMATICA\GitHub\CivitAi Prompt Examiner\.venv1\Scripts\activate.bat"

python "run_pipeline.py"

REM Reopen Diffusion Toolkit
echo.
echo Pipeline completed. Reopening Diffusion Toolkit...
timeout /t 2 /nobreak >nul

REM Find and launch Diffusion Toolkit executable
cd /d "%~dp0.."
if exist "Diffusion.Toolkit.exe" (
    start "" "Diffusion.Toolkit.exe"
) else (
    echo Warning: Could not find Diffusion.Toolkit.exe in the parent directory.
    echo Please manually restart Diffusion Toolkit.
)

pause
