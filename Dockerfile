FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/IntellectBot/IntellectBot.csproj src/IntellectBot/packages.lock.json src/IntellectBot/
RUN dotnet restore src/IntellectBot/IntellectBot.csproj --locked-mode
COPY src/ src/
RUN dotnet publish src/IntellectBot/IntellectBot.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "IntellectBot.dll"]
