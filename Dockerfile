# Используем официальный образ .NET SDK для сборки приложения
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

USER root

RUN apt-get update \
    && apt-get install -y --allow-unauthenticated \
        libleptonica-dev \
        libtesseract-dev \
    && rm -rf /var/lib/apt/lists/*

RUN ln -s /usr/lib/x86_64-linux-gnu/libdl.so.2 /usr/lib/x86_64-linux-gnu/libdl.so

WORKDIR /app/x64

RUN ln -s /usr/lib/x86_64-linux-gnu/liblept.so.5 /app/x64/libleptonica-1.82.0.so
#RUN ln -s /usr/lib/x86_64-linux-gnu/libtesseract.so.5 /app/x64/libtesseract50.so
RUN ln -s /usr/lib/x86_64-linux-gnu/libtesseract.so.4 /app/x64/libtesseract50.so

# Clean up to reduce image size
RUN apt-get clean && rm -rf /var/lib/apt/lists/*
# Hack to Allow Tesseract to work

# Switch back to the non-root user
USER app

# Копируем файл проекта и восстанавливаем зависимости
COPY . ./
RUN dotnet restore

RUN dotnet publish -c Release -o out

# Используем официальный образ .NET Runtime для запуска приложения
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Устанавливаем Tesseract OCR и Leptonica
RUN apt-get update -q && apt-get install -y tesseract-ocr && apt-get clean

# Создаем папку для языковых моделей Tesseract
RUN mkdir -p /app/tessdata

# Копируем языковые модели (например, rus.traineddata и eng.traineddata)
COPY tessdata/* /app/tessdata/

# Копируем собранные файлы из этапа сборки
COPY --from=build-env /app/out .

# Указываем команду для запуска приложения
ENTRYPOINT ["dotnet", "ConsoleApp1.dll"]
