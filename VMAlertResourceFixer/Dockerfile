FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY VMAlertResourceFixer.csproj ./
RUN dotnet restore VMAlertResourceFixer.csproj

COPY . ./
RUN dotnet publish VMAlertResourceFixer.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "VMAlertResourceFixer.dll"]