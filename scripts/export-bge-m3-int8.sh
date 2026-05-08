#!/usr/bin/env bash
# scripts/export-bge-m3-int8.sh
# Self-export bge-m3 to int8 ONNX from BAAI/bge-m3 source weights.
# Reproducibility path: yields the same int8 ONNX as scripts/download-models.sh's mirror.
# Requires: Python 3.10+, optimum[onnxruntime], onnxruntime, ~6 GB disk + ~4 GB RAM.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EMBED_DIR="${REPO_ROOT}/models/embed"
FP32_DIR="${EMBED_DIR}/bge-m3-fp32"
mkdir -p "${EMBED_DIR}"

# Step 1: FP32 ONNX export via optimum-cli
echo "[export-bge-m3-int8] Step 1: FP32 ONNX export → ${FP32_DIR}/"
optimum-cli export onnx \
  --model BAAI/bge-m3 \
  --task feature-extraction \
  --opset 17 \
  --framework pt \
  "${FP32_DIR}"

# Step 2: int8 dynamic quantization
echo "[export-bge-m3-int8] Step 2: int8 dynamic quantization → ${EMBED_DIR}/bge-m3-int8.onnx"
python3 - <<EOF
from onnxruntime.quantization import quantize_dynamic, QuantType
quantize_dynamic(
    model_input="${FP32_DIR}/model.onnx",
    model_output="${EMBED_DIR}/bge-m3-int8.onnx",
    weight_type=QuantType.QInt8,
    optimize_model=True,
    op_types_to_quantize=["MatMul", "Gather"]
)
print("Done.")
EOF

# Step 3: copy tokenizer (raw SentencePiece binary used by Microsoft.ML.Tokenizers)
cp "${FP32_DIR}/sentencepiece.bpe.model" "${EMBED_DIR}/sentencepiece.bpe.model"

echo "[export-bge-m3-int8] Files ready:"
ls -lh "${EMBED_DIR}/bge-m3-int8.onnx" "${EMBED_DIR}/sentencepiece.bpe.model"
