FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /App

COPY *.csproj ./
COPY nuget.config ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release --property:PublishDir=/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 as base
COPY --from=build-env /publish /app
WORKDIR /app
EXPOSE 8080
ENTRYPOINT ["dotnet", "AutoDispatchService.dll"]
