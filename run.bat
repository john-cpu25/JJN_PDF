@echo off
cd /d "%~dp0"
python -m pdf_qa %*
if errorlevel 1 pause
