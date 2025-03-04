# Используем официальный образ .NET SDK для сборки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app

# Копируем файл решения и восстанавливаем зависимости
COPY *.sln ./
COPY ConsoleApp1/*.csproj ./ConsoleApp1/
RUN dotnet restore

# Копируем все файлы и собираем проект
COPY . ./
RUN dotnet publish -c Release -o out

# Используем runtime-образ
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Устанавливаем Tesseract и его зависимости
RUN apt-get update && apt-get install -y \
    tesseract-ocr \
    tesseract-ocr-rus \
    libtesseract-dev \
    libleptonica-dev \
    && apt-get clean

# Проверяем наличие Leptonica
RUN ldconfig && ldconfig -p | grep leptonica

# Копируем собранный проект
COPY --from=build-env /app/out .

# Копируем папку tessdata
COPY --from=build-env /app/tessdata /app/tessdata

# Указываем путь к tessdata
ENV TESSDATA_PREFIX=/app/tessdata

# Запускаем приложение
ENTRYPOINT ["dotnet", "ConsoleApp1.dll"]
