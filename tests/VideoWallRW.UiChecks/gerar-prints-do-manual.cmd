@echo off
rem Duplo-clique: gera os prints do Manual a partir das telas reais (dados ficticios).
rem Nao abre o programa de verdade, nao liga saidas nem mexe em projetos.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-manual-prints.ps1"
echo.
pause
