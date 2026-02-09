@echo off
setlocal

REM =============================================================
REM  Gridder Installer Build Script
REM  Builds the .NET app + Python standalone + Inno Setup installer
REM
REM  Prerequisites:
REM    - .NET 10 SDK
REM    - Python 3.12 venv at python\.venv with deps installed
REM    - Inno Setup 6 (iscc.exe on PATH, or installed at default location)
REM    - Visual Studio Build Tools (for madmom C extensions)
REM =============================================================

set ROOT=%~dp0..
set PUBLISH_DIR=%ROOT%\publish\win-x64
set PYTHON_DIR=%ROOT%\python
set INSTALLER_DIR=%ROOT%\installer

echo.
echo ========================================
echo  Gridder Installer Build
echo ========================================
echo.

REM --- Step 1: Publish .NET MAUI app ---
echo [1/3] Publishing .NET MAUI app...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"

dotnet publish "%ROOT%\Gridder\Gridder.csproj" ^
    -f net10.0-windows10.0.19041.0 ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -o "%PUBLISH_DIR%" ^
    -p:WindowsPackageType=None ^
    -p:PublishSingleFile=false

if errorlevel 1 (
    echo ERROR: .NET publish failed!
    exit /b 1
)
echo .NET publish complete.
echo.

REM --- Step 2: Build Python standalone exe ---
echo [2/3] Building Python standalone exe...
if not exist "%PYTHON_DIR%\.venv\Scripts\pyinstaller.exe" (
    echo ERROR: PyInstaller not found. Run: python\.venv\Scripts\pip install pyinstaller
    exit /b 1
)

pushd "%PYTHON_DIR%"
call "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul 2>&1
.venv\Scripts\pyinstaller.exe gridder_analysis.spec --noconfirm
if errorlevel 1 (
    echo ERROR: PyInstaller build failed!
    popd
    exit /b 1
)
popd
echo Python standalone build complete.
echo.

REM --- Step 3: Build installer ---
echo [3/3] Building installer...

REM Find Inno Setup compiler
set ISCC=
where iscc >nul 2>&1 && set ISCC=iscc
if not defined ISCC (
    if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)
if not defined ISCC (
    if exist "C:\Program Files\Inno Setup 6\ISCC.exe" set ISCC="C:\Program Files\Inno Setup 6\ISCC.exe"
)
if not defined ISCC (
    if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set ISCC="%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
)

if not defined ISCC (
    echo.
    echo WARNING: Inno Setup not found. Skipping installer creation.
    echo Install from: https://jrsoftware.org/isinfo.php
    echo Then run:  iscc installer\gridder.iss
    echo.
    echo Build output is ready at:
    echo   .NET app:   %PUBLISH_DIR%
    echo   Python exe: %PYTHON_DIR%\dist\gridder_analysis
    exit /b 0
)

%ISCC% "%INSTALLER_DIR%\gridder.iss"
if errorlevel 1 (
    echo ERROR: Inno Setup build failed!
    exit /b 1
)

echo.
echo ========================================
echo  BUILD COMPLETE
echo ========================================
echo  Installer: %INSTALLER_DIR%\Output\GridderSetup.exe
echo ========================================
