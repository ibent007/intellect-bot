FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/IntellectBot/IntellectBot.csproj src/IntellectBot/packages.lock.json src/IntellectBot/
COPY src/IntellectBot.McpServer/IntellectBot.McpServer.csproj src/IntellectBot.McpServer/packages.lock.json src/IntellectBot.McpServer/
COPY src/IntellectBot.Shared/IntellectBot.Shared.csproj src/IntellectBot.Shared/packages.lock.json src/IntellectBot.Shared/
RUN dotnet restore src/IntellectBot/IntellectBot.csproj --locked-mode && dotnet restore src/IntellectBot.McpServer/IntellectBot.McpServer.csproj --locked-mode
COPY src/ src/
COPY data/demo-schedule.json data/demo-schedule.json
RUN dotnet publish src/IntellectBot/IntellectBot.csproj -c Release --no-restore -o /out/bot /p:UseAppHost=false
RUN dotnet publish src/IntellectBot.McpServer/IntellectBot.McpServer.csproj -c Release --no-restore -o /out/mcp /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /out/bot ./
COPY --from=build /out/mcp ./mcp/
ENV MCP_SERVER_PATH=/app/mcp/IntellectBot.McpServer.dll
ENV Schedule__File=/app/mcp/data/demo-schedule.json

RUN chmod -R a+rX /app

USER $APP_UID
ENTRYPOINT ["dotnet", "IntellectBot.dll"]
