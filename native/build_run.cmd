@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
cmake -S C:\Users\User\Projects\FluxCord\native -B C:\Users\User\Projects\FluxCord\native\build -G Ninja -DCMAKE_BUILD_TYPE=Release || exit /b 1
cmake --build C:\Users\User\Projects\FluxCord\native\build -j 8 || exit /b 1
C:\Users\User\Projects\FluxCord\native\build\FluxCord.Tests.exe || exit /b 1

