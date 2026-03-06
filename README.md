## Genealogy RAG Assistant

> [!NOTE]
> Created at November 2025

### Overview

This project is my attempt at Retrieval-Augmented Generation (RAG) system, specifically designed for genealogical
research. It allows users to store family tree data (individuals, families, documents, and events) into a PostgreSQL
database and uses vector embeddings to answer questions about family histories accurately.

> [!NOTE]
> As the frontend chat app uses Microsoft's Winforms, it will only work on Windows!

### Key Features

- **Genealogical Data Storage:** Core database schema configured to store individuals (`persons`), family units (
  `families`), and parent-child relationships using standard GEDCOM-style
  identifiers
- **Vector Search Integration:** Uses the PostgreSQL `pgvector` extension to store 4096-dimensional embeddings (I used
  `qwen3-embedding:8b`) for semantic search across family data
- **Automated Data Management:** Database triggers automatically cascade and delete associated RAG chunks whenever a
  person or family record is removed
- **Context-Aware AI Assistant:** Built-in logic structures prompts to ensure the AI assistant answers in the user's
  native language and relies strictly on the provided genealogical context

### Helper Scripts

This repository has two python helper scripts:

- [`ged_parser.py`](PythonUtils/ged_parser.py) - Parses .ged files to the db structure
- [`generate_embed.py`](PythonUtils/generate_embed.py) - That generates embeddings for RAG chunks

### Tech Stack

- **Database:** PostgreSQL with `pgvector` extension
- **Backend:** C# / .NET (for prompt generation and stream handling)
- **Frontend:** C# / Windows Presentation Foundation (the chat app)
- **AI:** Hosted via Ollama, accessed by its web API

### Database Schema Highlights

- `persons`: Stores individual details like name, birth/death dates, locations, and living status.
- `families` & `family_children`: Maps marriages and parent-child relationships.
- `rag_chunks` & `rag_source_types`: Stores text summaries and their vector embeddings categorized by source type (
  person, family, document, event).

### Getting Started

**1. Setup the Database**
Ensure you have PostgreSQL installed with the `pgvector` extension enabled. Use [db.sql](Database/db.sql) script to
generate the structure.

> [!TIP]
> You can use provided [docker compose](Database/docker-compose.yml) for the db!

**2. Configure the Backend**
Update your connection strings and AI model endpoints in your backend configuration to point to your local or
cloud-hosted database and embedding model.

**3. Load Data**
Import your GEDCOM files or genealogical data. The system will chunk the information, generate embeddings, and insert
them into the `rag_chunks` table.

### Usage

When a user asks a question, the system will:

1. Convert the user's question into a vector embedding
2. Use smaller, simpler AI to determine if fetch from RAG is necessary for the response
3. If so, generate embedding from the prompt
2. Query the `rag_chunks` table for the most relevant genealogical context
3. Pass the formatted context to the LLM
4. Stream back the answer to the chat app frontend

***