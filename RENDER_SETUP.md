# Развёртывание на Render.com

## 1. Подготовка

1. Создайте репозиторий на GitHub
2. Залейте код туда
3. Создайте аккаунт на [render.com](https://render.com)

## 2. Создание Web Service

1. **New +** → **Web Service**
2. **Connect repository** → выберите ваш репозиторий
3. **Name**: `chosh-informator` (или другое)
4. **Region**: выберите ближайший (например, Frankfurt)
5. **Branch**: `main`

## 3. Build & Start Commands

**Build Command:**
```bash
dotnet build --configuration Release
```

**Start Command:**
```bash
cd bin/Release/net8.0 && dotnet TestApp.dll
```

## 4. Environment Variables

Добавьте в **Environment**:

```
Telegram__ApiId=12345678
Telegram__ApiHash=ваш_api_hash
Telegram__PhoneNumber=+77076298774
Telegram__Password2Fa=ваш_пароль_2fa

Ai__AiApiKey=sk-ваш_ключ
Ai__AiModelName=gpt-3.5-turbo
Ai__AiBaseUrl=https://api.openai.com/v1/

ConnectionStrings__DefaultConnection=Data Source=userbot.db
```

## 5. Plan

- **Plan**: Free или Starter (в зависимости от нагрузки)
- **Auto-Deploy**: ✅ включить
- **Keep Alive**: можно включить Cron Job для пинга (опционально)

## 6. Запуск

1. Нажмите **Create Web Service**
2. Ждите деплоя (обычно 5-10 минут)
3. При первом запуске потребуется авторизация в Telegram
4. Следите за логами в **Logs** на Render

## ⚠️ Важные замечания

- На Free плане сервис спит после 15 минут неактивности
- Для постоянной работы используйте **Paid Plans** (минимум $12/месяц)
- База данных (userbot.db) хранится **локально** на сервере
- При пересоздании инстанса база потеряется → используйте **PostgreSQL** для production

## 🔧 PostgreSQL вместо SQLite (для Production)

Если нужна постоянная база:

1. Добавьте PostgreSQL в Render
2. Обновите `AppDbContext.cs`:
```csharp
options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
```
3. Установите NuGet пакет: `Npgsql.EntityFrameworkCore.PostgreSQL`
4. Обновите connection string на Render

## 📝 Мониторинг

Следите за логами на Render:
- **Successful messages** → ✅ зелёные логи
- **Errors** → ❌ красные логи
- **FLOOD_WAIT** → ⚠️ жёлтые логи

## 🔄 Updates

Для обновления кода:
1. Пушьте изменения в GitHub
2. Render автоматически пересоберёт и перезапустит

---

**Готово!** Теперь ваш юзербот работает в облаке 🚀
