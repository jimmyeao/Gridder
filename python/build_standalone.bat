@echo off
REM Build gridder_analysis into a standalone executable (no Python required).
REM Run this from the python/ directory.
REM
REM Prerequisites:
REM   pip install pyinstaller
REM   pip install -r requirements.txt
REM
REM Output: dist\gridder_analysis\gridder_analysis.exe

setlocal

echo === Building gridder_analysis standalone ===

REM Ensure we're in the python directory
if not exist "gridder_analysis\__main__.py" (
    echo ERROR: Run this script from the python/ directory.
    exit /b 1
)

REM Install PyInstaller if needed
pip show pyinstaller >nul 2>&1
if errorlevel 1 (
    echo Installing PyInstaller...
    pip install pyinstaller
)

REM Clean previous build
if exist build\gridder_analysis rmdir /s /q build\gridder_analysis
if exist dist\gridder_analysis rmdir /s /q dist\gridder_analysis

echo Running PyInstaller...
pyinstaller gridder_analysis.spec --noconfirm

if errorlevel 1 (
    echo.
    echo === BUILD FAILED ===
    echo Check the output above for errors. Common fixes:
    echo   - Ensure all dependencies are installed: pip install -r requirements.txt
    echo   - Ensure PyInstaller is installed: pip install pyinstaller
    exit /b 1
)

REM Check that ffmpeg is bundled or available
where ffmpeg >nul 2>&1
if errorlevel 1 (
    echo.
    echo WARNING: ffmpeg not found on PATH.
    echo madmom needs ffmpeg to decode MP3 files.
    echo Download ffmpeg and place ffmpeg.exe in dist\gridder_analysis\
    echo   https://www.gyan.dev/ffmpeg/builds/
)

echo.
echo === BUILD SUCCEEDED ===
echo Output: dist\gridder_analysis\gridder_analysis.exe
echo.
echo To test: dist\gridder_analysis\gridder_analysis.exe "path\to\song.mp3"
echo To deploy: copy the entire dist\gridder_analysis\ folder alongside your app.
