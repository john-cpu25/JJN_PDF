FROM python:3.11-slim

WORKDIR /app

# Install curl for healthcheck if needed
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    && rm -rf /var/lib/apt/lists/*

COPY requirements-web.txt .
RUN pip install --no-cache-dir -r requirements-web.txt

# Copy backend logic and web server
COPY pdf_qa/ ./pdf_qa/
COPY pdf_qa_web/ ./pdf_qa_web/

ENV HOST=0.0.0.0
ENV PORT=8000

EXPOSE 8000

CMD ["python", "-m", "pdf_qa_web", "--host", "0.0.0.0", "--port", "8000", "--no-browser"]
