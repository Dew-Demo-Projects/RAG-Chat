import time

import psycopg2
import requests

DB_DSN = "host=192.168.233.52 port=5432 dbname=genealogy user=admin password=A232cb1"
OLLAMA_BASE_URL = "http://192.168.107.37:11434"
EMBED_MODEL = "qwen3-embedding:8b"


def get_embedding(text: str) -> list[float]:
    url = f"{OLLAMA_BASE_URL}/api/embeddings"
    payload = {
        "model": EMBED_MODEL,
        "prompt": text,
    }
    resp = requests.post(url, json=payload, timeout=60)
    resp.raise_for_status()
    data = resp.json()
    emb = data.get("embedding")
    if not emb or not isinstance(emb, list):
        raise RuntimeError(f"Bad embedding response: {data}")
    return emb


def main():
    conn = psycopg2.connect(DB_DSN)
    conn.autocommit = False

    try:
        with conn.cursor() as cur:
            # Fetch all chunks without embeddings
            cur.execute(
                """
                SELECT id, content
                FROM rag_chunks
                WHERE embedding IS NULL
                ORDER BY id;
                """
            )
            rows = cur.fetchall()

        print(f"Found {len(rows)} chunks without embeddings")

        with conn.cursor() as cur:
            for idx, (chunk_id, content) in enumerate(rows, start=1):
                # Get embedding from Ollama
                emb = get_embedding(content)

                # Optional: basic dimension sanity check
                if len(emb) != 4096:
                    raise RuntimeError(
                        f"Chunk {chunk_id} returned {len(emb)}-dim embedding, expected 4096"
                    )

                # Update embedding in DB; psycopg2 maps Python list[float] to 'vector' via pgvector
                cur.execute(
                    """
                    UPDATE rag_chunks
                    SET embedding = %s
                    WHERE id = %s;
                    """,
                    (emb, chunk_id),
                )

                if idx % 10 == 0:
                    conn.commit()
                    print(f"Updated {idx} chunks...")

                # Gentle sleep to avoid hammering Ollama
                time.sleep(0.1)

            conn.commit()
            print("Done, all embeddings backfilled.")

    finally:
        conn.close()


if __name__ == "__main__":
    main()
