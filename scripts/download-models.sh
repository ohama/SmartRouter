#!/usr/bin/env bash
# scripts/download-models.sh
# One-time setup: download bge-m3 int8 ONNX + tokenizer from HuggingFace into models/embed/.
# Operator runs this once before first `dotnet run` of the router.
# The router exits with a clear error if these files are missing at startup.
#
# Issue #5 fix (2026-05-09): the previous script used `huggingface-cli`, which is
# deprecated as of huggingface_hub 1.x in favor of the `hf` CLI. It also pointed at
# `model.onnx` at the repo root, but Teradata/bge-m3 actually ships the int8 file at
# `onnx/model_int8.onnx`. Both fixes are in this revision.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EMBED_DIR="${REPO_ROOT}/models/embed"
mkdir -p "${EMBED_DIR}"

echo "[download-models] Fetching Teradata/bge-m3 (int8 ONNX, ~542 MB) into ${EMBED_DIR}/"

# huggingface_hub 1.x ships the `hf` CLI; older `huggingface-cli` is deprecated.
if ! command -v hf > /dev/null 2>&1; then
  echo "ERROR: 'hf' CLI not found." >&2
  echo "       Install with: pip install -U huggingface_hub" >&2
  echo "       (or upgrade if you have an old huggingface-cli installed:" >&2
  echo "         pip install -U --upgrade-strategy=eager huggingface_hub)" >&2
  exit 1
fi

# Teradata/bge-m3 layout:
#   onnx/model_int8.onnx          ← what we want (rename to bge-m3-int8.onnx)
#   onnx/model_uint8.onnx         (alternative quantization; not used)
#   sentencepiece.bpe.model       ← tokenizer model
#   tokenizer.json                (also useful; some Microsoft.ML.Tokenizers paths use it)
hf download Teradata/bge-m3 onnx/model_int8.onnx     --local-dir "${EMBED_DIR}"
hf download Teradata/bge-m3 sentencepiece.bpe.model  --local-dir "${EMBED_DIR}"
hf download Teradata/bge-m3 tokenizer.json           --local-dir "${EMBED_DIR}"

# Flatten + rename to the path appsettings.json expects.
if [ -f "${EMBED_DIR}/onnx/model_int8.onnx" ]; then
  mv "${EMBED_DIR}/onnx/model_int8.onnx" "${EMBED_DIR}/bge-m3-int8.onnx"
  rmdir "${EMBED_DIR}/onnx" 2>/dev/null || true
fi

echo "[download-models] Done. Files in ${EMBED_DIR}/:"
ls -lh "${EMBED_DIR}/"
echo "[download-models] Run: dotnet run --project src/SmartRouter.Cli"
