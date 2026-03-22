FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY CargoWiseForesight.sln ./
COPY src/CargoWise.Foresight.Core/CargoWise.Foresight.Core.csproj src/CargoWise.Foresight.Core/
COPY src/CargoWise.Foresight.Api/CargoWise.Foresight.Api.csproj src/CargoWise.Foresight.Api/
COPY src/CargoWise.Foresight.Llm.Ollama/CargoWise.Foresight.Llm.Ollama.csproj src/CargoWise.Foresight.Llm.Ollama/
COPY src/CargoWise.Foresight.Data.Mock/CargoWise.Foresight.Data.Mock.csproj src/CargoWise.Foresight.Data.Mock/
COPY tests/CargoWise.Foresight.Tests/CargoWise.Foresight.Tests.csproj tests/CargoWise.Foresight.Tests/

RUN dotnet restore

COPY . .
RUN dotnet publish src/CargoWise.Foresight.Api/CargoWise.Foresight.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5248
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5248

ENTRYPOINT ["dotnet", "CargoWise.Foresight.Api.dll"]
