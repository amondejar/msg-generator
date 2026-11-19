# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (only project files) so Docker caches the layer while dependencies don't change.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/{{Service}}.ServiceDefaults/{{Service}}.ServiceDefaults.csproj src/{{Service}}.ServiceDefaults/
COPY src/{{Service}}.Api/{{Service}}.Api.csproj src/{{Service}}.Api/
RUN dotnet restore src/{{Service}}.Api/{{Service}}.Api.csproj

COPY src/ src/
RUN dotnet publish src/{{Service}}.Api/{{Service}}.Api.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Non-root user shipped with the official .NET images.
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "{{Service}}.Api.dll"]
