#!/usr/bin/env bash
# scripts/download-models.sh
# One-time setup: download bge-m3 int8 ONNX + tokenizer from HuggingFace into models/embed/.
# Operator runs this once before first `dotnet run` of the router.
# The router exits with a clear error if these files are missing at startup.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EMBED_DIR="${REPO_ROOT}/models/embed"
mkdir -p "${EMBED_DIR}"

echo "[download-models] Fetching Teradata/bge-m3 (int8 ONNX, ~542 MB) into ${EMBED_DIR}/"

# Requires huggingface-cli; install with: pip install -U "huggingface_hub[cli]"
if ! command -v huggingface-cli > /dev/null 2>&1; then
  echo "ERROR: huggingface-cli not found. Install with: pip install -U 'huggingface_hub[cli]'" >&2
  exit 1
fi

# Teradata/bge-m3 ships int8 model.onnx + sentencepiece.bpe.model + tokenizer.json
huggingface-cli download Teradata/bge-m3 \
  --local-dir "${EMBED_DIR}" \
  --include "model.onnx" "sentencepiece.bpe.model" "tokenizer.json"

# Convention: appsettings.json points at these exact filenames.
# Rename / symlink if the repo's filename differs.
if [ -f "${EMBED_DIR}/model.onnx" ] && [ ! -f "${EMBED_DIR}/bge-m3-int8.onnx" ]; then
  mv "${EMBED_DIR}/model.onnx" "${EMBED_DIR}/bge-m3-int8.onnx"
fi

echo "[download-models] Done. Files in ${EMBED_DIR}/:"
ls -lh "${EMBED_DIR}/"
echo "[download-models] Run: dotnet run --project src/SmartRouter.Cli"
