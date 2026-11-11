# syntax=docker/dockerfile:1
# --- Stage 1: build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
# --locked-mode: falla si las dependencias no coinciden con los packages.lock.json versionados.
RUN dotnet restore src/application/Pragma.Accounts.Application/Pragma.Accounts.Application.csproj --locked-mode
RUN dotnet publish src/application/Pragma.Accounts.Application/Pragma.Accounts.Application.csproj \
    -c Release -o /app --no-restore -p:UseAppHost=false

# --- Stage 2: runtime ---
# Imagen chiseled: sin shell ni gestor de paquetes, usuario no-root (app) por defecto.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "Pragma.Accounts.Application.dll"]
