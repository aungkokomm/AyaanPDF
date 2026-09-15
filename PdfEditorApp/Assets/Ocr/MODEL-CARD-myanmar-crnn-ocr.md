---
license: apache-2.0
language:
- my
tags:
- ocr
- crnn
- ctc
- myanmar
- onnx
library_name: onnxruntime
pipeline_tag: image-to-text
---

# Myanmar CRNN OCR Model

Lightweight CRNN + CTC model for recognising text lines from scanned Myanmar documents.
Used by [mmpdfkit](https://github.com/kaungsithu/mmpdfkit) to convert scanned Myanmar PDFs to Unicode Markdown.

## Model Details

| Property | Value |
|---|---|
| Architecture | CRNN (Conv + BiLSTM + CTC) |
| Input | Grayscale line crop, height=32px, variable width |
| Output | Myanmar / English Unicode text |
| Vocabulary size | 272 classes (Myanmar Unicode U+1000–U+109F, Extended U+AA60–U+AA7F, English, punctuation) |
| Format | INT8 quantised ONNX (~8.9 MB) |
| Training data | 7.6M synthetic + real scanned Myanmar document line images |
| Best val CER | **4.17%** (epoch 50) |

## Files

| File | Size | Description |
|---|---|---|
| `myanmar-crnn-ocr.onnx` | ~8.9 MB | INT8 quantised — production model used by mmpdfkit |

## Usage with mmpdfkit

**pip**
```bash
pip install mmpdfkit[ocr]
mmpdfkit your_scanned_document.pdf
```

**uv** (recommended — faster installs, isolated environment)
```bash
uv tool install mmpdfkit[ocr]
mmpdfkit your_scanned_document.pdf
```

The model is downloaded automatically on first OCR run and cached at `~/.cache/mmpdfkit/`.

## Direct ONNX Usage

```python
import onnxruntime as ort
import numpy as np
import cv2

# Load model
session = ort.InferenceSession("myanmar-crnn-ocr.onnx", providers=["CPUExecutionProvider"])

# Prepare a grayscale line crop (H, W) uint8
crop = cv2.imread("line.png", cv2.IMREAD_GRAYSCALE)
h, w = crop.shape
if h != 32:
    crop = cv2.resize(crop, (max(1, round(w * 32 / h)), 32), interpolation=cv2.INTER_LANCZOS4)
crop = cv2.resize(crop, (crop.shape[1] * 2, 32), interpolation=cv2.INTER_LANCZOS4)

x = (crop.astype(np.float32) / 255.0)[np.newaxis, np.newaxis]  # (1, 1, 32, W)
logits = session.run(["output"], {"input": x})[0]               # (1, W', 272)
indices = logits[0].argmax(axis=1).tolist()
# Apply greedy CTC decode (collapse repeats, remove blank index 22)
```

## Preprocessing

- Binarise page with Otsu threshold
- Deskew up to ~5° using `cv2.minAreaRect`
- Horizontal dilation to merge characters within a line
- Row projection to segment line bands
- Each line crop resized to height=32, then width doubled before inference

## Training

Trained with CTC loss on 7.6M Myanmar document line images (synthetic renders + real scans).
Optimiser: Adam, 50 epochs, best checkpoint selected by validation CER.
