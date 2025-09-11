@echo off
setlocal
:PROMPT
SET /P AREYOUSURE=Are you sure you want to publish (y/N)?
IF /I "%AREYOUSURE%" NEQ "Y" GOTO END

dotnet build -c Release -target:PackTS -v d -property:PublishTS=true


:END
endlocal
echo done
pause