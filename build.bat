@echo off
setlocal
cd /d "%~dp0"
set "OUT_DIR=src\UmamusumeWpfGui\bin\Release\net10.0-windows10.0.17763.0"
set "NATIVE_STAGED=build\native-staging\UmamusumeCore.dll"

tasklist /FI "IMAGENAME eq UmamusumeAss.exe" | find /I "UmamusumeAss.exe" >nul
if not errorlevel 1 goto :err_app_running

where cmake >nul 2>&1
if errorlevel 1 goto :err_cmake
where dotnet >nul 2>&1
if errorlevel 1 goto :err_dotnet
if not exist "build\release\CMakeCache.txt" (
    echo [1/4] Configuring CMake...
    cmake --preset release
    if errorlevel 1 goto :err_cmake_config
) else (
    echo [1/4] CMake already configured.
)
echo [2/4] Building UmamusumeCore.dll...
cmake --build build\release --config Release --target UmaAssistantCore
if errorlevel 1 goto :err_native
echo [3/4] Installing to native-staging...
cmake --install build\release --prefix build\native-staging --config Release
if errorlevel 1 goto :err_install
if not exist "%NATIVE_STAGED%" goto :err_install
echo [4/4] Publishing self-contained UmamusumeAss.exe...
dotnet publish src\UmamusumeWpfGui\UmamusumeWpfGui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "%OUT_DIR%" --nologo
if errorlevel 1 goto :err_dotnet_build
if not exist "%OUT_DIR%\UmamusumeAss.exe" goto :err_dotnet_build
copy /y "%NATIVE_STAGED%" "%OUT_DIR%\UmamusumeCore.dll"
if errorlevel 1 goto :err_copy_native
echo.
echo ============================================
echo   Build SUCCESSFUL
echo   Output: %OUT_DIR%\UmamusumeAss.exe
echo ============================================
echo.
goto :end
:err_app_running
echo [ERROR] UmamusumeAss.exe is still running.
echo         Close the running application, then run build.bat again.
goto :end_err
:err_cmake
echo [ERROR] cmake not found. Install CMake and add to PATH.
goto :end_err
:err_dotnet
echo [ERROR] dotnet not found. Install .NET SDK and add to PATH.
goto :end_err
:err_cmake_config
echo [ERROR] CMake configure failed.
goto :end_err
:err_native
echo [ERROR] Native library build failed.
goto :end_err
:err_install
echo [ERROR] Install step failed.
goto :end_err
:err_dotnet_build
echo [ERROR] Managed code build failed.
goto :end_err
:err_copy_native
echo [ERROR] Could not replace UmamusumeCore.dll.
echo         Close UmamusumeAss.exe and run build.bat again.
goto :end_err
:end_err
if /I not "%BUILD_NO_PAUSE%"=="1" pause
exit /b 1
:end
if /I not "%BUILD_NO_PAUSE%"=="1" pause
exit /b 0
