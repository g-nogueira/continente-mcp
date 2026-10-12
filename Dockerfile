FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Continente.Mcp/Continente.Mcp.csproj src/Continente.Mcp/
RUN dotnet restore src/Continente.Mcp/Continente.Mcp.csproj
COPY . .
RUN dotnet publish src/Continente.Mcp/Continente.Mcp.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Continente.Mcp.dll"]
