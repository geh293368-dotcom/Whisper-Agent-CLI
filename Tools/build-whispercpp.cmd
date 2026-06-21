@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
for %%i in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fi"

set "MODE=all"
set "FRESH_ARGS="
set "CUDA_ARCH=%WD_CUDA_ARCHITECTURES%"
if not defined CUDA_ARCH set "CUDA_ARCH=89"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="cpu" (
  set "MODE=cpu"
  shift
  goto parse_args
)
if /i "%~1"=="cuda" (
  set "MODE=cuda"
  shift
  goto parse_args
)
if /i "%~1"=="all" (
  set "MODE=all"
  shift
  goto parse_args
)
if /i "%~1"=="--fresh" (
  set "FRESH_ARGS=--fresh"
  shift
  goto parse_args
)
if /i "%~1"=="--cuda-arch" (
  if "%~2"=="" (
    echo Missing value for --cuda-arch.
    exit /b 1
  )
  set "CUDA_ARCH=%~2"
  shift
  shift
  goto parse_args
)
echo Unknown argument: %~1
echo Usage: build-whispercpp.cmd [cpu^|cuda^|all] [--fresh] [--cuda-arch ARCH]
exit /b 1

:args_done
if /i not "%MODE%"=="cpu" if /i not "%MODE%"=="cuda" if /i not "%MODE%"=="all" (
  echo Unknown backend mode: %MODE%
  exit /b 1
)

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo Visual Studio Installer's vswhere.exe was not found.
  exit /b 1
)

for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS_ROOT=%%i"
if not defined VS_ROOT (
  echo Visual Studio with the C++ x64 toolchain was not found.
  exit /b 1
)

set "CMAKE=%VS_ROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set "NINJA=%VS_ROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
set "SOURCE=%REPO_ROOT%\Native\WhisperCppBackend"

call "%VS_ROOT%\Common7\Tools\VsDevCmd.bat" -arch=x64 || exit /b 1
if /i "%MODE%"=="cpu" (
  call :build cpu OFF WhisperCppBackendCpu || exit /b 1
  exit /b 0
)
if /i "%MODE%"=="cuda" (
  call :build cuda ON WhisperCppBackendCuda || exit /b 1
  exit /b 0
)
call :build cpu OFF WhisperCppBackendCpu || exit /b 1
call :build cuda ON WhisperCppBackendCuda || exit /b 1
exit /b 0

:build
set "BUILD=%SOURCE%\build-%~1"
set "CUDA_ARGS="
if /i "%~2"=="ON" set "CUDA_ARGS=-DCMAKE_CUDA_ARCHITECTURES=%CUDA_ARCH%"
rem Refresh CMake's toolchain cache so an older Visual Studio installation cannot
rem be mixed with headers from the currently selected installation.
"%CMAKE%" %FRESH_ARGS% -Wno-deprecated -S "%SOURCE%" -B "%BUILD%" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_MAKE_PROGRAM="%NINJA%" -DWD_ENABLE_CUDA=%~2 -DWD_OUTPUT_NAME=%~3 %CUDA_ARGS% || exit /b 1
"%CMAKE%" --build "%BUILD%" --target WhisperCppBackend || exit /b 1
echo Built %BUILD%\bin\%~3.dll
exit /b 0
