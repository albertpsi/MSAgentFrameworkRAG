FROM python:3.10-slim

# Install system dependencies required for machine learning execution (e.g. PyTorch, Docling)
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    libgomp1 \
    curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy requirements and install dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Pre-download docling models during image build step
COPY download_models.py .
RUN python download_models.py

# Copy app code
COPY app.py .

# Configure environment variables
ENV STORAGE_DIR=/app/storage
ENV PORT=8000

EXPOSE 8000

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8000"]
