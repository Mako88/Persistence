@echo off
rem The embedding server for Claude's Persistence recall: bge-small on the CPU, never the card
rem (CUDA hidden), port 8081. Started by hook.py when recall finds it down.
set CUDA_VISIBLE_DEVICES=
set CUDA_VISIBLE_DEVICES=-1
D:\tools\llama.cpp-src\build\bin\Release\llama-server.exe -m D:\models\embed\bge-small-en-v1.5-q8_0.gguf --embedding --pooling cls -ngl 0 -c 512 -b 512 -ub 512 --parallel 1 --threads 2 --host 127.0.0.1 --port 8081 > C:\Users\John\.claude\persistence\embedding-server.log 2>&1
