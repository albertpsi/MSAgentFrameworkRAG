# FastAPI Docling GPU Service
# Note for Google Colab: Save this code by putting '%%writefile app.py' as the first line of the notebook cell.
# Run with: !python app.py

# Install commands used in Colab (commented out here to prevent python syntax errors):
# os.system("pip install docling fastapi uvicorn pyngrok pydantic requests pandas pypdfium2 python-multipart onnxruntime")

!pip install fastapi
!pip install uvicorn
!pip install docling
!pip install pydantic
!pip install requests
!pip install pandas
!pip install pypdfium2
!pip install onnxruntime
!pip install docling fastapi uvicorn pyngrok pydantic requests pandas pypdfium2 python-multipart

import os
from fastapi import FastAPI, UploadFile, File, HTTPException
from docling.document_converter import DocumentConverter
from docling.datamodel.base_models import InputFormat

app = FastAPI(title="Google Colab Docling GPU Service")

print("Initializing DocumentConverter on GPU (CUDA)...")
converter = DocumentConverter()
pipeline_options = converter.format_to_options[InputFormat.PDF].pipeline_options
pipeline_options.do_ocr = True
pipeline_options.do_table_structure = True
pipeline_options.layout_batch_size = 8  # Batch size optimized for GPU
pipeline_options.table_batch_size = 8
pipeline_options.images_scale = 1.0     # Full resolution
pipeline_options.accelerator_options.device = "cuda"  # Run on GPU!
print("DocumentConverter initialized successfully on GPU.")

@app.get("/")
def read_root():
    return {"status": "healthy", "service": "Google Colab Docling GPU Service"}

@app.post("/parse")
async def parse_uploaded_file(file: UploadFile = File(...)):
    # Verify file extension
    ext = os.path.splitext(file.filename)[1].lower()
    if ext not in [".pdf", ".docx"]:
        raise HTTPException(status_code=400, detail="Only PDF and DOCX files are supported.")
    
    # Save uploaded file temporarily in Colab/Service
    temp_path = f"temp_upload{ext}"
    with open(temp_path, "wb") as buffer:
        buffer.write(await file.read())
        
    try:
        sections = []
        tables = []
        
        # GPU parsing: No page-by-page loop needed
        result = converter.convert(temp_path)
        doc = result.document
        page_count = getattr(doc, "page_count", 1) or 1
        
        for item, level in doc.iterate_items():
            page_number = 1
            if item.prov and len(item.prov) > 0:
                page_number = item.prov[0].page_no
                
            label_name = item.label.name if hasattr(item.label, "name") else str(item.label)
            label_name = label_name.lower()
            
            if "table" in label_name:
                try:
                    df = item.export_to_dataframe(doc)
                    headers = [str(col) for col in df.columns]
                    rows = []
                    for _, row in df.iterrows():
                        rows.append([str(val) if val is not None else "" for val in row.values])
                    
                    tables.append({
                        "pageNumber": page_number,
                        "headers": headers,
                        "rows": rows
                    })
                except Exception:
                    pass
            else:
                item_type = "paragraph"
                if "heading" in label_name or "title" in label_name or "header" in label_name:
                    item_type = "heading"
                
                text = getattr(item, "text", "")
                if text and text.strip():
                    sections.append({
                        "type": item_type,
                        "text": text.strip(),
                        "pageNumber": page_number
                    })
        
        # Clean up temp file
        os.remove(temp_path)
        
        return {
            "pageCount": page_count,
            "sections": sections,
            "tables": tables
        }
        
    except Exception as ex:
        if os.path.exists(temp_path):
            os.remove(temp_path)
        raise HTTPException(status_code=500, detail=f"Parsing failed: {str(ex)}")

if __name__ == "__main__":
    import uvicorn
    from pyngrok import ngrok

    # Expose server using Ngrok (reads from environment variable or direct paste)
    NGROK_TOKEN = os.environ.get("NGROK_TOKEN", "YOUR_NGROK_AUTHTOKEN_HERE")
    if NGROK_TOKEN and NGROK_TOKEN != "YOUR_NGROK_AUTHTOKEN_HERE":
        ngrok.set_auth_token(NGROK_TOKEN)

    tunnel = ngrok.connect("127.0.0.1:8000")
    print("\n" + "="*50)
    print(f"PUBLIC WORKER URL: {tunnel.public_url}")
    print("Copy this URL and paste it in your test script API_URL")
    print("="*50 + "\n")

    # Start uvicorn in the foreground (blocks and keeps the process and tunnel alive)
    uvicorn.run(app, host="127.0.0.1", port=8000)
