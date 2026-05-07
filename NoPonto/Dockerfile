# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /src

# Copy csproj and restore as distinct layers
COPY ["NoPonto.csproj", "./"]
RUN dotnet restore "NoPonto.csproj"

# Copy the remaining source and publish
COPY . .
RUN dotnet publish "NoPonto.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "NoPonto.dll"]
