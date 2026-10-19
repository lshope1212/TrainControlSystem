@echo off
setlocal
set "LAB_PYTHON="
set "LAB_PYTHON_ARGS="

if defined VOICEMAIL_PYTHON goto custom_python

py -3 -c "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)" >nul 2>&1
if not errorlevel 1 (
    set "LAB_PYTHON=py"
    set "LAB_PYTHON_ARGS=-3"
    goto run
)

python -c "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)" >nul 2>&1
if not errorlevel 1 (
    set "LAB_PYTHON=python"
    goto run
)
python3 -c "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)" >nul 2>&1
if not errorlevel 1 (
    set "LAB_PYTHON=python3"
    goto run
)

echo Python 3.8 or newer was not found. Install Python, then reopen this launcher.
echo You can also set VOICEMAIL_PYTHON to the full path of a Python executable.
if "%~1"=="" pause
exit /b 1

:custom_python
"%VOICEMAIL_PYTHON%" -c "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)" >nul 2>&1
if errorlevel 1 (
    echo VOICEMAIL_PYTHON must point to a working Python 3.8 or newer executable.
    if "%~1"=="" pause
    exit /b 1
)
set "LAB_PYTHON=%VOICEMAIL_PYTHON%"

:run
"%LAB_PYTHON%" %LAB_PYTHON_ARGS% -B "%~dp0lab.py" %*
set "LAB_EXIT=%ERRORLEVEL%"
if "%~1"=="" if not "%LAB_EXIT%"=="0" pause
exit /b %LAB_EXIT%
