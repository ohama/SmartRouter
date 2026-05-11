# Practical Router Design for Hermes Agent on Mac (35B / 122B)

## Goal

Design a practical smart routing system for:

- Hermes Agent
- Qwen 35B
- Qwen 122B

running locally on a Mac system (e.g. Mac M4 128GB).

The key question:

```text
Should we run a separate 7B router server?
```

Short answer:

```text
Not necessarily.
```

In many real-world local setups:

```text
Hard Rules + 35B Self-Routing
```

is the best balance.

---

# 1. Key Philosophy

The router should be:

```text
as lightweight as possible
```

The routing layer itself should NOT become:

- another heavy inference server
- another large KV cache consumer
- another scheduling problem

---

# 2. Recommended Options

| Strategy | Recommendation |
|---|---|
| 35B self-router | Highly recommended |
| Embedded tiny classifier | Highly recommended |
| Separate 7B router server | Recommended |
| Pure heuristic-only routing | Supplemental only |

---

# 3. Most Practical Strategy
# 35B Self-Routing

Recommended architecture:

```text
Hard Rules
    ↓
35B tiny routing prompt
    ↓
35B continues
or
122B escalation
```

Flow:

```text
35B:
"Can I safely handle this request?"
```

If yes:

```text
35B continues
```

If no:

```text
Escalate to 122B
```

---

# 4. Why This Works Well on Mac

Benefits:

```text
No additional server
No additional GPU memory
No additional KV cache pressure
No additional scheduler complexity
```

This is especially important when already running:

- 35B
- 122B

simultaneously on a local machine.

---

# 5. Why a Dedicated 7B Router Was Originally Recommended

Dedicated router models are often:

- more deterministic
- more concise
- better at JSON formatting
- more stable classifiers

Example:

```text
Qwen2.5-Coder-7B
```

works very well as a semantic router.

However:

```text
practical local deployment constraints
```

matter.

---

# 6. Hard Rules Are Extremely Important

Some requests should bypass routing entirely.

Recommended immediate-122B triggers:

```text
LLVM
MLIR
compiler
segfault
optimization
concurrency
```

This dramatically reduces router failure risk.

---

# 7. Why Hard Rules Matter

Without hard rules:

```text
35B may become overconfident
```

Example:

```text
"I can probably solve this."
```

even when:

- debugging is deep
- reasoning is difficult
- continuation is dangerous

Hard rules prevent catastrophic misrouting.

---

# 8. Recommended Self-Router Prompt

```text
Determine whether this request is SAFE
for fast shallow processing.

Unsafe means:
- debugging
- compiler reasoning
- architecture redesign
- optimization
- continuation-heavy workflows

Return ONLY:
SAFE
or
UNSAFE
```

---

# 9. Recommended Router Settings

Very important.

| Setting | Recommended |
|---|---|
| max_tokens | 4~8 |
| temperature | 0 |
| stream | false |

The goal is:

```text
classification only
```

NOT:

```text
reasoning
```

---

# 10. Important Principle

If the router starts thinking deeply:

```text
the router has already failed
```

The router should be:

- tiny
- deterministic
- fast
- semantic-aware

---

# 11. Another Excellent Alternative
# Embedded Lightweight Classifier

Another very strong option:

```text
embedding model
+
small linear classifier
```

Example embedding models:

```text
sentence-transformers
bge-small
MiniLM
```

Architecture:

```text
prompt embedding
    ↓
tiny classifier
    ↓
35B or 122B
```

---

# 12. Advantages of Embedded Classifier

Benefits:

```text
5~20ms latency possible
```

Very lightweight.

Very cheap.

No extra large inference server needed.

---

# 13. Weaknesses of Embedded Classifiers

Potential weaknesses:

- continuation understanding
- debugging escalation detection
- hidden reasoning complexity
- semantic nuance

Therefore they are best combined with:

```text
Hard Rules
```

---

# 14. Pure Heuristic Routing

Possible example:

```python
if contains("LLVM"):
    return "122b"

if contains("continue"):
    return "122b"

if len(prompt) < 100:
    return "35b"
```

However this is usually insufficient for:

- compiler workflows
- debugging-heavy sessions
- continuation-heavy interactions

---

# 15. Recommended Real-World Strategy

Recommended production flow:

```text
Phase 1:
Hard Rules

Phase 2:
35B Self-Router

Phase 3:
Sticky Escalation
```

---

# 16. Sticky Escalation

Very important.

Recommended logic:

```python
if session.current_model == "122b":
    return "122b"
```

Benefits:

- reasoning continuity
- debugging coherence
- stable continuation behavior

---

# 17. Another Advanced Option
# Speculative Routing

Very interesting architecture.

Flow:

```text
35B starts draft generation
+
router evaluates complexity
```

If complexity detected:

```text
cancel
→ switch to 122B
```

This can reduce latency significantly.

---

# 18. Recommended Final Architecture

For local Mac deployment:

```text
Telegram / Slack / VSCode
           ↓
       Hermes Agent
           ↓
       Smart Router
           ↓
      Session Store
           ↓
 ┌───────────────────┐
 │  Qwen 35B :8000   │
 │  Qwen 122B :8001  │
 └───────────────────┘
```

Routing logic:

```text
Hard Rules
+
35B Self-Routing
+
Sticky Escalation
```

---

# 19. Future Upgrade Path

Later, if needed:

```text
Dedicated tiny router model
```

can be added.

Example:

| Role | Model |
|---|---|
| Router | Qwen2.5-3B |
| Fast | 35B |
| Smart | 122B |

---

# 20. Final Recommendation

For the current local Mac setup:

```text
Hard Rules
+
35B Self-Routing
+
Sticky Continuation Escalation
```

is likely the best balance of:

- latency
- simplicity
- memory efficiency
- semantic awareness
- continuation reliability
- operational stability