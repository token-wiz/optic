taskkill /F /IM dotnet.exe 2>$null; Start-Sleep -Seconds 2; cd "c:\workspace\OPTIC\OPTIC" ; dotnet run -- --web 127.0.0.1 5070
