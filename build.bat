@echo off
setlocal
cd /d "%~dp0"

rem This is the only application output directory produced by this script.
set "OUT_DIR=src\UmamusumeWpfGui\bin\Release\net10.0-windows10.0.17763.0"
set "BUILD_DIR=build\release"
set "NATIVE_STAGING=build\native-staging"
set "NATIVE_DLL=%NATIVE_STAGING%\UmamusumeCore.dll"
set "PUBLISH_DIR=build\publish\win-x64"
set "PROJECT=src\UmamusumeWpfGui\UmamusumeWpfGui.csproj"

tasklist /FI "IMAGENAME eq UmamusumeAss.exe" | find /I "UmamusumeAss.exe" >nul
if not errorlevel 1 goto :err_app_running

where cmake >nul 2>&1
if errorlevel 1 goto :err_cmake
where dotnet >nul 2>&1
if errorlevel 1 goto :err_dotnet

if not exist "%BUILD_DIR%\CMakeCache.txt" (
    echo [1/5] Configuring native release build...
    cmake --preset release
    if errorlevel 1 goto :err_cmake_config
) else (
    echo [1/5] Native release build already configured.
)

echo [2/5] Building UmamusumeCore.dll...
cmake --build "%BUILD_DIR%" --config Release --target UmaAssistantCore --parallel
if errorlevel 1 goto :err_native

echo [3/5] Staging native DLL...
if exist "%NATIVE_STAGING%" rmdir /s /q "%NATIVE_STAGING%"
cmake --install "%BUILD_DIR%" --prefix "%NATIVE_STAGING%" --config Release
if errorlevel 1 goto :err_install
if not exist "%NATIVE_DLL%" goto :err_install

echo [4/5] Cleaning old application output...
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
if exist "%OUT_DIR%\*" del /f /q "%OUT_DIR%\*" >nul 2>&1
for /d %%D in ("%OUT_DIR%\*") do rmdir /s /q "%%~fD" >nul 2>&1
for /f "delims=" %%F in ('dir /a /b "%OUT_DIR%" 2^>nul') do goto :err_output_clean

echo [5/6] Publishing self-contained single-file application...
if exist "build\publish" rmdir /s /q "build\publish"
if exist "build\publish" goto :err_publish_clean
mkdir "%PUBLISH_DIR%"
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -p:PublishTrimmed=false ^
    -o "%PUBLISH_DIR%" --nologo
if errorlevel 1 goto :err_dotnet_build
if not exist "%PUBLISH_DIR%\UmamusumeAss.exe" goto :err_dotnet_build

echo [6/6] Copying final files to application output...
xcopy /e /i /y /q "%PUBLISH_DIR%\*" "%OUT_DIR%\" >nul
if errorlevel 1 goto :err_copy_publish
if exist "%OUT_DIR%\win-x64" rmdir /s /q "%OUT_DIR%\win-x64"
if exist "%OUT_DIR%\win-x64" goto :err_output_clean
copy /y "%NATIVE_DLL%" "%OUT_DIR%\UmamusumeCore.dll" >nul
if errorlevel 1 goto :err_copy_native
if not exist "%OUT_DIR%\UmamusumeCore.dll" goto :err_copy_native
if not exist "%OUT_DIR%\resource\connection.json" goto :err_resources

if exist "build\publish" rmdir /s /q "build\publish"
if exist "build\publish" goto :err_publish_clean

rem A single-file self-contained publish has no framework dependency.
rem If a runtimeconfig is emitted, it must contain includedFrameworks.
if exist "%OUT_DIR%\UmamusumeAss.runtimeconfig.json" (
    findstr /C:"includedFrameworks" "%OUT_DIR%\UmamusumeAss.runtimeconfig.json" >nul
    if errorlevel 1 goto :err_framework_dependent
)

echo.
echo ============================================
echo   Build SUCCESSFUL
echo   Self-contained: YES
echo   Output: %OUT_DIR%\UmamusumeAss.exe
echo ============================================
echo.
goto :end

:err_app_running
echo [ERROR] UmamusumeAss.exe is still running. Close it and run build.bat again.
goto :end_err
:err_cmake
echo [ERROR] cmake not found. Install CMake and add it to PATH.
goto :end_err
:err_dotnet
echo [ERROR] dotnet not found. Install the .NET 10 SDK and add it to PATH.
goto :end_err
:err_cmake_config
echo [ERROR] CMake configure failed.
goto :end_err
:err_native
echo [ERROR] Native library build failed.
goto :end_err
:err_install
echo [ERROR] Native install/staging failed.
goto :end_err
:err_output_clean
echo [ERROR] Could not clean the old output directory. Close any process using it and retry.
goto :end_err
:err_dotnet_build
echo [ERROR] .NET publish failed.
goto :end_err
:err_copy_native
echo [ERROR] Could not place UmamusumeCore.dll beside the application.
goto :end_err
:err_copy_publish
echo [ERROR] Could not copy the publish output to the application directory.
goto :end_err
:err_publish_clean
echo [ERROR] Could not clean the temporary publish directory.
goto :end_err
:err_resources
echo [ERROR] Required resource files were not included in the publish output.
goto :end_err
:err_framework_dependent
echo [ERROR] Publish output still depends on an installed .NET runtime.
goto :end_err

:end_err
if /I not "%BUILD_NO_PAUSE%"=="1" pause
exit /b 1
:end
if /I not "%BUILD_NO_PAUSE%"=="1" pause
exit /b 0
