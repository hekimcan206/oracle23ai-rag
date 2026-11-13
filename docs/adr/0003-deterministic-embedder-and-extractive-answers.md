# 0003: A deterministic lexical embedder and extractive answers instead of neural models

- Status: accepted
- Date: 2026-11-13

## Context

A RAG pipeline normally uses a neural embedding model and a generative LLM. Both are poor fits for this repository:

- The comparison is between **vector stores**. The model only has to produce the same vectors for both databases; its
  semantic quality is a separate question (answered for Turkish in the sibling `rag-tr` project).
- Numbers must be reproducible on a CI runner and on a laptop without downloading hundreds of megabytes of weights or
  depending on a GPU, and tests must be deterministic.
- The development machine shares a 7.6 GB Docker VM with Oracle Database and other workloads; a local LLM does not fit.

## Options considered

1. *Small neural embedder (e.g. a MiniLM / E5-small ONNX model).* Semantically better; adds a model download, an
   inference runtime and run-to-run numeric noise across CPUs, and does not change what the vector-store comparison
   measures.
2. *Feature hashing into sparse vectors.* Deterministic, but very sparse vectors behave unlike real embeddings in
   HNSW/IVF (many exact ties, degenerate centroids).
3. *Random-projection lexical embedder* (chosen). Every content token and adjacent token pair gets a fixed
   pseudo-random ±1 vector, seeded by a 64-bit FNV-1a hash of the feature; a text's embedding is the L2-normalised,
   sub-linearly weighted sum. The result is dense and unit length, and texts that share vocabulary cluster, which is
   the structure ANN indexes depend on.

For answers:

1. *Generative LLM with citations.* Out of scope for the same reasons; it would also make answer quality depend on a
   model rather than on retrieval.
2. *Extractive answerer* (chosen). Returns the sentence from the top three chunks that covers most of the question,
   counting the chunk's contextual header (document title and section heading), with a bonus for the expected answer
   type ("what time" → a clock time, "how many/much" → a number). It abstains when the best chunk is too far
   (cosine distance > 0.75) or the best sentence covers less than 60% of the question, and it refuses any chunk that
   lacks one of the question's capitalised name tokens.

## Decision

Use `LexicalProjectionEmbedder` (384 dimensions, model id `lexproj-384-v1-5eed0a1c2026`) and `ExtractiveAnswerer`.
The model id is recorded in `rag_meta` by both installers; installing with a different model fails instead of mixing
vectors from two models in one index. A unit test pins the first components of a fixed text's vector, so an
accidental change to the embedder (which would silently invalidate every stored vector) fails the build.

The thresholds were set on the seed-2026, 40-entity question set and are checked against a 300-question sample of the
3,700-entity benchmark corpus, which uses different entities (`docs/benchmarks.md`).

## Consequences

- Every number in the repository is reproducible bit for bit on any machine; no download, no GPU.
- Retrieval is **lexical**: a synonym counts for nothing. Recall@10 against exact search measures the vector index, not
  the embedder, and transfers to neural embeddings only qualitatively: real embeddings have different intrinsic
  dimensionality and neighbourhood structure, so absolute recall at a given `ef_search` will differ. The README states
  this next to the results.
- The answerer is a baseline, not a product: it cannot paraphrase, combine facts from two chunks, or recognise an
  entity typed in lower case. Its value here is that it makes "same retrieval, same answer" checkable across databases.
- `IEmbedder` is the seam for a neural model; the stores, schema guard and benchmark do not change.

## Sources

- Achlioptas, *Database-friendly random projections* (PODS 2001), the ±1 projection used here.
- Sahlgren, *An introduction to random indexing* (2005), the token-vector summation idea.
- The sibling project `rag-tr` (same author), for neural embedders and generation on a Turkish corpus.
