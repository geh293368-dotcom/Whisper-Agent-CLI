@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
for %%i in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fi"
set "WEB_DIR=%REPO_ROOT%\Examples\WhisperDesktop.Web"
set "NODE_EXE=%ProgramFiles%\nodejs\node.exe"
set "NPM_CLI=%ProgramFiles%\nodejs\node_modules\npm\bin\npm-cli.js"
set "INSTALL_MODE=auto"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--install" (
  set "INSTALL_MODE=always"
  shift
  goto parse_args
)
if /i "%~1"=="--skip-install" (
  set "INSTALL_MODE=never"
  shift
  goto parse_args
)
echo Unknown argument: %~1
echo Usage: build-webui.cmd [--install^|--skip-install]
exit /b 1

:args_done

if not exist "%NODE_EXE%" (
  echo WARNING: Node.js was not found at "%NODE_EXE%". Skipping Web UI build.
  exit /b 0
)

if not exist "%NPM_CLI%" (
  echo WARNING: npm was not found at "%NPM_CLI%". Skipping Web UI build.
  exit /b 0
)

pushd "%WEB_DIR%"
set "NEED_INSTALL=0"
if /i "%INSTALL_MODE%"=="always" set "NEED_INSTALL=1"
if /i "%INSTALL_MODE%"=="auto" if not exist "node_modules" set "NEED_INSTALL=1"
if "%NEED_INSTALL%"=="1" (
  if exist "package-lock.json" (
    "%NODE_EXE%" "%NPM_CLI%" ci
  ) else (
    "%NODE_EXE%" "%NPM_CLI%" install
  )
  set "RESULT=%ERRORLEVEL%"
  if not "%RESULT%"=="0" (
    popd
    exit /b %RESULT%
  )
)
"%NODE_EXE%" "%NPM_CLI%" run build
set "RESULT=%ERRORLEVEL%"
popd

exit /b %RESULT%
