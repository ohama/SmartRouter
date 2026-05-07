# Prompt: Build a Graphify Smart Router in F#

You are an expert F# distributed systems engineer and LLM infrastructure architect.

Your task is to design and implement a production-grade Smart Router for Graphify-based local LLM environments.

The system must intelligently route requests between:

- Qwen 3.5 35B
- Qwen 3.5 122B

running as OpenAI-compatible local endpoints.

The implementation language must be:

- F#
- .NET 9
- ASP.NET Core Minimal API

The router itself must expose:

```text
/v1/chat/completions
```

compatible with OpenAI APIs.

The router will sit between:

```text
Graphify
    ↓
Smart Router (F#)
    ↓
35B / 122B
```

The router must analyze requests and decide which model to use.

---

# Main Goals

The router must:

1. Support OpenAI-compatible APIs
2. Proxy requests to backend local LLMs
3. Route based on task type
4. Handle concurrency safely
5. Prevent 122B overload
6. Support streaming responses
7. Be extensible for future models
8. Provide observability/logging
9. Be production-quality
10. Be optimized for coding/Graph RAG/compiler workloads

---

# Environment

Assume the following environment:

## Qwen 35B

```text
http://localhost:8000/v1/chat/completions
```

Purpose:

- fast retrieval
- summaries
- quick Q&A
- routing
- lightweight graph lookup

---

## Qwen 122B

```text
http://localhost:8001/v1/chat/completions
```

Purpose:

- graph indexing
- relation extraction
- compiler reasoning
- architecture analysis
- deep debugging
- multi-file reasoning
- recursive dependency analysis

---

# Router Requirements

# 1. OpenAI-Compatible API

The router must implement:

```http
POST /v1/chat/completions
```

Compatible with:

- Graphify
- Hermes
- Claude Code
- OpenAI SDKs

---

# 2. Request Parsing

Support:

- messages
- model
- stream
- temperature
- top_p
- max_tokens

Preserve unknown fields when proxying.

---

# 3. Routing Logic

The router must support:

## A. Explicit Task Routing

Requests may contain:

```json
{
  "task": "graph_indexing"
}
```

Examples:

- graph_indexing
- retrieval
- summary
- reasoning
- compiler_debug
- architecture_analysis
- dependency_analysis

---

## B. Heuristic Routing

If no explicit task exists, infer routing using:

- keywords
- system prompts
- message content
- context size
- token size

Examples of keywords requiring 122B:

- recursive
- dependency
- lowering
- MLIR
- LLVM
- compiler
- architecture
- type inference
- graph relation
- closure conversion
- cross-file
- multi-file

---

## C. Context-Based Routing

Large contexts should prefer 122B.

---

# 4. Concurrency Control

IMPORTANT.

The router must protect the 122B model.

Implement:

```text
SemaphoreSlim(1)
```

for heavy 122B requests.

Only allow:

```text
1 concurrent heavy reasoning request
```

to 122B.

35B may allow higher concurrency.

---

# 5. Streaming Support

The router must support:

```json
{
  "stream": true
}
```

Requirements:

- proxy SSE streams
- preserve chunk ordering
- avoid buffering entire response
- support cancellation

---

# 6. Timeout + Retry

Implement:

- request timeout
- cancellation token support
- retry policy
- backend health detection

---

# 7. Health Endpoints

Implement:

```text
/health
/models
/stats
```

---

# 8. Logging

Use structured logging.

Include:

- selected model
- routing reason
- latency
- token count
- backend status
- queue wait time

---

# 9. Metrics

Track:

- requests/sec
- active requests
- 122B queue size
- average latency
- failures
- streaming duration

---

# 10. Future Extensibility

The design must support future models:

- DeepSeek
- Gemma
- Llama
- Claude proxy
- OpenAI cloud

Use clean abstractions.

---

# F# Technical Requirements

Use:

- ASP.NET Core Minimal API
- HttpClientFactory
- async/await
- Task
- CancellationToken
- Channels or TPL Dataflow if useful
- immutable records where appropriate

Avoid:

- unnecessary OOP complexity
- reflection-heavy designs

Favor:

- functional architecture
- composable routing rules
- clean separation of concerns

---

# Required Output

Generate:

1. Complete architecture explanation
2. Directory structure
3. F# source code
4. Routing engine implementation
5. Streaming proxy implementation
6. Concurrency limiter implementation
7. Health endpoints
8. Logging strategy
9. Example configuration
10. Dockerfile
11. docker-compose example
12. Testing strategy
13. Load testing strategy
14. Benchmarking strategy
15. Future extension strategy

---

# Important Design Goals

This router is specifically intended for:

- Graphify
- compiler projects
- MLIR/LLVM reasoning
- graph RAG
- large codebases
- local LLM orchestration

Optimize accordingly.

---

# Additional Requirement

The implementation must explain:

- WHY each design decision was made
- WHY task-based routing is better than prompt-length routing
- WHY 122B concurrency must be protected
- WHY graph indexing should prefer 122B
- HOW to avoid latency explosions

---

# Advanced Requirement

Add support for:

## Priority Queue

Example:

```text
graph indexing
    priority = high

quick summary
    priority = low
```

---

# Advanced Requirement 2

Implement fallback logic:

```text
122B unavailable
    ↓
fallback to 35B
```

except for:

```text
graph indexing
```

which must fail instead.

---

# Advanced Requirement 3

Implement queue monitoring.

Expose:

```text
/stats
```

including:

- queue size
- waiting requests
- active model
- average wait time

---

# Testing

Include:

- unit tests
- integration tests
- streaming tests
- concurrency tests
- load tests

Use:

- Expecto
- xUnit

where appropriate.

---

# Final Goal

Produce production-quality F# code suitable for:

- Mac M4 local LLM environments
- Graphify integration
- Hermes integration
- compiler-scale graph reasoning systems
- future multi-model orchestration
