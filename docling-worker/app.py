import os
# Restrict PyTorch and OMP threads at startup to minimize memory allocations and prevent CPU OOM crashes
os.environ["OMP_NUM_THREADS"] = "1"
os.environ["MKL_NUM_THREADS"] = "1"
os.environ["OPENBLAS_NUM_THREADS"] = "1"
os.environ["VECLIB_MAXIMUM_THREADS"] = "1"
os.environ["NUMEXPR_NUM_THREADS"] = "1"

import json
import logging
from typing import Optional
from fastapi import FastAPI, BackgroundTasks, HTTPException
from pydantic import BaseModel
from docling.document_converter import DocumentConverter

# Setup logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s"
)
logger = logging.getLogger("docling-worker")

app = FastAPI(title="Docling Parser Worker Service")

# Retrieve storage root directory from environment (defaults to shared volume mount)
STORAGE_DIR = os.getenv("STORAGE_DIR", "/app/storage")
DOCUMENTS_DIR = os.path.join(STORAGE_DIR, "documents")
PARSED_DIR = os.path.join(STORAGE_DIR, "parsed")

# Ensure directories exist
os.makedirs(DOCUMENTS_DIR, exist_ok=True)
os.makedirs(PARSED_DIR, exist_ok=True)

logger.info("Initializing DocumentConverter with OCR disabled...")
from docling.datamodel.base_models import InputFormat
converter = DocumentConverter()
pipeline_options = converter.format_to_options[InputFormat.PDF].pipeline_options
pipeline_options.do_ocr = False
pipeline_options.do_table_structure = True
pipeline_options.layout_batch_size = 1
pipeline_options.table_batch_size = 1
pipeline_options.images_scale = 0.8
pipeline_options.accelerator_options.num_threads = 1
pipeline_options.accelerator_options.device = "cpu"
logger.info("DocumentConverter initialized successfully.")

class ParseRequest(BaseModel):
    documentId: str
    fileName: str
    extension: Optional[str] = None

class ParseResponse(BaseModel):
    status: str
    documentId: str

@app.get("/api/health")
def health():
    return {"status": "healthy"}

def run_parsing_task(document_id: str, file_name: str, file_ext: str):
    logger.info(f"Starting background parsing task for document ID: {document_id}")
    
    # Locate the PDF or DOCX file
    # File is saved as {documentId}{file_ext} in documents/
    pdf_path = os.path.join(DOCUMENTS_DIR, f"{document_id}{file_ext}")
    
    # Fallback check if extension was not provided or file doesn't exist
    if not os.path.exists(pdf_path):
        # Scan for any file starting with document_id
        found = False
        for f in os.listdir(DOCUMENTS_DIR):
            if f.startswith(document_id):
                pdf_path = os.path.join(DOCUMENTS_DIR, f)
                found = True
                break
        if not found:
            logger.error(f"Source file for document {document_id} not found in {DOCUMENTS_DIR}")
            return

    logger.info(f"Parsing file: {pdf_path}")
    
    try:
        sections = []
        tables = []
        page_count = 1
        
        # Check if it's a PDF. If so, process page-by-page to prevent std::bad_alloc OOMs
        if file_ext.lower() == ".pdf":
            import pypdfium2 as pdfium
            try:
                pdf = pdfium.PdfDocument(pdf_path)
                page_count = len(pdf)
                pdf.close()
            except Exception as pdf_err:
                logger.error(f"Failed to read page count via pypdfium2: {pdf_err}")
                page_count = 1
                
            logger.info(f"PDF page count: {page_count}. Converting page-by-page...")
            for p in range(1, page_count + 1):
                logger.info(f"Processing page {p}/{page_count}...")
                try:
                    result = converter.convert(pdf_path, page_range=(p, p))
                    doc = result.document
                    
                    for item, level in doc.iterate_items():
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
                                    "pageNumber": p,
                                    "headers": headers,
                                    "rows": rows
                                })
                            except Exception as table_err:
                                logger.error(f"Error extracting table data on page {p}: {table_err}")
                        else:
                            item_type = "paragraph"
                            if "heading" in label_name or "title" in label_name or "header" in label_name:
                                item_type = "heading"
                            
                            text = getattr(item, "text", "")
                            if text and text.strip():
                                sections.append({
                                    "type": item_type,
                                    "text": text.strip(),
                                    "pageNumber": p
                                })
                except Exception as page_ex:
                    logger.error(f"Failed to parse page {p}: {page_ex}", exc_info=True)
        else:
            # For non-PDF (e.g. DOCX), convert directly since OOM is generally not an issue
            result = converter.convert(pdf_path)
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
                    except Exception as table_err:
                        logger.error(f"Error extracting table data: {table_err}")
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

        # Construct final output JSON
        output = {
            "documentId": document_id,
            "pageCount": page_count,
            "sections": sections,
            "tables": tables
        }
        
        # Save output to shared storage
        parsed_json_path = os.path.join(PARSED_DIR, f"{document_id}.json")
        with open(parsed_json_path, "w", encoding="utf-8") as f:
            json.dump(output, f, ensure_ascii=False, indent=2)
            
        logger.info(f"Successfully saved parsed document to {parsed_json_path}")
        
    except Exception as ex:
        logger.error(f"Failed to parse document {document_id}: {ex}", exc_info=True)

@app.post("/api/parse", response_model=ParseResponse, status_code=202)
def parse_document(request: ParseRequest, background_tasks: BackgroundTasks):
    ext = request.extension
    if not ext:
        _, ext = os.path.splitext(request.fileName)
        
    ext = ext.lower()
    
    # Start parsing in the background
    background_tasks.add_task(run_parsing_task, request.documentId, request.fileName, ext)
    
    return ParseResponse(status="Accepted", documentId=request.documentId)
