@echo off
setlocal

set "WEB_DIR=%~dp0..\Examples\WhisperDesktop.Web"
set "NODE_EXE=%ProgramFiles%\nodejs\node.exe"
set "NPM_CLI=%ProgramFiles%\nodejs\node_modules\npm\bin\npm-cli.js"

if not exist "%NODE_EXE%" (
  echo WARNING: Node.js was not found at "%NODE_EXE%". Skipping Web UI build.
  exit /b 0
)

if not exist "%NPM_CLI%" (
  echo WARNING: npm was not found at "%NPM_CLI%". Skipping Web UI build.
  exit /b 0
)

pushd "%WEB_DIR%"
"%NODE_EXE%" "%NPM_CLI%" run build
set "RESULT=%ERRORLEVEL%"
popd

exit /b %RESULT%
