import os
# Restrict PyTorch and OMP threads at startup to minimize memory allocations and prevent CPU OOM crashes
os.environ["OMP_NUM_THREADS"] = "1"
os.environ["MKL_NUM_THREADS"] = "1"
os.environ["OPENBLAS_NUM_THREADS"] = "1"
os.environ["VECLIB_MAXIMUM_THREADS"] = "1"
os.environ["NUMEXPR_NUM_THREADS"] = "1"

import sys

def main():
    print("Pre-downloading Docling models...", flush=True)
    try:
        from docling.document_converter import DocumentConverter
        # Instantiating the converter triggers download and validation of default models
        converter = DocumentConverter()
        print("Docling models successfully downloaded and cached.", flush=True)
    except Exception as e:
        print(f"Error pre-downloading Docling models: {e}", file=sys.stderr, flush=True)
        sys.exit(1)

if __name__ == "__main__":
    main()
