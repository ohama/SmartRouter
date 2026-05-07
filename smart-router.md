Create a production-quality F# project that implements an OpenAI-compatible LLM routing gateway.

# Goal

Build a smart router for Hermes Agent that automatically routes requests to different local LLMs depending on prompt complexity.

# Environment

Two local OpenAI-compatible LLM servers already exist:

* Qwen 3.5 35B

  * http://localhost:8000/v1/chat/completions

* Qwen 3.5 122B

  * http://localhost:8001/v1/chat/completions

Hermes Agent will connect only to the router.

The router must expose:

* http://localhost:4000/v1/chat/completions

using OpenAI-compatible APIs.

# Technical Requirements

Use:

* F#
* .NET 10
* ASP.NET Core Minimal API
* System.Text.Json
* HttpClientFactory
* async/Task-based concurrency

Do NOT use Python.

# Functional Requirements

The router must:

1. Accept OpenAI-compatible chat completion requests

2. Inspect prompt complexity

3. Route:

   * simple prompts → 35B
   * complex prompts → 122B

4. Return OpenAI-compatible responses unchanged

5. Support streaming=false initially

6. Add structured logging

7. Add request timing metrics

8. Add configurable routing rules

9. Be extensible for future:

   * Claude
   * OpenAI
   * DeepSeek
   * Gemini

# Complexity Routing Logic

Implement configurable heuristic routing.

Use:

* prompt length
* keyword matching
* message count
* code block detection

Complex keywords include:

* compiler
* mlir
* llvm
* f#
* type inference
* closure conversion
* optimization
* triton
* neon
* wasm
* debugging
* architecture
* recursive
* async
* performance

Large prompts should automatically use 122B.

# Project Structure

Create clean architecture:

* Program.fs
* Models/
* Services/
* Routing/
* Configuration/
* Tests/

# Required Components

Implement:

* OpenAI request/response models
* Router service
* Complexity analyzer
* LLM client service
* Configuration loading
* Logging middleware
* Error handling middleware

# Configuration

Use appsettings.json.

Example:

{
"Models": {
"FastModel": "http://localhost:8000/v1",
"SmartModel": "http://localhost:8001/v1"
},
"Routing": {
"ComplexityThreshold": 10
}
}

# Important

The router itself must remain stateless.

Avoid static mutable state.

Use dependency injection properly.

# Testing Requirements

Create full automated tests using:

* xUnit
* FsUnit
* ASP.NET Core TestServer

Implement:

1. Unit tests

   * complexity scoring
   * keyword detection
   * routing decisions

2. Integration tests

   * fake LLM servers
   * end-to-end routing

3. Load tests

   * concurrent requests
   * latency measurements

4. Failure tests

   * upstream timeout
   * malformed JSON
   * unavailable model server

# Mock Server Requirements

Create fake OpenAI-compatible mock servers for testing.

Mock servers should:

* run locally
* return deterministic responses
* simulate latency
* simulate failures

# Test Scenarios

Test:

* simple hello prompt
* short coding prompt
* large F# compiler prompt
* MLIR optimization prompt
* long debugging session
* concurrent requests
* timeout recovery

# Output Requirements

Generate:

1. Complete F# source code
2. All project files
3. .fsproj files
4. appsettings.json
5. test projects
6. README.md
7. build instructions
8. run instructions
9. curl examples
10. Hermes integration examples

# README Requirements

Explain:

* architecture
* routing logic
* adding new models
* tuning thresholds
* debugging
* performance tuning

# Extra Features

If possible implement:

* retry logic
* circuit breaker
* request queueing
* rate limiting
* health checks
* /metrics endpoint

# Performance

Optimize for:

* low latency
* high concurrency
* minimal allocations

# Important Design Constraint

The 122B model is expensive and slower.

The router should aggressively prefer 35B unless complexity strongly suggests 122B.

# Deliverables

Generate the entire project in a runnable state.
Include all code.
Avoid placeholders.
