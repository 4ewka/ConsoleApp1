# Используем официальный образ .NET SDK для сборки приложения
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app

# Копируем файл проекта и восстанавливаем зависимости
COPY *.csproj ./
RUN dotnet restore

# Копируем все файлы и собираем приложение
COPY . ./
RUN dotnet publish -c Release -o out

# Используем официальный образ .NET Runtime для запуска приложения
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Устанавливаем Tesseract OCR и Leptonica
RUN apt-get update -q && apt-get install -y tesseract-ocr libtesseract-dev libleptonica-dev && apt-get clean

# Создаем папку для языковых моделей Tesseract
RUN mkdir -p /app/tessdata

# Копируем языковые модели (например, rus.traineddata и eng.traineddata)
COPY tessdata/* /app/tessdata/

# Копируем собранные файлы из этапа сборки
COPY --from=build-env /app/out .

# Указываем команду для запуска приложения
ENTRYPOINT ["dotnet", "ConsoleApp1.dll"]
