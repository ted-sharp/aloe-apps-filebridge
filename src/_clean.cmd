@echo off

cd /d %~dp0

REM Recursively delete .vs, bin, obj, publish, logs, tmp folders under current directory
for /d /r %%d in (.vs bin obj publish, logs, tmp) do (
    if exist "%%d" (
        echo Deleting directory: "%%d"
        rd /s /q "%%d"
    )
)

REM Delete *.nettrace files under current directory
for /r %%f in (*.nettrace) do (
    echo Deleting file: "%%f"
    del /q "%%f"
)

echo Completed.
pause
