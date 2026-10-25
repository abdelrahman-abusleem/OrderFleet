FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:afd0ccff9d306812a69ef0f47458376b6acb544ae866cfc4b8d28ff0eef35ea7 AS build
WORKDIR /source
COPY global.json Directory.Build.props OrderFleet.slnx ./
COPY src/OrderFleet.Domain/OrderFleet.Domain.csproj src/OrderFleet.Domain/
COPY src/OrderFleet.Application/OrderFleet.Application.csproj src/OrderFleet.Application/
COPY src/OrderFleet.Infrastructure/OrderFleet.Infrastructure.csproj src/OrderFleet.Infrastructure/
COPY src/OrderFleet.Api/OrderFleet.Api.csproj src/OrderFleet.Api/
RUN dotnet restore src/OrderFleet.Api/OrderFleet.Api.csproj
COPY src/ src/
RUN dotnet publish src/OrderFleet.Api/OrderFleet.Api.csproj -c Release --no-restore -o /app/publish \
    -p:DebugType=None -p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14@sha256:d247d17dd429d96580eeb96cfce220c1089d860dbbf9f94d599ca64cf3604fe4 AS final
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
    CMD ["dotnet", "OrderFleet.Api.dll", "--health-check"]
ENTRYPOINT ["dotnet", "OrderFleet.Api.dll"]
