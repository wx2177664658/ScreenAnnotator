@echo off
setlocal
cd /d "%~dp0.."

echo [1/2] Publishing self-contained win-x64...
dotnet publish src\ScreenAnnotator\ScreenAnnotator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish\ScreenAnnotator
if errorlevel 1 exit /b 1

set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo ISCC.exe not found. Install Inno Setup 6 first.
  exit /b 1
)

echo [2/2] Building installer with Inno Setup...
"%ISCC%" installer\ScreenAnnotator.iss
if errorlevel 1 exit /b 1

echo.
echo Output: dist\ScreenAnnotator-Setup-1.0.8.exe
exit /b 0
