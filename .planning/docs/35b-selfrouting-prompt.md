# Designing a SAFE-for-35B Router Prompt (Qwen2.5-Coder-7B)

## Goal

Use `Qwen2.5-Coder-7B` as a lightweight semantic router for:

- Qwen 35B
- Qwen 122B
- Hermes Agent

The router should decide:

```text
Can this request be safely handled by 35B?
```

NOT:

```text
Is this request simple?
```

This distinction is extremely important.

---

# 1. Core Philosophy

The router should NOT try to detect all complex tasks.

Instead:

```text
Detect only tasks that are clearly SAFE for 35B.
```

This minimizes:

```text
False Simple
(complex request incorrectly routed to 35B)
```

which is the most dangerous failure mode.

---

# 2. Why "SAFE" Works Better Than "SIMPLE"

Avoid prompts like:

```text
Is this task simple?
```

because:

- ambiguous
- subjective
- encourages reasoning
- unstable classification

Instead use:

```text
SAFE for a fast 35B model
```

This produces:

- more deterministic outputs
- cleaner classification
- lower false-simple rate

---

# 3. Recommended Router Prompt

```text
You are a routing classifier.

Your task is to determine whether a request is SAFE
for a fast 35B coding model.

A request is SAFE if:
- it requires only shallow reasoning
- no difficult debugging
- no architecture design
- no compiler expertise
- no deep continuation context
- no optimization reasoning
- no multi-step planning

SAFE examples:
- formatting
- summaries
- boilerplate generation
- simple explanations
- basic code snippets
- YAML/JSON generation
- translation
- documentation cleanup

UNSAFE examples:
- LLVM
- MLIR
- compiler bugs
- debugging
- optimization
- concurrency
- type inference
- closure lowering
- architecture redesign
- retry/fix/continue workflows

Return ONLY valid JSON:

{
  "route": "35b" | "122b",
  "confidence": 0.0-1.0,
  "reason": "short reason"
}
```

---

# 4. Why This Prompt Works Well

The key concept is:

```text
SAFE FOR 35B
```

instead of:

```text
simple vs complex
```

This turns the problem into a stable binary classification task.

Benefits:

- deterministic routing
- concise output
- low reasoning overhead
- better semantic gating

---

# 5. Important Prompt Design Principles

## Prefer

| Expression | Recommendation |
|---|---|
| SAFE for 35B | Strongly recommended |
| suitable for fast model | Recommended |
| low-risk for small model | Recommended |

---

## Avoid

| Expression | Reason |
|---|---|
| simple | ambiguous |
| easy | subjective |
| complex | too broad |

---

# 6. Importance of UNSAFE Examples

UNSAFE examples are extremely important.

For compiler-heavy workflows, strongly include:

```text
LLVM
MLIR
compiler
debugging
optimization
concurrency
type inference
lowering
closure
segfault
```

These keywords dramatically improve routing quality.

---

# 7. Continuation-Aware Routing

Very important.

Short prompts may still be highly complex.

Examples:

```text
continue
retry
fix this
keep going
```

These usually imply:

- hidden reasoning state
- previous failures
- deep debugging chains
- architectural continuity

Therefore include:

```text
retry/fix/continue workflows
```

inside UNSAFE examples.

---

# 8. Recommended Inference Settings

Recommended router settings:

| Setting | Recommended Value |
|---|---|
| temperature | 0.0 |
| top_p | 0.1 |
| max_tokens | 16~32 |
| stream | false |

---

# 9. Force JSON-only Output

Very important.

Always include:

```text
Return ONLY valid JSON
```

This improves:

- parsing stability
- deterministic outputs
- integration reliability

---

# 10. Example Output

```json
{
  "route": "122b",
  "confidence": 0.98,
  "reason": "compiler debugging"
}
```

---

# 11. Recommended Architecture

```text
Hard Rules
    ↓
Qwen2.5-Coder-7B Router
    ↓
35B / 122B
```

---

# 12. Recommended Hard Rules

Immediately route to 122B if request contains:

```text
LLVM
MLIR
compiler
segfault
optimization
concurrency
```

This prevents catastrophic misrouting.

---

# 13. Why Hard Rules Matter

Router models may become overconfident.

Example failure:

```text
"I can probably solve this."
```

even when:

- debugging is deep
- reasoning is difficult
- architecture is fragile

Hard rules prevent these failures.

---

# 14. Recommended Final Strategy

For production-grade routing:

```text
Hard Rules
+
SAFE-for-35B classifier
+
JSON-only output
+
low max_tokens
+
sticky continuation escalation
```

This combination provides the best balance of:

- latency
- routing stability
- semantic accuracy
- continuation coherence
- debugging reliability

---

# 15. Final Key Insight

The router is NOT:

```text
a mini assistant
```

It is:

```text
a semantic safety classifier
```

The goal is NOT:

```text
save 122B usage
```

The goal IS:

```text
send only clearly safe tasks to 35B
```

This mindset produces the most stable production behavior.