@echo off
REM Quick start script for CivitAI Downloader v04
REM Windows batch file for easy setup and usage

echo ========================================
echo CivitAI Collection Downloader v04
echo ========================================
echo.

REM Check if Python is installed
python --version >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Python is not installed or not in PATH
    echo Please install Python 3.8 or higher from python.org
    pause
    exit /b 1
)

echo [1/4] Checking Python version...
python --version

REM Check if config exists
if not exist "config.yaml" (
    echo.
    echo ERROR: config.yaml not found!
    echo Please make sure config.yaml is in this directory.
    pause
    exit /b 1
)

echo [2/4] Checking dependencies...
pip show requests >nul 2>&1
if %errorlevel% neq 0 (
    echo Installing dependencies...
    pip install -r requirements.txt
) else (
    echo Dependencies already installed.
)

echo [3/4] Testing authentication...
python main.py test-auth

if %errorlevel% neq 0 (
    echo.
    echo WARNING: Authentication test failed!
    echo Make sure you're logged into CivitAI in Chrome.
    echo.
    set /p continue="Continue anyway? (y/n): "
    if /i not "%continue%"=="y" exit /b 1
)

echo.
echo [4/4] Ready to sync!
echo.
echo What would you like to do?
echo.
echo   1. Sync all collections from config
echo   2. Sync one specific collection
echo   3. Show statistics
echo   4. Retry failed downloads
echo   5. Exit
echo.

set /p choice="Enter choice (1-5): "

if "%choice%"=="1" (
    echo.
    echo Starting sync of all collections...
    python main.py sync
    goto :end
)

if "%choice%"=="2" (
    set /p collection_id="Enter collection ID: "
    set /p collection_name="Enter collection name: "
    echo.
    echo Syncing collection %collection_name% (ID: %collection_id%)...
    python main.py sync --collection %collection_id% --name "%collection_name%"
    goto :end
)

if "%choice%"=="3" (
    python main.py stats
    goto :end
)

if "%choice%"=="4" (
    echo.
    echo Retrying failed downloads...
    python main.py retry
    goto :end
)

if "%choice%"=="5" (
    echo Goodbye!
    exit /b 0
)

echo Invalid choice!
pause
exit /b 1

:end
echo.
echo ========================================
echo Done!
echo ========================================
pause
