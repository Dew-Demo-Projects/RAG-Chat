BEGIN;

------------------------------------------------------------
-- Extensions
------------------------------------------------------------

-- pgvector for embeddings
CREATE EXTENSION IF NOT EXISTS vector;

------------------------------------------------------------
-- Core genealogical schema
------------------------------------------------------------

CREATE TABLE persons
(
    id             text PRIMARY KEY, -- GEDCOM individual ID, e.g. @I1@
    first_name     text,
    middle_names   text,
    last_name      text,
    display_name   text,             -- e.g. "John Michael Smith"
    gender         text,
    birth_date_raw text,
    birth_place    text,
    death_date_raw text,
    death_place    text,
    is_living      boolean,          -- TRUE = living, FALSE = deceased, NULL = unknown
    notes          text,
    extra          jsonb             -- structured extras: alt names, event notes, custom tags, etc.
);

CREATE TABLE families
(
    id                text PRIMARY KEY, -- GEDCOM family ID, e.g. @F1@
    husband_id        text REFERENCES persons (id) ON DELETE SET NULL,
    wife_id           text REFERENCES persons (id) ON DELETE SET NULL,
    marriage_date_raw text,
    marriage_place    text,
    notes             text,
    extra             jsonb
);

CREATE TABLE family_children
(
    family_id text NOT NULL REFERENCES families (id) ON DELETE CASCADE,
    child_id  text NOT NULL REFERENCES persons (id) ON DELETE CASCADE,
    PRIMARY KEY (family_id, child_id)
);

------------------------------------------------------------
-- RAG source types
------------------------------------------------------------

CREATE TABLE rag_source_types
(
    id          smallserial PRIMARY KEY,
    code        text UNIQUE NOT NULL,  -- e.g. 'person','family','document','event'
    description text
);

INSERT INTO rag_source_types (code, description) VALUES
                                                     ('person',   'Summary chunk for an individual'),
                                                     ('family',   'Summary chunk for a family unit'),
                                                     ('document', 'Transcribed document or note'),
                                                     ('event',    'Specific event description')
    ON CONFLICT (code) DO NOTHING;

------------------------------------------------------------
-- RAG chunks
------------------------------------------------------------

CREATE TABLE rag_chunks
(
    id             bigserial PRIMARY KEY,
    source_type_id smallint NOT NULL REFERENCES rag_source_types (id),
    source_id      text NOT NULL,   -- e.g. @I1@, @F3@, or document ID
    content        text NOT NULL,
    metadata       jsonb,
    embedding      vector(4096)     -- qwen3-embedding:8b dimension
);

-- Enforce the uniqueness required by the upsert logic
ALTER TABLE rag_chunks
    ADD CONSTRAINT rag_chunks_unique_source UNIQUE (source_type_id, source_id);

------------------------------------------------------------
-- Indexes
------------------------------------------------------------

-- Relationship lookup helpers
CREATE INDEX family_children_child_idx ON family_children (child_id);
CREATE INDEX families_husband_idx      ON families (husband_id);
CREATE INDEX families_wife_idx         ON families (wife_id);

-- RAG lookup
CREATE INDEX rag_chunks_source_idx
    ON rag_chunks (source_type_id, source_id);


------------------------------------------------------------
-- Triggers to cascade-delete related RAG chunks
------------------------------------------------------------

-- When a person is deleted, delete their 'person' chunks.
CREATE OR REPLACE FUNCTION delete_person_chunks()
RETURNS trigger AS $$
BEGIN
DELETE FROM rag_chunks rc
    USING rag_source_types rst
WHERE rc.source_type_id = rst.id
  AND rst.code = 'person'
  AND rc.source_id = OLD.id;

RETURN OLD;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_delete_person_chunks
    AFTER DELETE ON persons
    FOR EACH ROW
    EXECUTE FUNCTION delete_person_chunks();


-- When a family is deleted, delete their 'family' chunks.
CREATE OR REPLACE FUNCTION delete_family_chunks()
RETURNS trigger AS $$
BEGIN
DELETE FROM rag_chunks rc
    USING rag_source_types rst
WHERE rc.source_type_id = rst.id
  AND rst.code = 'family'
  AND rc.source_id = OLD.id;

RETURN OLD;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_delete_family_chunks
    AFTER DELETE ON families
    FOR EACH ROW
    EXECUTE FUNCTION delete_family_chunks();

COMMIT;
