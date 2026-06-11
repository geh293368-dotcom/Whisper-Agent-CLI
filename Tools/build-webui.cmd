@echo off
setlocal

set "WEB_DIR=%~dp0..\Examples\WhisperDesktop.Web"
set "NODE_EXE=%ProgramFiles%\nodejs\node.exe"
set "NPM_CLI=%ProgramFiles%\nodejs\node_modules\npm\bin\npm-cli.js"

if not exist "%NODE_EXE%" (
  echo Node.js was not found at "%NODE_EXE%".
  exit /b 1
)

if not exist "%NPM_CLI%" (
  echo npm was not found at "%NPM_CLI%".
  exit /b 1
)

pushd "%WEB_DIR%"
"%NODE_EXE%" "%NPM_CLI%" run build
set "RESULT=%ERRORLEVEL%"
popd

exit /b %RESULT%
