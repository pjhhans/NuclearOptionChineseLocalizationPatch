@echo off
rem 翻译审核台启动器 —— 双击即可。参数原样透传。
rem 例：review.cmd --port 9000      /      review.cmd --selftest
setlocal
set PY=python
where %PY% >nul 2>nul || set PY=py
%PY% "%~dp0review_server.py" %*
if errorlevel 1 pause
endlocal
