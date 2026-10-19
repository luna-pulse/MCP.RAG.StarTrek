# AGENTS.md

Инструкции для агентов в репозитории MCP.RAG.StarTrek. Подробности запуска — в [README.md](README.md), обоснования — в [Docs/6. Plan.md](Docs/6.%20Plan.md).

## Проект

Консольный индексатор корпуса `SourceFiles` (Star Trek, русский язык). Нарезка по заголовкам с решётками `#`, локальные эмбеддинги `intfloat/multilingual-e5-small` (ONNX, 384 измерения), коллекция ChromaDB `startrek` и индекс BM25. Гибридный поиск (RRF) — внутренний API; командной строки поиска нет. MCP-сервер ещё не реализован.

## Стек

- Язык и рантайм: C# / .NET 10 (`global.json`, `net10.0`). Солюшен `MCP.RAG.StarTrek.sln`, проект `src/StarTrek.Indexer`, тесты `Tests/StarTrek.Indexer.Tests` (xUnit).
- Python, LangChain и LangGraph в решение не добавлять. Гибридный поиск оркестрирует Microsoft Semantic Kernel: плагин `Search`, вызовы `kernel.InvokeAsync`.
- Платный API эмбеддингов не использовать. Генеративная LLM для нарезки не нужна: границы чанков уже стоят решётками в файлах.
- Родительские и дочерние чанки не делать. Один чанк и для поиска, и для ответа.

## Команды

```powershell
dotnet test
dotnet run --project src/StarTrek.Indexer -- <команда>
```

| Команда | Что делает |
|---|---|
| `analyze` | Печатает отчёт по корпусу. Ничего не пишет. |
| `chunk` | Режет по решёткам и пишет JSON в `Data/staging/`. В ChromaDB не пишет. |
| `embed` | Считает векторы и делает upsert в коллекцию `startrek`, пересобирает BM25. Нужны `model.onnx` и живой ChromaDB. |
| `health-check` | Печатает `Индексатор отвечает: healthy.` Код 0. Корпус, ONNX и ChromaDB не трогает. |
| `help` | Список команд, код 0. |

`analyze`, `chunk` и `health-check` работают без `model.onnx`. `chunk` читает только `tokenizer.json`.

`embed` и `embed --force` запускать только по явной просьбе: команда пишет в ChromaDB и `Data/bm25/`. Если модели нет или API не отвечает, команда останавливается и уже записанное не трогает.

`docker compose up` корпус не индексирует. Сервис `indexer` по умолчанию выполняет `health-check`.

## Конфигурация

Пороги, пути, адрес ChromaDB и параметры BM25/RRF — в `src/StarTrek.Indexer/appsettings.json`, не в коде. Переопределение — переменные окружения с префиксом `STARTREK_` (в Compose: `STARTREK_Indexer__Chroma__Endpoint`).

Нарезка: потолок 480 токенов, короткий раздел — меньше 80, окно модели — 512. BM25: `K1 = 1.5`, `B = 0.75`. Короткие разделы не склеивать через границу решёток: соседние корабли и факты должны остаться разными чанками.

В эмбеддинг уходит префикс `passage: `, в запрос — `query: `. В ChromaDB хранится текст без этого префикса и без символов `#`.

## Папки

Новая папка — CamelCase с заглавной буквы: `Data`, `Tests`, `SourceFiles`, `Docs`. Не `data`, `tests`, `source-files`.

Том `./ChromaDB:/data` в Docker Compose не переименовывать: `/data` ожидает образ ChromaDB.

`Data/` и `ChromaDB/` — данные индекса, они в git. Не очищать и не переписывать их целиком без просьбы. C# пишет в ChromaDB только через HTTP API (`http://127.0.0.1:8000` локально, `http://chromadb:8000` в Compose), папку `ChromaDB` напрямую не открывает.

`Models/multilingual-e5-small/model.onnx` (~448 МБ) в git не входит (см. `.gitignore`). `tokenizer.json` в git есть. Файл весов не коммитить.

## Код

- `nullable` и неявные `using` включены. Пространства имён — `StarTrek.Indexer.*`, тесты — `StarTrek.Indexer.Tests`.
- Идентификаторы в коде — на английском. Сообщения консоли, документы в `Docs/` и комментарии к пользовательским сценариям — на русском.
- Текст корпуса не переписывать: имена, номера кораблей и коды серий сохранять. Нормализация — Unicode NFC и переводы строк `\n`.
- Три диалекта заголовков в `SourceFiles`: расы (строка из одних `####`, заголовок следующей строкой), планеты и сериалы (Markdown `#` на той же строке), корабли (`#` реестр, `##` класс, `###` корабль).

## Тесты

`dotnet test` — основная проверка. Интеграционные тесты с живой ChromaDB (`ChromaServerFact`) пропускаются, если `http://127.0.0.1:8000` не отвечает; для них нужен `docker compose up -d`. Остальные тесты Chroma ходят в `FakeChromaServer` и от Docker не зависят.
