@echo off
cd /d "%~dp0"
python -m pdf_qa_web %*
if errorlevel 1 pause
