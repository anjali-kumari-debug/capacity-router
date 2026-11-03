# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/CapacityRouter.Api/CapacityRouter.Api.csproj src/CapacityRouter.Api/
RUN dotnet restore src/CapacityRouter.Api/CapacityRouter.Api.csproj
COPY src/ src/
RUN dotnet publish src/CapacityRouter.Api/CapacityRouter.Api.csproj -c Release -o /app --no-restore

# Runtime stage: small image, runs as the built-in non-root user
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "CapacityRouter.Api.dll"]
