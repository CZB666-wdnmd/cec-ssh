# cec-ssh
run cec-ctl on windows
need .NET 9

dotnet add package SSH.NET
dotnet publish -c Release -r win-x64    --self-contained true    -p:PublishSingleFile=true    -p:IncludeAllContentForSelfExtract=true  -p:PublishTrimmed=false
