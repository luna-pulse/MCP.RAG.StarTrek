# RAG Knowledge Base: MCP-сервер с локальной LLM и Microsoft Semantic Kernel

Поиск по русскоязычному сериалу Star Trek на C# / .NET 10. 

Солюшен включает в себя:

- проект индексатора `src/StarTrek.Indexer` — консоль, которая режет `SourceFiles` по решёткам и считает локальные эмбеддинги `intfloat/multilingual-e5-small`;
- проект MCP-сервера `src/StarTrek.Mcp` — HTTP-сервер, который отдаёт найденные чанки;
- база ChromaDB — коллекция `startrek` с векторами чанков, файлы в папке `ChromaDB`;
- данные полнотекстового поиска — корпус Okapi BM25 в `Data/bm25/corpus.json`.

Python в решении нет. Подробности индексации — в [Docs/6. Plan.md](Docs/6.%20Plan.md). Запуск индексатора в Docker — в [Docs/7. План запуска в Docker.md](Docs/7.%20План%20запуска%20в%20Docker.md). Устройство сервера — в [Docs/8. Спецификация MCP-сервера.md](Docs/8.%20Спецификация%20MCP-сервера.md).

## Инструменты MCP

```mermaid
flowchart TB
    Client["Клиент: агент"] --> Server["MCP-сервер"]
    Server --> Bm25["search_bm25\nслова запроса, Okapi BM25"]
    Server --> Semantic["search_semantic\nсмысл запроса в ChromaDB"]
    Server --> Hybrid["find_relevant_docs\nслова и смысл, слияние RRF"]
    Server --> Ask["ask_question\nответ по чанкам Corrective RAG"]
    Bm25 --> Corpus["Data/bm25/corpus.json"]
    Semantic --> Chroma["ChromaDB, коллекция startrek"]
    Hybrid --> Corpus
    Hybrid --> Chroma
```


## Проверочные факты для MCP

- Как назывался вулканский корабль первого контакта?
- Кто родители Спока и в каком году он родился?
- Сурак
- Иные примеры вопросов в файле [Docs/5. Примеры вопросов.md](Docs/5. Примеры вопросов.md)

### Пример `ask_question`

Факты из `SourceFiles/Races/Вулканцы.txt`. Вулканский корабль первого контакта — «Т'Плана-Хат» (5 апреля 2063 года, варп-полёт «Феникса»). Родители Спока — Сарек и Аманда Грейсон, Спок родился в 2230 году на Вулкане.

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "ask_question",
    "arguments": {
      "question": "Как назывался вулканский корабль первого контакта?"
    }
  }
}
```

Ответ инструмента содержит `answer`, `sources` (`sourceFile`, `position`) и `meta` (итоговый запрос и число попыток). Проверка — имя «Т'Плана-Хат» и файл `SourceFiles/Races/Вулканцы.txt`. Формулировка `answer` принадлежит модели, `position` зависит от нарезки. Модель в Compose — `qwen3.5:4b`, её скачивает сервис `ollama-pull`. Для контейнера `mcp` адрес и тег задаются `STARTREK_Llm__Endpoint=http://ollama:11434` и `STARTREK_Llm__Model=qwen3.5:4b`.

## Что нужно заранее установить

- **С Docker** — [Docker](https://www.docker.com/). При первом `docker compose up` сервис `model-pull` сам кладёт `model.onnx` в `Models/multilingual-e5-small/`.
- **Без Docker** — SDK .NET 10. Для `embed` и смыслового поиска нужен `model.onnx` (~448 МБ) в `Models/multilingual-e5-small/`: ссылка в разделе «Модель эмбеддингов». После первого `docker compose up` тот же файл уже лежит на диске.



## Запуск



### 1. Запуск индексатора

`health-check` и `help` не требуют ChromaDB; `embed` — требует (контейнер `startrek-chromadb`). `docker compose up` корпус не индексирует: у сервиса `indexer` по умолчанию `health-check`.

#### С Docker

SDK на хосте не нужен. Сборка образа `startrek-indexer`, `health-check` и запуск команд в контейнере — в [Docs/7. План запуска в Docker.md](Docs/7.%20План%20запуска%20в%20Docker.md).

#### Без Docker

Нужен SDK .NET 10.

```powershell
dotnet run --project src/StarTrek.Indexer -- <команда>
```


| Команда        | Что делает                                                                                                                                                                                                                                                                          |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `analyze`      | Только печатает отчёт: файлы, уровни заголовков, пустые блоки, разделы длиннее 480 и короче 80 токенов (`--all` — показать все, а не первые 20). Ничего не пишет.                                                                                                                   |
| `chunk`        | Режет файлы по решёткам и пишет JSON в `Data/staging/` без векторов. Файлы с тем же `content_hash` пропускает (`--force` — переделать все). В ChromaDB ничего не кладёт.                                                                                                            |
| `embed`        | Читает `Data/staging/`, считает векторы e5 ONNX, пишет (upsert) чанки в коллекцию `startrek` и пересобирает индекс BM25 (корпус сохраняется в `Data/bm25/corpus.json`). Без модели или без ответа ChromaDB останавливается, уже записанное не трогает. `--force` — пересчитать всё. |
| `health-check` | Печатает `Индексатор отвечает: healthy.` и возвращает код 0. Не читает корпус, не грузит ONNX и не вызывает ChromaDB.                                                                                                                                                               |
| `help`         | Печатает список команд и завершается с кодом 0.                                                                                                                                                                                                                                     |




### 2. Запуск MCP-сервера

Сервис `mcp` — образ и контейнер `startrek-mcp`, порт `8080`. Сборка и запуск:

```powershell
docker compose build mcp
docker compose up -d chromadb ollama ollama-pull model-pull mcp
```

`docker compose up` по-прежнему не вызывает `analyze`, `chunk` и `embed`. Сервис `indexer` выполняет `health-check` и завершается. Сервис `mcp` остаётся слушать HTTP.

Адрес клиента: `http://127.0.0.1:8080/mcp`.

Проверка процесса: `GET http://127.0.0.1:8080/health`. Ответ — код 200 и тело `MCP-сервер отвечает: healthy.` Этот запрос не читает корпус BM25, не грузит ONNX и не вызывает ChromaDB: строка значит, что процесс слушает, а не что индекс на месте. Проверка успешна и без корпуса. К старту `mcp` файл `model.onnx` уже скачан сервисом `model-pull`; сам запрос `/health` его не открывает.

Имена инструментов и куда они ходят — на схеме «Инструменты MCP» в начале файла.

Пример подключения — файл `mcp.example.json` в корне репозитория. Скопируйте его в `.vscode/mcp.json` для VS Code Copilot. Каталог `.vscode/` в git не входит. В примере адрес HTTP, не команда STDIO. 

## Модель эмбеддингов

`tokenizer.json` лежит в `Models/multilingual-e5-small/` и хранится в git. Это копия модели [intfloat/multilingual-e5-small](https://huggingface.co/intfloat/multilingual-e5-small). Откуда скачаны файлы и на каких правах они используются — в [Models/LICENSE.md](Models/LICENSE.md).

`Models/multilingual-e5-small/model.onnx` в git не входит: файл весит около 448 МБ и в образ Docker не копируется. При `docker compose up` сервис `model-pull` скачивает его в `Models/multilingual-e5-small/model.onnx`, если файла ещё нет или он слишком мал. После этого тот же файл на диске используют локальные `dotnet run` и `embed`.

Без Docker скачайте файл сами и сохраните по тому же пути:

[https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/onnx/model.onnx](https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/onnx/model.onnx)

`analyze`, `chunk` и `health-check` работают без `model.onnx`: `chunk` нужен только `tokenizer.json` (подсчёт токенов), сам файл весов не вызывается.

При индексации E5 получает служебный префикс `passage:` , а при поисковом запросе — `query:` . В ChromaDB хранится текст без этого префикса. Эмбеддинги вычисляются локально, платный API не используется.

## ChromaDB

```powershell
docker compose up        # или: docker compose up -d
```

Compose поднимает хранилище, индексатор, MCP и локальную модель. `docker compose up` корпус не индексирует:

- `chromadb` — образ `chromadb/chroma:1.5.9`, контейнер `startrek-chromadb`, порт `8000`, том `./ChromaDB:/data`;
- `indexer` — образ и контейнер `startrek-indexer`. По умолчанию выполняет только `health-check` и сразу завершается;
- `ollama` — образ `ollama/ollama:0.40.6`, контейнер `startrek-ollama`, порт `11434`, том `ollama-models`;
- `ollama-pull` — один раз выполняет `ollama pull qwen3.5:4b` и завершается;
- `model-pull` — образ `curlimages/curl:8.23.0`, контейнер `startrek-model-pull`. Если `model.onnx` ещё нет или файл слишком мал, скачивает его в `Models/multilingual-e5-small/` и завершается;
- `mcp` — образ и контейнер `startrek-mcp`, порт `8080`. Слушает HTTP и отдаёт три инструмента поиска и `ask_question`. В контейнере `STARTREK_Llm__Endpoint=http://ollama:11434` и `STARTREK_Llm__Model=qwen3.5:4b`. Сборка и адрес клиента — в разделе «2. Запуск MCP-сервера».

**Healthcheck.** У `ollama` проверка через `ollama list`: команда обращается к самому серверу внутри контейнера, поэтому сервис считается готовым, только когда реально принимает запросы (`ollama --version` прошла бы раньше, а `curl`/`wget` в образе может не быть).

**Порядок запуска.** `model-pull` не ждёт остальные сервисы: проверяет файл на диске и при необходимости скачивает `model.onnx`. `ollama-pull` ждёт, пока `ollama` станет healthy, и один раз скачивает `qwen3.5:4b`. `mcp` стартует только после того, как `ollama` healthy, а `ollama-pull` и `model-pull` успешно завершились (`service_completed_successfully`). Том модели у `mcp` остаётся только для чтения. `indexer` скачивание модели не ждёт. `chromadb` — `service_started`.

Локальный доступ к хранилищу:

- `http://127.0.0.1:8000` — локальный API ChromaDB на этом компьютере, а не сайт в интернете. Консоль отправляет в него команды «запиши чанк» и «найди ближайшие».
- Папка [ChromaDB](ChromaDB) — файлы хранилища, их пишет контейнер. C#-пакет эту папку напрямую не открывает: запись идёт только через API.

Консоль контейнер сама не запускает. Если API не отвечает, `embed` останавливается с сообщением, что ChromaDB не запущена.

## Схема

```mermaid
flowchart LR
  sourceFiles[SourceFiles] --> analyze[analyze]
  sourceFiles --> chunk[chunk]
  chunk --> staging[stagingJson]
  onnx[e5small_ONNX] --> embed[embed]
  staging --> embed
  embed --> chroma[ChromaDB]
  embed --> bm25[Bm25Corpus]
  bm25 --> mcp[McpServer]
  chroma --> mcp
  onnx --> mcp
  mcp --> client[Клиент MCP]
```





## Конфигурация

Всё настраивается в [src/StarTrek.Indexer/appsettings.json](src/StarTrek.Indexer/appsettings.json), а не в коде (отдельные значения можно переопределить переменными окружения с префиксом `STARTREK_`):

- `Indexer:Paths` — корпус, `Data/staging`, папка модели, файл корпуса BM25;
- `Indexer:Chroma` — адрес (`http://127.0.0.1:8000` локально; в Compose — `http://chromadb:8000`) и коллекция `startrek`;
- `Indexer:Chunking` — пороги 480 / 80 токенов и параметры переноса между кусками;
- `Indexer:Bm25` — `K1 = 1.5`, `B = 0.75`;
- `Indexer:Search` — параметры гибридного поиска (RRF);
- `Llm` — локальная модель Ollama для переписывания, расширения запроса и ответа `ask_question`: адрес, тег модели, температура и таймаут;
- `Rag` — сколько релевантных чанков достаточно (`MinRelevantChunks`), сколько раз расширять запрос (`MaxBroadenAttempts`) и временный порог оценки RRF (`MinRrfScore`).

Адрес, путь `/mcp` и журнал сервера — в [src/StarTrek.Mcp/appsettings.json](src/StarTrek.Mcp/appsettings.json). Пороги поиска сервер читает из файла индексатора. Переопределение — те же переменные `STARTREK_`.

Папки `Data/` и `ChromaDB/` хранятся в репозитории. Из `Models/` в git входит `tokenizer.json`. `model.onnx` в git нет: при первом `docker compose up` его скачивает `model-pull` в `Models/multilingual-e5-small/`.

## Поиск

Командной строки для поиска нет. Готовый индекс читает MCP-сервер:

- `search_bm25` — слова запроса в индексе Okapi BM25;
- `search_semantic` — смысл запроса в коллекции `startrek`;
- `find_relevant_docs` — оба списка, слияние RRF в `HybridSearchService`.

Три инструмента поиска возвращают чанки. `ask_question` пишет ответ по чанкам, которые цикл счёл релевантными.

Три поиска передают в индекс фразу аргумента `query`. Вход `CorrectiveRagRetrieval` сначала просит Ollama сжать вопрос до поискового запроса и ищет его через `HybridSearchService`. Если релевантных чанков меньше `Rag:MinRelevantChunks` (по умолчанию 2), запрос расширяется и поиск повторяется, не больше `Rag:MaxBroadenAttempts` раз (по умолчанию 2). Пока оценку чанка не делает модель, релевантным считается чанк с оценкой RRF не ниже `Rag:MinRrfScore`. Одинаковые чанки из разных попыток склеиваются, остаётся большая оценка. Модель по умолчанию — `qwen3.5:4b`, адрес — `http://localhost:11434` (секция `Llm` в `src/StarTrek.Indexer/appsettings.json`). Перед вызовом Ollama должна быть запущена, а модель скачана: `ollama pull qwen3.5:4b`. Другой тег задаётся полем `Llm:Model` или переменной `STARTREK_Llm__Model`. Если модель молчит, возвращает пустой текст или не укладывается в `Llm:TimeoutSeconds`, поиск идёт по исходному или предыдущему запросу. `ask_question` вызывает этот же вход и затем просит модель написать ответ. Если чанков с текстом выше порога не осталось, модель для ответа не вызывается и возвращается фиксированная фраза, что в базе знаний ничего не найдено.

## Тесты

```powershell
dotnet test
```

Проверяются `Tests/StarTrek.Indexer.Tests` и `Tests/StarTrek.Mcp.Tests`. Интеграционные тесты с живой ChromaDB пропускаются, если `http://127.0.0.1:8000` не отвечает.