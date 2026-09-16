# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so the dependency layer is cached independently of source changes.
COPY Directory.Build.props ./
COPY src/NovaWallet.Domain/NovaWallet.Domain.csproj src/NovaWallet.Domain/
COPY src/NovaWallet.Application/NovaWallet.Application.csproj src/NovaWallet.Application/
COPY src/NovaWallet.Infrastructure/NovaWallet.Infrastructure.csproj src/NovaWallet.Infrastructure/
COPY src/NovaWallet.Api/NovaWallet.Api.csproj src/NovaWallet.Api/
RUN dotnet restore src/NovaWallet.Api/NovaWallet.Api.csproj

COPY src/ src/
RUN dotnet publish src/NovaWallet.Api/NovaWallet.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
COPY --from=build /app ./
# Run as the non-root user that ships with the .NET 8 images.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "NovaWallet.Api.dll"]
